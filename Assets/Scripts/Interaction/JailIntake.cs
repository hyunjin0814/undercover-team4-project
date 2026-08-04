using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 유치장 출입구 — <b>누가 유치장에 들어왔고, 어디 앉히고, 언제 내보내나</b>를 담당한다. (#492)
///
/// <see cref="JailZone"/>과 역할이 갈린다: 저쪽은 대장(수용 인원·현상금·정산 레코드·좌석 소유),
/// 이쪽은 출입구다. 한 클래스에 두면 정산 책임과 출입 책임이 섞이고 크기도 감당이 안 된다.
///
/// 서버(또는 오프라인)에서 <see cref="m_checkInterval"/>마다 훑으며 규칙 두 개를 집행한다:
///
///  <b>R1 판정</b> — 확보된 신병(Escorted/Captured)이 <see cref="JailScanner"/> 게이트 안에 들어서면
///                   그 순간 판정한다. 오검거를 좌석까지 끌고 가야 알게 되는 헛수고를 없앤다
///                   (팀 확정 2026-08-03).
///  <b>R2 착석</b> — 판정에서 수감 대상으로 확정된 대상이 Jail 영역 안에서 Captured가 되고
///                   <b>플레이어도 유치장 안에 있으면</b> 가장 가까운 빈 좌석을 배정하고 계상한다.
///                   좌석까지 걸어가 앉는 것은 NpcJailedState가 한다.
///                   사람이 안에 있어야 하는 이유는 TrySeat 주석 — 문 밖에서 밀어 넣는 것을 막는다.
///
/// <b>판정 장소는 유치장 문턱이 아니라 문 앞 게이트다.</b> 처음에는 Jail 영역 진입을 트리거로 썼는데,
/// 유치장 <b>안</b>에서 오검거가 확정되면 그 시민이 신병에서 빠지는 순간 Jail 통행을 잃고, 자기가
/// 딛고 선 폴리곤이 금지돼 그 자리에 굳었다. 판정을 문 밖으로 빼면 오검거된 시민이 애초에 유치장에
/// 발을 들이지 않아 그 사고가 사라진다 — 자세한 근거는 <see cref="JailScanner"/> 주석.
///
/// <b>순서가 강제다: R1 판정 → 통행 → R2 착석.</b> 통행이 판정 결과에 달려 있으므로(아래
/// <see cref="TickJailAccess"/>) 판정이 먼저 나야 같은 틱에 통행이 따라온다. 뒤집으면 판정된 다음
/// 틱(m_checkInterval)에야 통행이 나가, 게이트와 문이 가까운 배치에서는 통행 없이 문턱을 넘는
/// 프레임이 생긴다.
///
/// 판정과 계상이 분리돼 있다 — 문턱만 넘고 안 앉히면 <b>0원</b>이다. 이것이 "직접 넣게 만든다"의
/// 실질적 강제력이고, GDD 9-2의 "이송 중 라운드 종료 시 보상 없음"과도 정확히 맞는다.
///
/// 장소 오브젝트라 App 파사드에 등록하지 않는다 — JailLock·JailZone과 같은 관례로 씬 탐색을 쓴다.
/// </summary>
public class JailIntake : MonoBehaviour
{
    [Header("유치장 (비우면 같은 오브젝트·부모에서 자동 탐색)")]
    [SerializeField] private JailZone m_jailZone;

    [Header("판정 게이트 (비우면 씬에서 자동 탐색)")]
    [Tooltip("확보한 신병이 이 안에 들어서면 판정한다 — 유치장 앞 보안 스캐너")]
    [SerializeField] private JailScanner m_scanner;

    [Header("출입 검사")]
    [Tooltip("검사 주기(초) — 매 프레임 돌 필요가 없다. 0이면 매 프레임 검사한다 (JailDoor와 같은 관례)")]
    [SerializeField] private float m_checkInterval = 0.1f;

    // 다음 검사까지 남은 시간
    private float m_cooldown;

    // '놓은 사람이 안에 있다' 판정의 여유(m) — 밧줄 길이에 더해 쓴다. 놓은 순간 플레이어는 밧줄 길이
    // 안에 있지만, 검사는 최대 m_checkInterval 뒤라 그새 한두 걸음 물러날 수 있다. (#492)
    private const float k_seatWitnessMargin = 2f;

    // 판정에서 수감 대상으로 확정된 대상과 그 보상액 — R2가 여기 있는 대상만 앉힌다.
    // 보상액을 함께 들고 있는 이유: Admit이 착석 시점이라 판정 결과를 그때까지 보관해야 한다.
    // 서버(또는 오프라인) 전용.
    private readonly Dictionary<NpcController, int> m_pendingSeat = new Dictionary<NpcController, int>();

    // 게이트 판정 후 밧줄이 걸렸던 플레이어 — 착석 시 이 집합이 인계자다. (#484)
    // 착석 순간만 보면 E 놓기(줄 유지)와 좌클릭 풀기(줄 제거)가 서로 다른 몫을 줘, 손 떼는 방법에 따라
    // 조용히 돈이 달라진다. 누적하면 둘이 같아진다. 창의 시작이 게이트인 이유는 유치장 문턱을 기준으로
    // 삼으면 플레이어가 알 수 없는 선이 생기기 때문이다 — 판정은 눈에 보이는 사건이라 설명이 된다.
    // 위치는 보지 않으므로 밖으로 다시 끌고 나가도 비우지 않는다 — 신병을 놓치거나(TickJailAccess)
    // 착석할 때만 비운다. 서버(또는 오프라인) 전용.
    private readonly Dictionary<NpcController, HashSet<ulong>> m_deliverers = new Dictionary<NpcController, HashSet<ulong>>();

    // 이번 게이트 통과에 이미 판정한 대상 — 게이트를 벗어나면 지운다(그래야 다시 통과하면 재판정된다, #358).
    //
    // <b>NpcController.IsDelivered를 쓰면 안 된다.</b> 그 플래그는 오검거당한 시민에게 영구히 남는다 —
    // 석방(ReleaseFromCustody)은 ClearDelivered를 부르지 않기 때문이고, 그건 오검거 카운트가 매 인계마다
    // 올라야 해서 의도된 것이다(WrongfulArrestPenalty 주석). 그걸 중복 가드로 쓰면 한 번 오검거된 시민은
    // 다시 끌고 와도 영영 재판정되지 않는다.
    //
    // 옛 인계 단말 경로에는 이 문제가 없었다 — 트리거가 E 입력이라 누른 횟수만큼만 판정됐다.
    // 폴링으로 바뀌면서(#492) 중복 가드가 필요해졌고, 그 기준은 "이번 통과"여야 한다.
    private readonly HashSet<NpcController> m_judgedThisPass = new HashSet<NpcController>();

    // 판정 자체가 불가능했던 대상(신원·경범죄 마커 둘 다 없음) — 매 틱 재시도하면 경고가 폭주한다.
    private readonly HashSet<NpcController> m_unjudgeable = new HashSet<NpcController>();

    // Jail 통행을 내준 대상 — 유치장을 벗어나면 회수한다. 서버(또는 오프라인) 전용.
    private readonly HashSet<NpcController> m_jailAccessGranted = new HashSet<NpcController>();

    // 파괴된 대상 정리용 임시 버퍼 — 매 틱 새로 할당하지 않게 재사용한다
    private readonly List<NpcController> m_deadBuffer = new List<NpcController>();

    private void Awake()
    {
        // 유치장은 같은 오브젝트에 두는 것이 기본 — 인스펙터로 따로 지정할 수도 있다
        if (m_jailZone == null)
            m_jailZone = GetComponentInParent<JailZone>();

        if (m_jailZone == null)
            Debug.LogWarning("JailIntake: 유치장(JailZone)을 찾지 못했다 — 수용이 동작하지 않는다", this);

        // 게이트는 유치장 밖(문 앞)에 서 있어 부모 탐색으로는 닿지 않는다 — 장소 오브젝트라 씬 탐색을 쓴다
        if (m_scanner == null)
            m_scanner = FindFirstObjectByType<JailScanner>();

        if (m_scanner == null)
            Debug.LogWarning("JailIntake: 판정 게이트(JailScanner)를 찾지 못했다 — 판정이 일어나지 않는다", this);
    }

    // 판정·좌석 배정은 서버 권위 — NetworkBehaviour가 아니므로 직접 게이트한다 (CustodyRouter와 같은 패턴)
    private static bool HasServerAuthority =>
        NetworkManager.Singleton == null
        || !NetworkManager.Singleton.IsListening
        || NetworkManager.Singleton.IsServer;

    private void Update()
    {
        if (!HasServerAuthority)
            return;

        m_cooldown -= Time.deltaTime;
        if (m_cooldown > 0f)
            return;
        m_cooldown = m_checkInterval;

        PruneDestroyed();

        // 검사 주기로 호출을 눌러 두었기에 목록 훑기로 충분하다 (JailDoor와 같은 판단)
        NpcController[] npcs = FindObjectsByType<NpcController>(FindObjectsSortMode.None);

        // 순서 강제 — 판정 → 통행 → 착석 (클래스 주석 참고).
        // 판정이 통행의 근거이므로 앞서야 하고, 통행은 착석(좌석까지 걷기)의 바닥이므로 그 사이다.
        for (int i = 0; i < npcs.Length; i++)
            TryJudgeOnEntry(npcs[i]);

        for (int i = 0; i < npcs.Length; i++)
            TickJailAccess(npcs[i]);

        for (int i = 0; i < npcs.Length; i++)
            TrySeat(npcs[i]);
    }

    /// <summary>
    /// Jail 영역 통행 관리 — <b>판정을 통과해 유치장에 들어갈 자격이 있는 대상</b>에게 통행을 내주고,
    /// 자격이 없으면서 밖에 있으면 회수한다. (#492)
    ///
    /// <b>기준이 '신병 여부'에서 '판정 통과 여부'로 바뀌었다.</b> 판정이 문 앞 게이트로 나가면서
    /// (<see cref="JailScanner"/>) "판정을 통과한 수감 대상만 유치장에 들어간다"가 성립하게 됐고,
    /// 그게 상태보다 의미가 정확하다 — 오검거된 시민은 게이트에서 걸러져 통행을 아예 얻지 못한다.
    ///
    /// 자격은 셋 중 하나다:
    ///
    ///  · <b>수감 대상으로 확정</b>(<see cref="m_pendingSeat"/>) — 놓는 순간 <c>Warp</c>가 좌석 근처에
    ///    재부착돼야 한다. 그 부착 지점을 <b>에이전트 자신의 areaMask 안에서</b> 찾기 때문이다
    ///    (NpcController.StopRopeDrag). 시민 마스크는 Jail이 빠져 있어(#415) 통행 없이 놓으면
    ///    좌석 18개 전부가 최대 1.36m 바깥으로 스냅된다 — 실측값이다.
    ///  · <b>이미 앉은 수감자</b>(Jailed) — 좌석까지 걸어가는 경로가 필요하다.
    ///  · <b>반출돼 따라오는 수감자</b>(<see cref="NpcStateRules.IsFollowingUnroped"/>) — 밧줄이 없어
    ///    제 발로 NavMesh를 걷는다. 통행이 없으면 유치장 안에서 걸어 나오는 경로가 문턱에서 끊긴다.
    ///
    /// <b>밧줄로 끌리는 중은 자격에 넣지 않는다</b> — 밧줄은 위치를 직접 대입하므로(에이전트가 꺼져 있다)
    /// NavMesh 영역을 타지 않아 통행이 필요 없다. 넣으면 판정 전에 통행이 나가 게이트가 무의미해진다.
    ///
    /// 회수는 <b>자격이 없고 + 밖에 있을 때만</b> 한다. 안에 선 채로 회수하면 자기가 딛고 선 폴리곤이
    /// 금지돼 경로가 아예 안 잡히고 그 자리에 굳는다. 판정이 문 밖으로 나가 이 사고의 주된 원인
    /// (유치장 안에서 확정되는 오검거)은 사라졌지만, 안에 선 대상은 나올 수 있어야 하므로 남겨 둔다.
    /// </summary>
    private void TickJailAccess(NpcController npc)
    {
        if (npc == null)
            return;

        NpcState state = npc.CurrentState;

        // 신병을 벗어난 대상의 수감 예정을 지운다 — 방치 타이머로 달아났거나(#290) 어떤 이유로든
        // 통제를 벗어난 것이다. 지우지 않으면 자격이 남아 통행을 계속 들고 다니며, 배회로 돌아간
        // 시민이 유치장 안을 거닐어 "시민은 유치장에 못 들어간다"(#415)가 깨진다.
        // 예전에는 통행이 신병 상태에 묶여 있어 이 정리가 저절로 됐다 — 기준이 판정 결과로
        // 바뀌었으니(#492) 명시적으로 돌려놔야 한다. 다시 잡아 게이트를 통과하면 재판정된다.
        if (state != NpcState.Escorted && state != NpcState.Captured && state != NpcState.Jailed)
        {
            m_pendingSeat.Remove(npc);
            m_deliverers.Remove(npc); // 통제를 벗어났으면 인계자 누적도 무효 — 다시 잡아 오면 처음부터 (#484)
        }

        bool qualified =
            m_pendingSeat.ContainsKey(npc)
            || state == NpcState.Jailed
            || NpcStateRules.IsFollowingUnroped(npc);

        // 자격이 있거나, 없어도 이미 유치장 안이면 내준다(안에 선 대상은 그 폴리곤을 딛어야 한다)
        if (qualified || JailArea.Contains(npc.transform.position))
        {
            if (m_jailAccessGranted.Add(npc))
                npc.SetJailAccess(true);
            return;
        }

        // 자격이 없고 밖에 있다 — 회수해 "시민은 유치장에 못 들어간다"(#415)를 되돌린다.
        if (m_jailAccessGranted.Remove(npc))
            npc.SetJailAccess(false);
    }

    // 파괴된 대상을 걷어낸다 — 라운드 종료 잔류 정리(MisdemeanorLoiterer)로 NPC가 사라져도
    // 키가 남아 목록이 라운드마다 자란다. 판정·통행·착석 세 규칙은 <b>살아 있는 NPC를 훑어</b> 도므로
    // (Update의 FindObjectsByType 결과) 사라진 대상은 스스로 지우지 못한다. 그래서 매 틱 먼저 돈다.
    private void PruneDestroyed()
    {
        m_deadBuffer.Clear();

        // 지우면서 돌 수 없으니 키를 먼저 모은다 — Unity의 가짜 null 비교로 파괴 여부를 본다
        foreach (NpcController npc in m_pendingSeat.Keys)
            if (npc == null)
                m_deadBuffer.Add(npc);

        for (int i = 0; i < m_deadBuffer.Count; i++)
        {
            m_pendingSeat.Remove(m_deadBuffer[i]);
            m_deliverers.Remove(m_deadBuffer[i]);
        }

        m_unjudgeable.RemoveWhere(npc => npc == null);
        m_jailAccessGranted.RemoveWhere(npc => npc == null);
        m_judgedThisPass.RemoveWhere(npc => npc == null);
    }

    // R1 — 확보된 신병이 판정 게이트 안에 들어선 순간 판정한다. 통과당 한 번.
    private void TryJudgeOnEntry(NpcController npc)
    {
        if (npc == null || m_scanner == null)
            return;

        // 게이트를 벗어나면 '이번 통과'가 끝난다 — 다시 통과하면 재판정된다 (#358).
        // 상태·판정가능 검사보다 먼저 봐야 한다: 게이트 밖의 모든 대상에서 기록이 지워져야
        // 배회로 돌아간 시민을 나중에 다시 데려왔을 때 재판정이 열린다.
        if (!m_scanner.Contains(npc.transform.position))
        {
            m_judgedThisPass.Remove(npc);
            return;
        }

        if (m_unjudgeable.Contains(npc))
            return;

        // 확보된 신병만 — 끌려오는 중(Escorted)과 내려놓은 대상(Captured) 둘 다 통과한다.
        // 앉은 수감자(Jailed)는 여기 안 걸린다 — 이미 판정이 끝난 최종 상태다.
        if (npc.CurrentState != NpcState.Escorted && npc.CurrentState != NpcState.Captured)
            return;

        // 이번 통과에 이미 판정했다 — 게이트를 벗어났다 다시 들어와야 재판정이다 (#358)
        if (!m_judgedThisPass.Add(npc))
            return;

        ArrestJudge judge = App.Game.ArrestJudge;
        if (judge == null)
        {
            Debug.LogWarning("JailIntake: ArrestJudge가 없어 판정할 수 없다", this);
            return;
        }

        ArrestResult? result = judge.Judge(npc);
        if (result == null)
        {
            // 신원도 경범죄 마커도 없는 대상 — 다시 물어도 답이 같으므로 한 번만 시도한다
            m_unjudgeable.Add(npc);
            return;
        }

        // 오검거는 WrongfulArrestPenalty가 Detained로 가져간다 — 앉힐 대상이 아니다
        if (result.Value.Verdict != ArrestVerdict.WrongfulArrest)
            m_pendingSeat[npc] = result.Value.Reward;
    }

    // R2 — 수감 대상이 유치장 안에서 멈추면(플레이어가 E로 놓으면) 좌석을 배정하고 계상한다.
    private void TrySeat(NpcController npc)
    {
        if (npc == null || m_jailZone == null)
            return;

        int bounty;
        if (!m_pendingSeat.TryGetValue(npc, out bounty))
            return;

        // 줄이 걸린 사람을 계속 모은다 — 창은 게이트 판정(m_pendingSeat 등재)부터다. 이유는 m_deliverers 주석 (#484)
        AccumulateDeliverers(npc);

        // 끌려가는 중에는 앉히지 않는다 — 놓아야(Captured) 앉는다
        if (npc.CurrentState != NpcState.Captured)
            return;

        // 이미 일어나는 중 — 모션이 끝나면 아래에서 예약한 착석이 이어진다. 폴링이라 매 틱 다시 오므로
        // 여기서 걸러 두 번 예약하지 않는다 (#513)
        if (npc.IsStandingUp)
            return;

        if (!JailArea.Contains(npc.transform.position))
            return;

        // <b>사람이 유치장 안에 있어야 앉힌다</b> (#492) — "직접 끌고 들어가 앉힌다"를 그대로 옮긴 조건이다.
        //
        // 이게 없으면 문 밖에 선 채로 신병을 개구부로 밀어 넣고 놓아 수용시킬 수 있다. 밧줄 끌기는
        // 벽 스윕이 파고드는 성분만 버리고 미끄러뜨리므로(NpcController.Rope) 비비면 문틈으로 흘러
        // 들어가고, 그 뒤는 전부 정상 경로라 좌석까지 배정된다 — 문을 열 필요조차 없었다.
        //
        // <b>실패해도 기록을 지우지 않는다.</b> 폴링이라 조건이 매 틱 다시 평가되므로, 밀어 넣힌 대상은
        // 그 자리에 Captured로 서 있다가 <b>누가 실제로 들어오면 그때 앉는다</b>. 조용히 취소해 버리면
        // 정상적으로 놓고 한 걸음 물러난 경우까지 "왜 안 앉지"가 되므로 이 편이 낫다.
        if (!HasPlayerInsideNear(npc))
            return;

        // 여기서 밧줄이 실제로 풀린다 — 묶여 누워 있던 몸이 일어난 뒤 좌석까지 걸어간다 (#513).
        // 좌석 배정·계상·기록 정리를 전부 예약 안에 두는 이유: 일어나는 도중 누가 E로 다시 끌면
        // 예약이 통째로 취소되는데(NpcController.StartRopeDrag), 그때 좌석만 잡히거나 돈만 들어가면
        // 어긋난다. 취소되면 아무 일도 없었던 것이 되고, 다시 놓으면 폴링이 처음부터 다시 판단한다.
        // (묶인 적 없는 대상은 기다리지 않고 곧바로 실행된다 — ServerStandUpThen이 가른다)
        npc.ServerStandUpThen(() =>
        {
            ulong[] deliverers = TakeDelivererIds(npc);

            // 놓은 자리에서 가장 가까운 빈 좌석 — 여기서 좌석까지는 NpcJailedState가 걸어간다(1.5~5.6m)
            Transform seat = m_jailZone.ReserveSeat(npc, npc.transform.position);
            npc.SendToJail(seat);

            // 계상은 착석 시점 (#492) — 판정만 받고 안 앉히면 0원이다
            m_jailZone.Admit(npc, bounty, deliverers);
            m_pendingSeat.Remove(npc);
        });
    }

    // 지금 줄이 걸린 사람을 누적 집합에 더한다. 판정 시점의 ArrestResult.DeliveredBy를 쓰지 않는 이유는
    // 게이트가 재통과마다 다시 판정돼 그 목록이 "마지막 통과" 기준이 되기 때문이다.
    private void AccumulateDeliverers(NpcController npc)
    {
        List<PlayerEscorter> escorters = PlayerEscorter.FindEscortersOf(npc);
        if (escorters.Count == 0)
            return;

        if (!m_deliverers.TryGetValue(npc, out HashSet<ulong> ids))
        {
            ids = new HashSet<ulong>();
            m_deliverers[npc] = ids;
        }

        for (int i = 0; i < escorters.Count; i++)
            ids.Add(escorters[i].OwnerClientId);
    }

    // 누적된 인계자를 꺼내며 기록을 비운다 — 착석 1회당 한 번만 쓴다.
    private ulong[] TakeDelivererIds(NpcController npc)
    {
        if (!m_deliverers.TryGetValue(npc, out HashSet<ulong> ids))
            return System.Array.Empty<ulong>();

        m_deliverers.Remove(npc);

        var result = new ulong[ids.Count];
        ids.CopyTo(result);
        return result;
    }

    // 이 NPC를 앉힐 자격이 있는 사람이 유치장 안에 있는가 — 놓은 사람 본인을 특정하지는 않는다.
    // 밧줄 길이 안의 '유치장 안 플레이어'면 충분하다: 동료가 안에서 받아 주는 것은 막을 이유가 없는
    // 협동이고, 반대로 문 밖에서 밀어 넣는 사람만 있는 경우는 이 조건에 걸린다.
    //
    // 매 틱 전체 조회를 하지만 플레이어는 3~6명이고 검사 주기(m_checkInterval)로 눌려 있다 —
    // NPC 조회와 같은 판단이다.
    private static bool HasPlayerInsideNear(NpcController npc)
    {
        float reach = npc.RopeLength + k_seatWitnessMargin;
        float sqrReach = reach * reach;
        Vector3 npcPosition = npc.transform.position;

        PlayerInteractor[] players = FindObjectsByType<PlayerInteractor>(FindObjectsSortMode.None);
        for (int i = 0; i < players.Length; i++)
        {
            Vector3 playerPosition = players[i].transform.position;
            if ((playerPosition - npcPosition).sqrMagnitude > sqrReach)
                continue;
            if (JailArea.Contains(playerPosition))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 반출 — 앉은 수감자를 일으켜 플레이어를 따라오게 한다. 서버(또는 오프라인) 전용. (#492)
    ///
    /// 밧줄을 걸지 않는다: <see cref="NpcEscortedState"/>가 밧줄 이전의 추종·근접 정지(#97)를 그대로
    /// 들고 있고 <c>IsRoped</c>일 때만 건너뛰므로, <c>StartRopeDrag</c> 없이 <c>StartEscort</c>만 부르면
    /// 추종·속도 부스트·거리 이탈이 전부 동작한다.
    ///
    /// 좌석 반납과 정산 제외가 함께 일어난다 — "끝까지 데리고 있어야 인정"(GDD 9-2)이 그대로 유지된다.
    /// </summary>
    public void ServerExtract(NpcController npc, Transform follower)
    {
        if (!HasServerAuthority)
            return;

        if (npc == null || follower == null || m_jailZone == null)
            return;

        if (npc.CurrentState != NpcState.Jailed)
            return;

        m_jailZone.ReleaseInmate(npc); // 좌석 반납 + 정산·진행도에서 제외

        // 수감 대상 기록도 지운다 — 남겨두면 재판정 결과가 오검거로 바뀌어도 R2가 옛 기록을 보고 앉힌다
        m_pendingSeat.Remove(npc);

        // 통과 기록은 건드릴 것이 없다 — 판정 장소가 문 앞 게이트로 나가면서(#492) 유치장 안에서의
        // 재판정 경로가 사라졌다. 반출한 대상을 다시 앉히려면 게이트를 다시 통과해야 하고,
        // 그때 이 기록은 이미 '게이트 밖'으로 지워져 있다(TryJudgeOnEntry 첫 분기).
        // 팀 확정 2026-08-03: "판정 장소는 스캐너 하나로 일원화한다".

        // <b>ClearDelivered는 부르지 않는다 — 반출은 탈옥이 아니다.</b>
        // 재판정은 게이트를 다시 통과하는 것으로 열려 있고, 여기서 '첫 인계' 표식까지
        // 되돌리면 반출→재착석을 반복해 진범 검거 수(RoundManager.CriminalArrestCount)를 부풀릴 수 있다.
        // 탈옥(JailbreakEvent)이 ClearDelivered를 부르는 것은 대상이 실제로 달아나 도시에서 다시
        // 잡아야 하는 진짜 재검거이기 때문이다 (#358) — 플레이어가 스스로 꺼낸 것과는 다르다.

        // 반출 표식 — 거리 이탈로 멈춰도(Captured) 남아, E가 밧줄이 아니라 추종 재개로 가게 한다 (#517).
        // 끄는 것은 NpcController가 한다: 재착석·도주 등 커스터디 이탈과 밧줄에 묶이는 순간.
        npc.SetJailExtracted(true);

        // 앉은 자세를 전이보다 먼저 푼다 — 뒤에 두면 일어서는 순간이 한두 프레임 앉은 채로 보인다 (#462)
        npc.SetSeated(false);
        npc.StartEscort(follower);

        Debug.Log($"[유치장] 반출 — 따라오게 한다: {npc.name}");
    }
}
