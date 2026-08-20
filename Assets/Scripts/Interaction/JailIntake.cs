using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 감옥 출입구 — <b>누가 감옥에 들어가고, 어디 세우고, 언제 나오나</b>를 담당한다. (#492/#537)
///
/// <see cref="JailZone"/>과 역할이 갈린다: 저쪽은 대장(수용 인원·현상금·정산 레코드·배치 지점 소유),
/// 이쪽은 출입구다. 한 클래스에 두면 정산 책임과 출입 책임이 섞이고 크기도 감당이 안 된다.
///
/// <b>폴링이 사라졌다</b> (#537). 예전에는 매 틱 NPC 전부를 훑으며 세 규칙(게이트 판정 → Jail 통행 →
/// 착석)을 집행했다. 감옥이 격리 공간이 되면서 셋 다 근거를 잃었다:
///
///  · <b>게이트 판정</b> → 문 앞 E가 트리거다. 명시적 입력이라 "언제 판정됐는지"를 폴링으로 추적할
///    이유가 없고, 통과당 1회 중복 가드(<c>m_judgedThisPass</c>)도 함께 사라진다.
///  · <b>Jail NavMesh 통행</b> → 감옥이 도시와 이어지지 않은 별도 NavMesh 섬이라 걸어 들어올 경로
///    자체가 없다. 영역 마스크로 시민을 막을 일이 없어졌다 (#415가 하던 일).
///  · <b>착석</b> → 판정과 동시에 순간이동해 배치된다. "문턱은 넘었는데 안 앉힌 상태"라는 중간이 없다.
///
/// 그래서 이 클래스는 이제 <b>서버에서 불릴 때만 도는 서비스</b>다 — 문(<see cref="JailDoor"/>)과
/// 반출 요청(<see cref="PlayerEscortCommands"/>)이 호출한다.
///
/// <b>계상 시점이 판정 즉시로 앞당겨졌다.</b> 예전에는 "문턱만 넘고 안 앉히면 0원"이 직접 넣게 만드는
/// 강제력이었는데(#492), 앉히는 단계가 없어져 그 창이 닫혔다. 강제력은 <b>문 앞까지 끌고 오는 것</b>으로
/// 줄어들고, GDD 9-2의 "이송 중 라운드 종료 시 보상 없음"은 그대로 성립한다 — 문에 닿기 전에 라운드가
/// 끝나면 여전히 0원이다.
///
/// 장소 오브젝트라 App 파사드에 등록하지 않는다 — JailLock·JailZone과 같은 관례로 씬 탐색을 쓴다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class JailIntake : CommonManagerBase
{
    [Header("감옥 (비우면 같은 오브젝트·부모에서 자동 탐색)")]
    [SerializeField] private JailZone m_jailZone;

    [Tooltip(
        "판정 버튼이 신병으로 인정하는 거리(m) — 손이 빈 사람이 눌렀을 때만 쓴다. 이 안의 확보 상태"
            + "(놓아둔 Captured, 남이 끌고 온 Escorted, 내려놓은 시체)를 함께 판정하되, 아무도 손대지 "
            + "않은 대상은 빠진다. 줄을 쥐고 있으면 밧줄에 걸린 대상만 판정한다(#637). "
            + "밧줄 길이(1.6m)보다 넉넉히 둘 것"
    )]
    [SerializeField] private float m_admitReach = 4f;

    // 판정 대상을 모으는 임시 버퍼 — E 입력마다 새로 할당하지 않게 재사용한다. 서버(또는 오프라인) 전용.
    private readonly List<NpcController> m_admitBuffer = new List<NpcController>();

    // 판정 자체가 불가능했던 대상(신원·경범죄 마커 둘 다 없음) — 누를 때마다 경고가 폭주하지 않게 기억한다.
    private readonly HashSet<NpcController> m_unjudgeable = new HashSet<NpcController>();

    /// <summary>감옥 — 문이 배치 지점·퇴장 지점을 물어볼 때 쓴다. 미배선이면 null.</summary>
    public JailZone Zone => m_jailZone;

    protected override void Awake()
    {
        base.Awake(); // App.Game.JailIntake 등록

        // 감옥은 같은 오브젝트에 두는 것이 기본 — 인스펙터로 따로 지정할 수도 있다
        if (m_jailZone == null)
            m_jailZone = GetComponentInParent<JailZone>();

        if (m_jailZone == null)
            Debug.LogWarning("JailIntake: 감옥(JailZone)을 찾지 못했다 — 수용이 동작하지 않는다", this);
    }

    // 판정·배치는 서버 권위 — NetworkBehaviour가 아니므로 직접 게이트한다 (CustodyRouter와 같은 패턴)
    private static bool HasServerAuthority =>
        NetworkManager.Singleton == null
        || !NetworkManager.Singleton.IsListening
        || NetworkManager.Singleton.IsServer;

    // ---- 수감 (문 앞 E) ----

    /// <summary>
    /// 수감 버튼 처리 — <paramref name="interactor"/>가 확보 중인 신병을 <b>줄을 걷고 일으켜 세운 뒤</b>
    /// 판정하고, 범죄자만 감옥 안으로 순간이동시켜 계상한다. 서버(또는 오프라인) 전용. (#537)
    ///
    /// 돌려주는 값은 <b>실제로 판정을 시도한 대상 수</b>이지 수감된 수가 아니다 — 판정이 일어나기
    /// 뒤에 나오므로 이 시점에는 결과를 모른다. 부르는 쪽(<see cref="JailIntakeButton"/>)이 "확보한
    /// 신병이 없다"만 가르는 데 쓴다. <c>m_unjudgeable</c>에 걸려 통째로 건너뛴 시도는 세지 않는다 —
    /// 안 그러면 전부 건너뛴 누름도 "성공"으로 잡혀 그 안내가 나가지 않는다 (#705).
    ///
    /// <b>확보의 기준은 갈래 둘이고, ①이 있으면 ②는 돌지 않는다</b> (#637) — ① 이 사람의 밧줄에
    /// 걸린 대상 전부, ② 버튼 앞 <see cref="m_admitReach"/> 안에서 <see cref="IsAdmittableState"/>이면서
    /// <see cref="NpcCustody.IsSecuredByAnyone"/>인 대상. 줄을 쥔 것이 곧 "이것을 넣겠다"는 선택이므로 그 위에
    /// 반경 스캔을 얹지 않는다 — 옆에 놓아둔 대상이 함께 검거되던 사고가 그 경로였다.
    ///
    /// ②는 손이 빈 사람 몫이다: 밧줄을 풀어 세워 둔 뒤 누르는 조작, <b>남이 끌고 온 신병을 대신
    /// 넣어 주는</b> 협동, 그리고 여럿을 세워 뒀으면 <b>한 번에 전부 판정</b> (팀 확정 2026-08-06).
    ///
    /// <b>시체도 산 신병과 같은 규칙을 탄다</b> (#571) — 죽은 대상은 죽는 순간이 아니라 여기서
    /// 계상되고, 판정·배치가 통째로 다른 갈래라 <see cref="ServerAdmitCorpse"/>가 따로 받는다.
    ///
    /// <b>시체는 ①로만 받는다</b> (#571/#637) — 죽은 대상은 죽는 순간이 아니라 여기서 계상되고,
    /// 판정·배치가 통째로 다른 갈래라 <see cref="ServerAdmitCorpse"/>가 따로 받는다.
    ///
    /// 오검거는 감옥에 들이지 않고 그 자리에서 놓는다 — <see cref="WrongfulArrestPenalty"/>가
    /// Detained로 가져가 페널티를 굴린다(#101/#277). 감옥이 격리 공간이 된 뒤로는 "안에서 확정된
    /// 오검거가 통행을 잃고 굳는다"는 옛 사고(#492)가 애초에 생길 수 없다.
    /// </summary>
    public int ServerAdmitHeldBy(GameObject interactor)
    {
        if (!HasServerAuthority || interactor == null || m_jailZone == null)
            return 0;

        // 라운드 진행 중에만 받는다. ArrestJudge도 같은 게이트를 걸지만 여기서 먼저 끊어야 한다 —
        // 저쪽이 null을 돌려주면 아래 루프가 그 대상을 m_unjudgeable에 영구 등록해, 라운드가
        // 시작된 뒤에도 판정되지 않는 NPC가 된다.
        RoundManager round = App.Game.Round;
        if (round != null && round.Phase != RoundPhase.InProgress)
            return 0;

        CollectHeldBy(interactor);
        if (m_admitBuffer.Count == 0)
            return 0;

        if (App.Game.ArrestJudge == null)
        {
            Debug.LogWarning("JailIntake: ArrestJudge가 없어 판정할 수 없다", this);
            return 0;
        }

        // 누른 사람을 판정에 넘긴다 — 밧줄을 안 쥐고 있어도 인계자로 잡혀야 판정 배너가 이 사람에게 뜨고
        // 오검거 페널티도 걸린다 (#537, ArrestJudge.Judge의 presser 주석).
        PlayerEscorter presser = interactor.GetComponent<PlayerEscorter>();

        // 확보한 수를 먼저 센다 — 판정은 일어나기(약 0.6초) 뒤에 나므로 결과를 기다려 셀 수 없다.
        // 버튼이 이 수로 "확보한 신병이 없다"만 가르면 되고, 판정 결과는 각자 배너로 나간다.
        int held = m_admitBuffer.Count;
        int handled = 0;

        ArrestJudge judge = App.Game.ArrestJudge;

        for (int i = 0; i < held; i++)
        {
            NpcController npc = m_admitBuffer[i];
            if (npc == null || m_unjudgeable.Contains(npc))
                continue;

            // 시체는 갈래가 통째로 다르다 (#571) — 판정 이벤트도, 기상도, 커스터디 전이도 없다.
            if (npc.Death.IsDead)
            {
                // '이미 계상됨'으로 실패한 것이면 기억하지 않는다 (#705) — 반출로 답이 달라질 수 있다.
                bool wasDelivered = npc.Custody.IsDelivered;
                if (ServerAdmitCorpse(npc, presser))
                {
                    handled++;
                }
                else if (!wasDelivered)
                {
                    m_unjudgeable.Add(npc);
                }
                else
                {
                    Debug.Log($"[감옥] 수감 버튼 — 이미 계상된 시체: {npc.name}");
                }
                continue;
            }

            handled++;

            // <b>판정이 먼저다 — 줄이 걸려 있는 동안에.</b> ArrestJudge가 인계자를 그 순간의 밧줄에서
            // 뽑아 결과(ArrestResult.DeliveredBy)에 실으므로, 줄을 먼저 걷으면 목록이 비어 <b>판정 배너도
            // 오검거 페널티 대상도 사라진다</b>. 저쪽이 오검거일 때 끌기를 직접 푸는 것도 같은 이유로
            // 판정 안에서 일어나야 한다(그쪽 주석 참고).
            ArrestResult? result = judge.Judge(npc, presser);
            if (result == null)
            {
                // 신원도 경범죄 마커도 없는 대상 — 다시 물어도 답이 같으므로 한 번만 시도한다
                m_unjudgeable.Add(npc);
                continue;
            }

            // 인계 몫(#484)은 판정이 확정한 인계자를 그대로 쓴다 — 배너·페널티와 같은 목록이라
            // "누가 넣었나"의 답이 셋으로 갈리지 않는다.
            ulong[] deliverers = ToClientIds(result.Value.DeliveredBy);

            // 오검거 — 감옥에 들이지 않는다. 행선지(원한 구역 수용)는 WrongfulArrestPenalty가 같은
            // 판정 이벤트를 이미 받아 정했고, 그 매니저가 없는 씬에서는 CustodyRouter가 배회로 돌려보낸다.
            // 여기서는 남은 줄을 걷고 일으켜 세우기만 한다 — 누운 채 끌려가는 그림을 없앤다.
            if (result.Value.Verdict == ArrestVerdict.WrongfulArrest)
            {
                npc.Custody.SetJailExtracted(false); // 반출했던 대상이 오검거로 뒤집힌 경우 표식을 걷어낸다 (#517)
                PlayerEscorter.ReleaseAllTethersOn(npc, null);
                Debug.Log($"[감옥] 오검거 — 감옥에 들이지 않고 문 앞에서 놓는다: {npc.name}");
                continue;
            }

            // 기절 해제는 여기서 하지 않는다 — ServerStandUpThen이 예약 직전에 스스로 걷는다.
            // 검거는 무력화가 전제라(NpcStateRules.CanRopeBind) 여기 오는 대상은 거의 항상 오버레이를
            // 달고 있고, 그대로 두면 수감 콜백이 예약 취소와 함께 버려진다 — 위 오검거 분기도 같은 위험을
            // 안고 있었다. 두 경로가 같은 해제를 타도록 대상 쪽 한 곳으로 모았다.

            // 수감 — <b>줄을 걷고, 옮기고, 감옥 안에서 일어난다.</b> 셋 다 이 프레임 안에서 끝난다.
            //
            // 순서가 중요하다. 예전에는 일어나기가 <b>끝난 뒤</b>에 옮겼는데, 그러면 문 앞에서 일어나는
            // 모션이 마무리되는 순간 몸이 사라져 "일어나다 말고 증발한다"로 보였다.
            //
            // 그렇다고 옮기기를 앞세울 수도 없다: 밧줄이 걸린 동안은 NavMeshAgent가 꺼져 있어
            // (PlayerEscorter.StartRopeDrag) 워프가 조용히 실패한다 — 끌기 해제가 에이전트를 되살린다.
            //
            // 그래서 줄부터 걷고(기상 모션이 여기서 예약된다) 곧바로 옮긴다. 둘 사이에 프레임 경계가
            // 없으므로 화면에 그려지는 것은 <b>감옥 안에서 일어나는 모습</b> 하나뿐이다.
            int bounty = result.Value.Reward;
            PlayerEscorter.ReleaseAllTethersOn(npc, null);
            ServerPlaceInJail(npc, bounty, deliverers);
        }

        m_admitBuffer.Clear();
        return handled;
    }

    /// <summary>
    /// 대상을 감옥 안 배치 지점으로 순간이동시키고 계상한다 — 서버(또는 오프라인) 전용. (#537)
    /// 밧줄은 이 앞 단계에서 이미 걷혔고(<see cref="PlayerEscorter.ReleaseAllTethersOn"/>) 몸도 서 있다.
    /// </summary>
    private void ServerPlaceInJail(NpcController npc, int bounty, ulong[] deliverers)
    {
        Transform spot = m_jailZone.ReservePlacement(npc);
        npc.Custody.SendToJail(spot);
        m_jailZone.Admit(npc, bounty, deliverers);

        Debug.Log($"[감옥] 수감 — {npc.name}을(를) {spot.name}에 배치했다 (현상금 {bounty}원)");
    }

    // ---- 시체 수감 (#571) ----

    /// <summary>
    /// 시체 하나를 판정해 감옥 안에 눕힌다 — 서버(또는 오프라인) 전용. (#571)
    ///
    /// <b>산 신병 경로와 셋이 갈린다.</b> ① 판정이 <see cref="ArrestJudge.JudgeCorpse"/>다
    /// (<c>OnArrestJudged</c>를 발행하지 않는다 — 구독자가 전부 상태 전이라 시체에 성립하지 않는다),
    /// ② 줄을 걷을 때 일으켜 세우지 않는다, ③ 배치가 순간이동이 아니라 <b>몸을 통째로 옮기는</b> 것이다.
    ///
    /// <b>오검거 시체도 여기로 온다</b> — 예전에는 죽는 순간 이미 세고 표식을 세워 판정이 null로
    /// 끊겼는데, 그래서 <b>버튼을 눌러도 아무 결과가 안 나왔다</b>. 이제 산 신병과 같은 문을 지난다:
    /// 판정은 정상적으로 나고(배너·집계), 다만 <b>감옥에는 들이지 않는다</b>.
    /// </summary>
    /// <returns>판정이 성립했으면 참 — 거짓이면 다시 눌러도 결과가 같은 시체다(신원 없음·이미 계상됨).</returns>
    private bool ServerAdmitCorpse(NpcController npc, PlayerEscorter presser)
    {
        ArrestResult? result = App.Game.ArrestJudge?.JudgeCorpse(npc, presser);
        if (result == null)
            return false;

        // 오검거 시체 — 감옥에 들이지 않는다. 산 신병의 오검거와 같은 규칙이고(위 ServerAdmitHeldBy),
        // 되돌릴 수 없는 쪽이라 더 지켜야 한다: 한 번 눕히면 시체를 꺼내는 경로는 밧줄 반출뿐이다.
        // 원장(RecordDeceased)에도 올리지 않으므로 정산에 0원짜리 항목이 남지 않고, 산 수감자용
        // 자리도 축내지 않는다. 오검거 집계는 판정이 이미 했다(ArrestJudge.JudgeCorpse).
        //
        // 줄은 걷는다 — 안 걷으면 관절이 남아 운반자를 계속 따라다닌다(아래 수감 경로와 같은 사정).
        if (result.Value.Verdict == ArrestVerdict.WrongfulArrest)
        {
            PlayerEscorter.ReleaseAllTethersOnCorpse(npc);
            Debug.Log($"[감옥] 오검거 시체 — 감옥에 들이지 않고 문 앞에 둔다: {npc.name}");
            return true;
        }

        ulong[] deliverers = ToClientIds(result.Value.DeliveredBy);
        int bounty = result.Value.Reward;

        // 줄부터 걷는다 — 관절 밧줄이 걸린 채 옮기면 운반자의 손과 감옥 사이에 관절이 늘어난 채로
        // 남아, 물리가 그 장력으로 시체를 문 밖으로 도로 끌어낸다.
        PlayerEscorter.ReleaseAllTethersOnCorpse(npc);

        // 배치 지점(ReservePlacement)을 쓰지 않는다 — 그 목록은 <b>산 수감자가 설 자리</b>의 정원이고,
        // 시체가 한 칸씩 차지하면 자리를 영영 물린다(반납은 ReleaseInmate 경로뿐인데 시체는 거기 없다).
        // 시체는 아무것도 막지 않으므로(래그돌 레이어는 지형하고만 충돌한다) 방 안 아무 데나 누우면 된다.
        //
        // ⚠ <c>RandomPointInRoom</c>이 아니라 <c>RandomRestPointInRoom</c>이다 — 저쪽은 부피 밑면이라
        // 바닥 속이고, 시체에는 스냅해 줄 에이전트가 없다(그쪽 주석).
        Vector3 spot = m_jailZone.RandomRestPointInRoom();
        npc.Custody.SendCorpseToJail(spot);

        // 점유(m_inmates)가 아니라 원장(m_records)에만 올린다 — 시체는 유치장 표지판이 세는 인원도,
        // 탈옥이 풀어 줄 수감자도 아니다 (JailZone.RecordDeceased 주석).
        m_jailZone.RecordDeceased(npc, bounty, deliverers);

        Debug.Log($"[감옥] 시체 수감 — {npc.name}을(를) 감옥 안에 눕혔다 (현상금 {bounty}원)");
        return true;
    }

    // 이 플레이어가 확보 중인 대상을 모은다 — 줄을 쥐고 있으면 거기 걸린 전부, 손이 비었으면 문 앞에 놓아둔 신병.
    private void CollectHeldBy(GameObject interactor)
    {
        m_admitBuffer.Clear();

        PlayerEscorter escorter = interactor.GetComponent<PlayerEscorter>();
        if (escorter != null)
        {
            for (int i = 0; i < escorter.TetheredCount; i++)
            {
                NpcController roped = escorter.GetTetheredNpc(i);
                if (roped != null && !m_admitBuffer.Contains(roped))
                    m_admitBuffer.Add(roped);
            }
        }

        // <b>줄을 쥐고 있으면 거기서 끝이다</b> (#637) — 밧줄이 곧 "이것을 넣겠다"는 명시적 선택이라,
        // 반경 스캔이 그 위에 다른 대상을 얹으면 고른 적 없는 것이 함께 들어간다. 반경 갈래는 손이
        // 빈 사람 몫이다: 풀어 세워 둔 신병을 누르거나 남이 끌고 온 신병을 대신 넣어 주는 조작.
        if (m_admitBuffer.Count > 0)
            return;

        // 버튼 앞의 신병 — 기준은 <b>버튼</b>이 아니라 누른 사람이다. 버튼에서 재려면 버튼이 여럿일 때
        // 어느 것인지를 또 물어야 하는데, 사거리는 이미 PlayerInteractor가 걸러 줬으므로 사람 기준이면 충분하다.
        //
        // <b>연행 중(Escorted)도 받는다</b> — 내 줄에 걸린 대상은 위에서 이미 잡히지만, <b>남이 끌고 온</b>
        // 신병이나 반출돼 따라오는 대상은 이쪽으로만 들어온다. 문 앞에서 대신 넣어 주는 협동이 막히면
        // "끌고 온 사람이 직접 눌러야 한다"는 규칙이 새로 생기는데, 그럴 이유가 없다
        // (인계 몫은 밧줄 보유자 전원에게 가므로 가로채기도 아니다).
        Vector3 origin = interactor.transform.position;
        float sqrReach = m_admitReach * m_admitReach;

        NpcController[] npcs = FindObjectsByType<NpcController>(FindObjectsSortMode.None);
        for (int i = 0; i < npcs.Length; i++)
        {
            NpcController npc = npcs[i];
            if (npc == null)
                continue;
            if (!IsAdmittableState(npc.CurrentState))
                continue;
            if (npc.Custody == null || !npc.Custody.IsSecuredByAnyone)
                continue;
            if ((npc.transform.position - origin).sqrMagnitude > sqrReach)
                continue;
            if (!m_admitBuffer.Contains(npc))
                m_admitBuffer.Add(npc);
        }
    }

    /// <summary>
    /// 버튼 앞에 놓아둔 것만으로 판정 대상이 되는 상태인가 — 밧줄로 끌고 온 대상은 이 판정을 타지 않는다.
    ///
    /// <see cref="NpcState.Dead"/>가 들어 있는 이유는 산 신병과 같다 (#571): 문 앞에 <b>내려놓고</b>
    /// 누르는 조작이 자연스럽고, 남이 끌고 온 시체를 대신 넣어 주는 것도 되어야 한다.
    /// 시체 밧줄은 E로 내려놓으면 관절이 풀리므로(순수 운반이라 '놓아둔 Captured' 같은 중간이 없다)
    /// 이 갈래가 없으면 <b>줄을 쥔 채로만</b> 넣을 수 있게 된다.
    ///
    /// <b>휩쓸림은 소유 조건이 막는다</b> (#637) — 끌고 와서 내려놓은 시체는
    /// <see cref="NpcCustody.WasSecuredByPlayer"/>가 남지만, 길에 사살해 둔 시체는 셋 다 비어
    /// <see cref="NpcCustody.IsSecuredByAnyone"/>에서 걸러진다. 사고가 난 것이 후자였다.
    /// </summary>
    private static bool IsAdmittableState(NpcState state) =>
        state is NpcState.Captured or NpcState.Escorted or NpcState.Dead;

    // ---- 조준 안내용 조회 (#664) ----

    // 프레임당 한 번만 훑는다 — 조준 중 문구·사유가 각각 물어 오므로 결과를 그 프레임 안에서 나눠 쓴다.
    private GameObject m_custodyProbeInteractor;
    private int m_custodyProbeFrame = -1;
    private bool m_custodyProbeResult;

    /// <summary>
    /// 이 사람이 지금 누를 만한 신병이 있는가 — <b>조준 안내 전용</b>이고 전 피어에서 유효하다. (#664)
    ///
    /// <see cref="CollectHeldBy"/>와 같은 기준을 따라간다(줄을 쥐었으면 그것, 아니면 반경 안의 확보된
    /// 대상). 어긋나면 "회색인데 눌리긴 한다"가 되므로 저쪽을 고치면 여기도 함께 고칠 것.
    /// 개수는 세지 않는다 — 첫 하나에서 끊는다.
    ///
    /// 보는 값이 전부 클라에서 읽힌다: 밧줄 목록은 NetworkList이고, 확보 표식 셋은
    /// <see cref="NpcCustody.IsSecuredByAnyone"/>이 동기화된 것만 쓴다.
    /// </summary>
    public bool HasAdmittableCustody(GameObject interactor)
    {
        if (interactor == null)
            return false;

        if (m_custodyProbeFrame == Time.frameCount && m_custodyProbeInteractor == interactor)
            return m_custodyProbeResult;

        m_custodyProbeFrame = Time.frameCount;
        m_custodyProbeInteractor = interactor;
        m_custodyProbeResult = ProbeCustody(interactor);
        return m_custodyProbeResult;
    }

    // 씬 훑기가 있어 싸지 않다 — 부르는 쪽은 버튼을 <b>겨누고 있는</b> 동안뿐이고, 위 프레임 캐시가
    // 그 동안에도 프레임당 한 번으로 묶는다.
    private bool ProbeCustody(GameObject interactor)
    {
        // ① 줄을 쥐고 있으면 거기서 끝이다 (#637) — 반경 갈래는 손이 빈 사람 몫이다.
        PlayerEscorter escorter = interactor.GetComponent<PlayerEscorter>();
        if (escorter != null && escorter.TetheredCount > 0)
            return true;

        Vector3 origin = interactor.transform.position;
        float sqrReach = m_admitReach * m_admitReach;

        NpcController[] npcs = FindObjectsByType<NpcController>(FindObjectsSortMode.None);
        for (int i = 0; i < npcs.Length; i++)
        {
            NpcController npc = npcs[i];
            if (npc == null || !IsAdmittableState(npc.CurrentState))
                continue;
            if (npc.Custody == null || !npc.Custody.IsSecuredByAnyone)
                continue;
            if ((npc.transform.position - origin).sqrMagnitude <= sqrReach)
                return true;
        }

        return false;
    }

    // 인계 몫(#484)의 귀속자 — 판정이 확정한 인계자 목록(밧줄 보유자 전원 + 버튼을 누른 사람,
    // 팀 확정 2026-08-06)을 clientId로 옮긴다. 누가 인계자인지 정하는 것은 ArrestJudge 몫이다.
    private static ulong[] ToClientIds(List<PlayerEscorter> deliverers)
    {
        var ids = new HashSet<ulong>();
        for (int i = 0; i < deliverers.Count; i++)
            if (deliverers[i] != null)
                ids.Add(deliverers[i].OwnerClientId);

        var result = new ulong[ids.Count];
        ids.CopyTo(result);
        return result;
    }

    // ---- 반출 ----

    /// <summary>
    /// 반출 — 배치된 수감자를 플레이어를 따라오게 한다. 서버(또는 오프라인) 전용. (#492/#537)
    ///
    /// 밧줄을 걸지 않는다: <see cref="NpcEscortedState"/>가 밧줄 이전의 추종·근접 정지(#97)를 그대로
    /// 들고 있고 <c>IsRoped</c>일 때만 건너뛰므로, <c>StartRopeDrag</c> 없이 <c>StartEscort</c>만 부르면
    /// 추종·속도 부스트·거리 이탈이 전부 동작한다.
    ///
    /// <b>여기서 감옥 밖으로 나가지는 않는다</b> (#537) — 따라오게만 하고, 실제로 데리고 나오는 것은
    /// 플레이어가 문에 E를 누를 때다(<see cref="ServerExitJail"/>). 배치 반납과 정산 제외는 여기서
    /// 일어나므로 "끝까지 데리고 있어야 인정"(GDD 9-2)은 꺼내는 순간부터 적용된다.
    /// </summary>
    public void ServerExtract(NpcController npc, Transform follower)
    {
        if (!HasServerAuthority)
            return;

        if (npc == null || follower == null || m_jailZone == null)
            return;

        if (npc.CurrentState != NpcState.Jailed)
            return;

        // 수감 자격은 <b>남겨 둔다</b> — 판정 결과(현상금)를 정산 기록에서 꺼내 되살린다. (#517)
        // 감옥 안에서 다시 세우면(E 정지) 문 앞 재판정 없이 그 자리에서 다시 수감돼야 하는데,
        // 지우면 계상할 근거가 없어진다. 읽기는 ReleaseInmate <b>앞</b>이어야 한다 — 저쪽이 레코드를 지운다.
        if (m_jailZone.TryGetBounty(npc, out int bounty))
            m_pendingBounty[npc] = bounty;
        else
            m_pendingBounty.Remove(npc);

        m_jailZone.ReleaseInmate(npc); // 배치 반납 + 정산·진행도에서 제외

        // <b>ClearDelivered는 부르지 않는다 — 반출은 탈옥이 아니다.</b>
        // 재판정은 문 앞에서 다시 E를 누르면 열리고, 여기서 '첫 인계' 표식까지 되돌리면 반출→재수감을
        // 반복해 진범 검거 수(RoundManager.CriminalArrestCount)를 부풀릴 수 있다. 탈옥(JailbreakEvent)이
        // ClearDelivered를 부르는 것은 대상이 실제로 달아나 도시에서 다시 잡아야 하는 진짜 재검거이기
        // 때문이다 (#358) — 플레이어가 스스로 꺼낸 것과는 다르다.

        // 반출 표식 — 거리 이탈로 멈춰도(Captured) 남아, E가 밧줄이 아니라 추종 재개로 가게 한다 (#517).
        npc.Custody.SetJailExtracted(true);
        npc.Custody.StartEscort(follower);

        Debug.Log($"[감옥] 반출 — 따라오게 한다: {npc.name}");
    }

    // 반출한 대상의 현상금 보관 — 감옥 안에서 다시 세울 때 같은 값으로 계상한다. 서버(또는 오프라인) 전용. (#517)
    private readonly Dictionary<NpcController, int> m_pendingBounty = new Dictionary<NpcController, int>();

    /// <summary>
    /// 감옥 안에서 반출을 되돌린다 — 따라오던 대상을 그 자리에 다시 수감한다. 서버(또는 오프라인) 전용. (#517/#537)
    ///
    /// 예전에는 폴링(R2)이 "Captured + Jail 영역 안 + 사람이 안에 있음"을 보고 알아서 재착석시켰다.
    /// 폴링이 사라져(#537) 되돌리는 순간을 명시적으로 받는다 — 부르는 곳은 추종 정지
    /// (<see cref="PlayerEscortCommands"/>) 하나다. 감옥 밖에서 세운 것은 여기 걸리지 않는다.
    /// </summary>
    public bool ServerReturnToJail(NpcController npc)
    {
        if (!HasServerAuthority || npc == null || m_jailZone == null)
            return false;

        if (!npc.Custody.IsJailExtracted || !m_jailZone.ContainsPoint(npc.transform.position))
            return false;

        // 보관해 둔 현상금이 없으면 판정 결과를 잃은 것이다 — 0원으로 넣지 않고 문 앞 재판정에 맡긴다.
        if (!m_pendingBounty.TryGetValue(npc, out int bounty))
            return false;

        m_pendingBounty.Remove(npc);
        ServerPlaceInJail(npc, bounty, System.Array.Empty<ulong>());
        return true;
    }

    // ---- 반출 대상 떠나보내기 (#548) ----

    /// <summary>
    /// 반출한 수감자가 문 밖으로 나왔다 — 서버(또는 오프라인)에서만 발행한다. (#548)
    /// 구독자는 <see cref="SecretFavorBroker"/> 하나이며, 자기 청탁 대상이면 인도 지점을 목적지로 준다.
    /// 여기서 청탁을 <b>조회</b>하지 않고 알리기만 하는 이유는 감옥이 청탁을 모르게 두기 위해서다 —
    /// 반대로 두면 문이 비밀 청탁의 대상·보상까지 아는 물건이 된다.
    /// </summary>
    public event System.Action<NpcController> OnInmateExited;

    // 문을 나선 반출 대상을 떠나보낸다 — 플레이어를 따라다니던 것이 여기서 끝난다. (#548)
    private void ServerSendOff(NpcCustody custody, Transform extractor)
    {
        NpcController npc = custody != null ? custody.Owner : null;
        if (npc == null)
            return;

        // <b>문을 나서는 순간 반출 추종 국면이 끝난다.</b> 표식을 끄는 것이 곧 저지 창을 여는 일이다 —
        // PlayerEscortCommands.IsRopeBlocked가 이 표식을 보고 밧줄을 막고 있었다(팀 확정 2026-08-05).
        // 그 규칙의 취지는 '데리고 나오는 구간에 긴장을 남긴다'였는데, 그 구간이 문 안쪽으로 줄었다.
        custody.SetJailExtracted(false);

        OnInmateExited?.Invoke(npc);

        if (custody.HasReleaseDestination)
            return; // 청탁이 목적지를 줬다 — 인도 지점까지 스스로 걸어간다 (NpcReleasingState)

        // 청탁이 없는 반출 — 갈 곳이 없으니 도시로 달아난다 (팀 확정 2026-08-07).
        // 꺼낸 사람에게서 도망친다: 실수로 꺼냈으면 그 사람이 쫓아가 다시 잡아야 한다.
        npc.Reaction.StartFlee(extractor);
        Debug.Log($"[감옥] 반출 대상이 도시로 달아난다 (청탁 없음): {npc.name}");
    }

    // ---- 플레이어 출입 (문 E) ----

    /// <summary>플레이어를 감옥 안 입장 지점으로 옮긴다. 서버(또는 오프라인) 전용. (#537)</summary>
    public void ServerEnterJail(PlayerMovement mover)
    {
        if (!HasServerAuthority || mover == null || m_jailZone == null)
            return;

        Transform entry = m_jailZone.PlayerEntryPoint;
        mover.ServerTeleport(entry.position, entry.rotation);
        Debug.Log($"[감옥] 입장 — {mover.name}");
    }

    /// <summary>
    /// 플레이어를 문 밖 퇴장 지점으로 옮긴다 — <b>따라오던 반출 대상도 함께</b> 나온다. 서버(또는 오프라인). (#537)
    ///
    /// <b>순서가 셋으로 갈린다: 산 동행 → 플레이어 → 밧줄 시체.</b> 앞뒤 이유가 정반대다.
    /// <list type="bullet">
    ///   <item><b>산 동행이 먼저</b> — NavMesh로 <b>따라오므로</b>, 뒤에 남으면 한두 프레임 동안
    ///   감옥 안에서 문 밖의 플레이어를 향해 벽으로 달린다.</item>
    ///   <item><b>밧줄 시체가 나중</b> — 관절로 <b>매여 있으므로</b>, 운반자가 뒤에 남으면 줄이
    ///   수백 m로 늘어나고 그 위반이 시체를 발사한다(실측 237 m/s).</item>
    /// </list>
    ///
    /// <b>자리를 나눠 준다</b> — 플레이어는 퇴장 지점 그 자리, 동행은 그 뒤 좌우로 벌어진 자리
    /// (<see cref="JailZone.ExitSlot"/>). 전부 같은 좌표에 놓으면 겹침을 푸는 물리가 서로를 튕겨낸다.
    /// </summary>
    public void ServerExitJail(PlayerMovement mover)
    {
        if (!HasServerAuthority || mover == null || m_jailZone == null)
            return;

        Transform exit = m_jailZone.ExitPoint;

        List<NpcCustody> followers = NpcCustody.FindFollowersOf(mover.transform);

        // 데리고 나오는 대상이 있을 때만 정문을 연다 (#744) — 경찰 혼자 나오는 데 본부 대문이
        // 저절로 열릴 이유는 없다. 반출 대상은 여기서부터 도시까지 걸어 나간다.
        if (followers.Count > 0)
            m_jailZone.ServerOpenFrontDoors();

        for (int i = 0; i < followers.Count; i++)
        {
            followers[i].ServerExitJail(m_jailZone.ExitSlot(i + 1)); // 0번은 플레이어 자리다
            ServerSendOff(followers[i], mover.transform);
        }

        // ⚠ <b>플레이어를 시체보다 먼저 옮긴다.</b> 산 동행과 순서가 반대인데, 이유도 반대다:
        // 동행은 NavMesh로 <b>따라오므로</b> 뒤에 남으면 벽을 향해 달리지만, 밧줄 시체는 관절로
        // <b>매여 있어</b> 운반자가 뒤에 남으면 그 줄이 수백 m로 늘어난다.
        //
        // 배치는 줄을 끊었다 다시 매는데(<c>NpcRagdoll.ServerPlaceCorpse</c>), 다시 맬 때 운반자가
        // 아직 감옥 안이면 <b>방금 없앤 위반을 그대로 다시 만든다.</b>
        mover.ServerTeleport(exit.position, exit.rotation);

        // 밧줄에 걸린 시체도 함께 나온다 (#597) — 안 옮기면 줄만 벽을 뚫고 늘어나고 몸은 방에 남는다.
        int corpses = ServerExitRopedCorpses(mover, followers.Count + 1);
        Debug.Log($"[감옥] 퇴장 — {mover.name} (동행 {followers.Count}명, 시체 {corpses}구)");
    }

    // 이 플레이어 줄에 걸린 시체를 퇴장 자리로 옮긴다 — 산 신병은 FindFollowersOf가 이미 집었다.
    // 시체는 에이전트가 없어 워프가 아니라 뼈째 옮긴다(NpcCustody.ServerMoveCorpse).
    private int ServerExitRopedCorpses(PlayerMovement mover, int firstSlot)
    {
        PlayerEscorter escorter = mover.GetComponent<PlayerEscorter>();
        if (escorter == null)
            return 0;

        int moved = 0;
        for (int i = 0; i < escorter.TetheredCount; i++)
        {
            NpcController npc = escorter.GetTetheredNpc(i);
            if (npc == null || !npc.Death.IsDead)
                continue;

            npc.Custody.ServerMoveCorpse(m_jailZone.ExitSlot(firstSlot + moved));
            ServerReleaseCorpse(npc);
            moved++;
        }

        return moved;
    }

    /// <summary>
    /// 시체 반출 — 문 밖으로 끌고 나온 시체를 <b>정산에서 빼고 재판정을 연다.</b> 서버(또는 오프라인) 전용.
    ///
    /// 시체 수감(<see cref="ServerAdmitCorpse"/>)의 역이다. 저쪽이 세운 것이 둘이라 되돌릴 것도 둘이다:
    /// <list type="bullet">
    ///   <item><b>정산 원장</b>(<see cref="JailZone.RecordDeceased"/>) — 안 지우면 몸은 문 밖에 있는데
    ///   현상금은 그대로 잡혀, 반출해도 정산 금액이 줄지 않는다.</item>
    ///   <item><b>판정 표식</b>(<see cref="NpcCustody.IsDelivered"/>) — 안 지우면
    ///   <see cref="ArrestJudge.JudgeCorpse"/>가 '이미 계상됨'으로 끊어, 문 앞에 다시 놓고 눌러도
    ///   영영 들어가지 않는다.</item>
    /// </list>
    ///
    /// <b>산 수감자의 반출(<see cref="ServerExtract"/>)이 <c>ClearDelivered</c>를 부르지 않는 것과
    /// 갈린다</b> — 되돌릴 대상이 다르기 때문이다. 저쪽은 '첫 인계' 표식이 할당량
    /// (<see cref="RoundManager.CriminalArrestCount"/>)에 물려 있어 반출→재수감 반복으로 부풀 수 있지만,
    /// 시체 판정은 할당량을 건드리지 않는다(<c>OnCorpseJudged</c> 구독자에 RoundManager가 없다).
    ///
    /// <b>표식·기억은 원장 제거 성공과 무관하게 걷는다</b> (#705) — 예전에는 원장 제거가 실패하면
    /// <c>ClearDelivered</c>도 안 불려 재판정이 영영 막혔다.
    /// </summary>
    private void ServerReleaseCorpse(NpcController npc)
    {
        if (npc == null || m_jailZone == null)
            return;

        npc.Custody.ClearDelivered();
        m_unjudgeable.Remove(npc);

        bool ledgerCleared = m_jailZone.ReleaseDeceased(npc);
        Debug.Log(
            ledgerCleared
                ? $"[감옥] 시체 반출 — 정산에서 빼고 재판정을 연다: {npc.name}"
                : $"[감옥] 시체 반출 — 원장에는 없었지만 판정 표식은 걷는다: {npc.name}"
        );
    }
}
