using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 검거 판정 — 확보한 신병의 실제 신원을 대조해 진범/오검거를 판정한다. (GDD 7-2, #41)
/// <see cref="JailIntake"/>가 문 앞 E로 신병을 넣는 순간
/// <see cref="Judge"/>를 부르면 판정하고, 결과를 로그 + OnArrestJudged로 알린다.
/// 판정 장소 이력: 인계존 도달 자동 판정(#59) → 인계 단말 E(#414) →
/// <b>유치장 앞 보안 스캐너(#492)</b>. 중간에 '유치장 문턱(Jail 영역 진입)'을 거쳤는데, 유치장
/// <b>안</b>에서 확정된 오검거가 통행을 잃고 그 자리에 굳어 문 밖 게이트로 다시 물러났다.
/// 실제 자금 정산(#42)·오검거 페널티(GDD 7-3)·판정 UI(#43)는 이 이벤트를 구독해 후속 구현한다.
///
/// <b>시체도 같은 문을 지난다</b> (#571): 죽은 대상은 죽는 순간이 아니라 유치장 문 앞 버튼에서
/// 판정된다(<see cref="JudgeCorpse"/>). <b>죽는 순간에는 아무것도 하지 않는다</b> — 현상금도
/// 오검거도 전부 그 버튼에서 확정된다.
///
/// 범인 배정(CriminalAssigner)이 서버에서만 이뤄지고 아직 클라이언트에 동기화되지 않으므로(#52/#56 TODO),
/// 판정도 서버(또는 오프라인)에서만 수행한다 — 인계 NPC의 네트워크 권위로 게이트한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class ArrestJudge : CommonManagerBase
{
    private const int k_wrongfulReward = 0;

    private RoundManager Round => App.Game.Round;

    // 조건 부합 조회 대상 — 정답이 개체가 아니라 수배 조건이 됐다 (#669). 판정은 여기 열린 항목과
    // 대조한다. static이던 TryResolveVerdict가 인스턴스 메서드가 된 것도 이 접근 때문이다.
    private WantedListManager WantedList => App.Game.WantedList;

    // 진범·위조범 보상은 여기서 정하지 않는다 (#395) — NPC마다 다른 현상금을 CriminalAssigner가
    // 라운드 시작에 뽑아 CitizenIdentity.Bounty에 확정해 두고, 판정은 그 값을 읽기만 한다.
    // 판정 시점에 뽑으면 재판정(#358)·탈옥 후 재검거(#231)로 금액을 리롤할 수 있게 된다.

    public event Action<ArrestResult> OnArrestJudged;

    /// <summary>
    /// 시체 판정이 났다 — <b>표시 전용 훅이다.</b> 서버(또는 오프라인)에서만 발생한다. (#571)
    ///
    /// <see cref="OnArrestJudged"/>와 <b>일부러</b> 갈라 뒀다. 저쪽 구독자 대부분은 "지금 신병을
    /// 확보했다"를 전제로 <b>상태를 전이시키는데</b>(오검거 수용·석방) 시체에는 성립하지 않고,
    /// <see cref="NpcStateMachine"/>이 사망 이탈을 막으므로 에러만 난다. 상태를 건드리지 않는
    /// 후처리(수배 항목 제거·표시)는 양쪽을 함께 구독한다 (#616).
    ///
    /// ⚠ <b>여기에 NPC 상태를 바꾸는 구독자를 붙이지 말 것.</b> 지금 구독자는 판정 배너
    /// (<see cref="ArrestVerdictFeedback"/>) · 전체 알림(<see cref="ArrestNoticeBroadcaster"/>) ·
    /// 수배 항목 제거(<see cref="WantedListManager"/>)뿐이다 (#616). 셋 다 표시·기록만 손대고
    /// 신병 라우팅을 하지 않는다 — 그 성격을 유지해야 한다.
    /// </summary>
    public event Action<ArrestResult> OnCorpseJudged;

    // 판정 완료 표식은 NpcCustody.IsDelivered가 들고 있다 (#230) — NPC와 수명을 같이하므로
    // 씬 전환·라운드 재시작 시 수동으로 비울 static 상태가 없다.
    // (App 등록 해제는 베이스 OnDestroy가 처리 — 여기서 오버라이드할 것이 없다)

    /// <summary>
    /// 판정 — 대상의 신원을 대조해 진범/경범죄/오검거를 확정한다. 서버(또는 오프라인) 전용.
    ///
    /// <b>호출 시점은 <see cref="JailIntake"/>가 쥔다</b> (#492): 확보한 신병이 판정 게이트를 지나는
    /// 순간 한 번 부른다. 상태·구역 검증은 그쪽이 이미 끝냈으므로 여기서 다시 하지 않는다 —
    /// 예전 인계 단말 경로의 TryDeliver(상태·구역 재검증)는 단말과 함께 제거됐다.
    ///
    /// 재판정(#358)은 그대로 허용되지만 <b>그 판단은 여기가 아니다</b>: 같은 통과에 매 틱 다시
    /// 판정되지 않게 거르는 것은 JailIntake의 통과 기록이고, 게이트를 벗어나면 그 기록이 지워져
    /// 재판정이 열린다. 반출한 수감자를 다시 앉히려면 게이트를 한 번 더 지나야 한다.
    /// 반출은 <c>ClearDelivered</c>를 부르지 않는다 — 그건 탈옥 전용이다(JailIntake 주석).
    /// 재판정 후처리 중복은 <see cref="ArrestResult.IsFirstDelivery"/>가 건다.
    /// </summary>
    /// <param name="presser">
    /// 수감 버튼을 누른 플레이어 — 밧줄을 걸고 있지 않아도 <b>인계자에 포함</b>된다. (#537)
    ///
    /// 예전에는 인계자를 밧줄에서만 뽑았다. 신병을 끌고 유치장까지 들어가는 것이 곧 인계였기
    /// 때문이다. 문 앞 버튼으로 바뀌면서(#537) <b>줄을 풀어 문 앞에 놓고 누르는 것이 정상 경로</b>가
    /// 됐고, 그때 밧줄 목록이 비어 판정 배너(<see cref="ArrestVerdictFeedback"/>)가 보여줄 대상을
    /// 잃고 조용히 스킵됐다 — 누른 사람에게 결과가 안 뜬다.
    ///
    /// 오검거 페널티도 이 목록을 대상으로 삼으므로, 누른 사람이 함께 책임진다 —
    /// 무고한 시민을 넣은 것은 버튼을 누른 손이다. 인계 몫(#484)의 귀속 기준과도 같다.
    /// </param>
    public ArrestResult? Judge(NpcController npc, PlayerEscorter presser = null)
    {
        if (npc == null)
            return null;
        if (npc.IsSpawned && !npc.IsServer)
            return null;

        // 라운드 진행 중에만 판정한다. 준비 중(Preparing)에는 먼저 입장한 플레이어가 남들이 로딩하는
        // 사이에 검거해 진행도를 벌어둘 수 있고, 종료 후(Ended)에는 정산이 이미 스냅샷된 뒤다.
        // 판정을 막으면 MarkDelivered도 서지 않아 라운드가 시작된 뒤 정상적으로 다시 판정된다.
        // (RoundManager가 없는 단독 테스트 씬은 게이트하지 않는다 — MisdemeanorLoiterer와 같은 관례)
        if (Round != null && Round.Phase != RoundPhase.InProgress)
            return null;

        // 첫 인계 여부를 표식 세우기 전에 잡아 둔다 — 할당량·오검거 카운트가 재판정으로 부풀지 않게 (#358).
        bool firstDelivery = !npc.Custody.IsDelivered;

        if (
            !TryResolveVerdict(
                npc,
                out ArrestVerdict verdict,
                out int reward,
                out CitizenProfile profile,
                out ulong matchedWantedId
            )
        )
            return null;

        // 판정 완료로 표시 — 방치 도주 타이머(#230)를 멈춘다. 재판정 자체는 허용하므로(#358)
        // 여기서 중복을 막지 않는다. 막는 것은 <b>부르는 쪽</b>이다: JailIntake가 게이트 통과당
        // 한 번만 부른다(m_judgedThisPass). 판정이 E 입력에서 폴링으로 바뀌었으므로(#492) "누른
        // 횟수만큼만 판정된다"는 옛 근거(#414)는 더 이상 성립하지 않는다.
        npc.Custody.MarkDelivered();

        // 줄다리기로 여러 명이 함께 끌고 왔을 수 있다 (#390) — 관여한 전원이 인계자다.
        // 오검거 페널티가 이 목록 전원에게 걸린다: 밧줄이 걸린 채 유치장까지 들어갔다는 것은
        // 막지 못했다는 뜻이고, 손을 떼는 수단(E 놓고 걸어가 줄 끊기 / 자기 줄 풀기)이 양쪽에 있다.
        // 끌고 있지 않아도 줄이 이어져 있으면 포함된다 — 유치장 안에 내려놓은 뒤 판정되는 경로(#492)에서도
        // 인계자가 '알 수 없음'이 되지 않는다.
        List<PlayerEscorter> deliverers = PlayerEscorter.FindEscortersOf(npc);

        // 버튼을 누른 사람도 인계자다 (#537) — 줄을 풀어 놓고 누르는 경로에서는 이 사람이 유일하다.
        // 중복은 거른다: 자기 줄에 걸어 끌고 온 사람이 그대로 누르는 것이 가장 흔한 경우다.
        if (presser != null && !deliverers.Contains(presser))
            deliverers.Add(presser);

        var result = new ArrestResult(
            npc,
            verdict,
            profile,
            reward,
            deliverers,
            firstDelivery,
            matchedWantedId
        );

        LogVerdict(result);

        // 밧줄 해제는 <b>오검거에만</b> 건다 (#492).
        //
        // 수감 판정(현상수배범·경범죄)은 판정 후에도 묶인 채 남아야 한다 — 플레이어가 좌석까지
        // 끌고 가 E로 놓을 때 앉기 때문이다. 여기서 풀면 유치장 문을 통과하는 순간 Captured가 되어
        // JailIntake가 즉시 착석시켜 버린다.
        //
        // 오검거는 반대로 <b>반드시 여기서 풀어야 한다</b>. 아래 OnArrestJudged의 구독자
        // WrongfulArrestPenalty가 그 자리에서 원한 구역으로 전이시키는데(SendToDetention),
        // 밧줄이 걸린 동안은 NavMeshAgent가 꺼져 있어(StartRopeDrag) 전이한 상태의 Enter가
        // 죽은 에이전트에 목적지를 걸고 조용히 실패한다 — 시민이 묶인 채 굳는다.
        // TickTetherCleanup은 목록에서만 빼고 앵커는 떼지 않으므로 뒤늦게 풀어도 이미 늦다.
        // 그래서 순서가 강제다: 해제 → 이벤트 발행.
        if (verdict == ArrestVerdict.WrongfulArrest)
        {
            // 서버 권위로 즉시 푼다 — RequestRelease는 클라 오너 권한이 필요해 비호스트 검거에서 동작하지 않는다.
            // 판정된 그 NPC의 줄만 전원에게서 푼다 — 같이 끌고 온 다른 대상은 계속 끌린다 (#390).
            if (deliverers.Count > 0)
            {
                foreach (PlayerEscorter deliverer in deliverers)
                    deliverer.ReleaseDrag(npc);
            }
            else
            {
                npc.Custody.StopEscort();
            }
        }

        // 수갑 회수(#307/#229)는 제거됐다 — 밧줄은 소모형이 아니라 NPC에 채워둔 자원이 없다. (#369)

        OnArrestJudged?.Invoke(result);

        return result;
    }

    // <b>사망 시점 판정(JudgeDeath)은 없어졌다.</b> 죽는 순간 오검거를 즉시 세던 경로였는데, 그 근거가
    // "죽여서 페널티를 회피하는 것이 최적 전략이 되면 안 된다"였다. 오검거가 페널티 없이 <b>횟수 집계만</b>
    // 하게 되면서 회피할 대상이 사라졌고, 즉시 집계가 세우던 표식(MarkDelivered)은 <see cref="JudgeCorpse"/>를
    // 함께 막아 <b>시체를 문 앞까지 끌고 가도 판정이 조용히 끊기는</b> 부작용을 내고 있었다.
    // 이제 산 신병과 시체가 같은 문 하나를 지난다 — 결과는 수감 버튼에서만 나온다.
    //
    // 환경사(차·폭발)를 걸러 내던 IsPlayerKiller도 함께 사라졌다 (#634). 같은 일을 인계자 기준이 대신 한다:
    // 아무도 시체를 문 앞까지 끌고 가지 않으면 판정 자체가 일어나지 않는다.

    /// <summary>
    /// 시체 판정 — 유치장 문 앞까지 끌고 온 시체를 판정한다. 서버(또는 오프라인) 전용. (#571)
    ///
    /// <see cref="JailIntake.ServerAdmitHeldBy"/>가 수감 버튼 경로에서 부른다. <see cref="Judge"/>와
    /// <b>판별은 공유하고 뒤처리는 공유하지 않는다</b>:
    ///
    /// <list type="bullet">
    ///   <item><b><see cref="OnArrestJudged"/> 대신 <see cref="OnCorpseJudged"/>를 발행한다</b> —
    ///   저쪽에는 신병 라우팅(상태 전이) 구독자가 섞여 있어 시체에 성립하지 않는다. 양쪽 모두에
    ///   필요한 후처리(수배 항목 제거·표시)는 두 훅을 함께 구독한다.</item>
    ///   <item><b>재판정이 판정 종류로 갈린다.</b> <see cref="NpcCustody.IsDelivered"/>가 막는 것은
    ///   <b>계상</b>이지 판정이 아니다 — 계상되는 쪽(진범·경범죄)은 원장에 두 번 오르면 현상금이
    ///   겹치므로 한 번만 받고, 그 표식을 걷는 <b>밧줄 반출</b>(<c>JailIntake.ServerReleaseCorpse</c>)을
    ///   거쳐야 다시 넣을 수 있다. <b>오검거는 이 가드를 타지 않는다</b>: 감옥에 들어가지 않아 원장에
    ///   없고 계상 대신 횟수만 세므로 막을 중복이 없다. 산 신병과 같은 규칙이다 — 다시 인계하면
    ///   또 한 번의 오검거다 (#358).</item>
    ///   <item><b>오검거도 여기서 센다.</b> 예전에는 죽는 순간 셌고(JudgeDeath) 그 표식이 무고한 시민의
    ///   시체를 이 함수에 들어오지도 못하게 막았다 — 버튼을 눌러도 아무 결과가 안 나오던 원인이다.
    ///   이제 산 신병과 같은 기준으로 <b>인계자</b>에게 붙는다: 시체를 문 앞까지 끌고 와 누른 사람이
    ///   무고한 시민을 넣은 것이다. 끌고 가지 않으면 아무 일도 일어나지 않는다.</item>
    /// </list>
    /// </summary>
    /// <param name="presser">수감 버튼을 누른 플레이어 — 밧줄을 쥔 사람들과 함께 인계자가 된다.</param>
    /// <returns>판정 결과 — 판정할 수 없거나 이미 <b>계상된</b> 시체면 null (오검거는 매번 성립한다).</returns>
    public ArrestResult? JudgeCorpse(NpcController npc, PlayerEscorter presser)
    {
        if (npc == null)
            return null;
        if (npc.IsSpawned && !npc.IsServer)
            return null;
        if (!npc.Death.IsDead)
            return null;

        if (Round != null && Round.Phase != RoundPhase.InProgress)
            return null;

        // 표식은 판별보다 <b>먼저</b> 읽는다 — 아래 계상 가드와 결과의 IsFirstDelivery가 같은 값을 봐야 한다.
        bool firstDelivery = !npc.Custody.IsDelivered;

        if (
            !TryResolveVerdict(
                npc,
                out ArrestVerdict verdict,
                out int reward,
                out CitizenProfile profile,
                out ulong matchedWantedId
            )
        )
            return null;

        // 이미 계상된 시체는 다시 계상하지 않는다 — 원장에 두 번 오르면 현상금이 겹친다. 되돌리는
        // 경로는 밧줄 반출 하나다(JailIntake.ServerReleaseCorpse가 원장과 표식을 함께 걷는다).
        //
        // <b>오검거는 여기를 지난다.</b> 감옥에 들어가지 않아 원장에 없고, 계상 대신 횟수만 세므로
        // 막을 중복이 없다 — 막으면 무고한 시민의 시체가 한 번 판정된 뒤 영영 결과를 못 내게 된다.
        if (!firstDelivery && verdict != ArrestVerdict.WrongfulArrest)
            return null;

        // 표식도 계상되는 쪽에만 세운다 — 오검거에 세우면 위 가드를 스스로 닫아 재판정이 막힌다.
        if (verdict != ArrestVerdict.WrongfulArrest)
            npc.Custody.MarkDelivered();

        // 인계자 기준은 산 신병과 같다 (#537) — 줄을 쥔 전원 + 버튼을 누른 사람.
        // 시체 밧줄은 1:1이라(NpcStateRules.CanRopeBind) 목록은 보통 한둘이다.
        List<PlayerEscorter> deliverers = PlayerEscorter.FindEscortersOf(npc);
        if (presser != null && !deliverers.Contains(presser))
            deliverers.Add(presser);

        var result = new ArrestResult(
            npc,
            verdict,
            profile,
            reward,
            deliverers,
            firstDelivery,
            matchedWantedId
        );

        // 오검거 집계는 산 신병과 같은 기준·같은 대상이고, <b>인계마다</b> 오른다 (#358 — 저쪽
        // HandleArrestJudged가 IsFirstDelivery로 막지 않는 것과 같은 이유).
        // 훅이 갈려 있어(OnCorpseJudged에는 페널티 매니저가 구독하지 않는다) 여기서 직접 부른다 —
        // 저쪽 구독을 늘리지 않는 이유는 OnCorpseJudged의 성격(표시·기록 전용, 상태를 안 건드림)을
        // 지키기 위해서다.
        if (verdict == ArrestVerdict.WrongfulArrest)
            App.Game.WrongfulArrestPenalty?.ServerCountWrongfulCorpse(deliverers);

        LogVerdict(result);
        OnCorpseJudged?.Invoke(result);

        return result;
    }

    /// <summary>
    /// 신원을 대조해 판정과 보상액을 낸다 — <b>부수효과가 없는 순수 판별</b>이다. (#571에서 분리)
    ///
    /// <see cref="Judge"/>(산 신병 인계)와 <see cref="JudgeCorpse"/>(시체 인계)가 공유한다.
    /// 갈라 둔 이유는 둘이 <b>판별은 같고 뒤처리가 전혀 다르기</b> 때문이다.
    /// 판별까지 복사하면 진범/위조범/난동꾼 우선순위가 여러 곳으로 갈린다.
    /// </summary>
    /// <returns>판정할 수 있으면 참 — 경범죄 마커도 신원도 없으면 거짓.</returns>
    ///
    /// <b>정답이 개체에서 조건으로 바뀌었다 (#669).</b> 예전에는 그 NPC 하나만 정답이었는데(IsCriminal),
    /// 이제는 열려 있는 수배 조건에 이 NPC의 외형이 부합하면 누구든 진범 판정이 난다 — WantedList
    /// 조회가 필요해 static을 벗었다. CitizenIdentity.IsCriminal은 "이 NPC가 조건의 기준(출제자)인가"로
    /// 뜻이 좁아졌고 판정에서는 더 이상 읽지 않는다.
    private bool TryResolveVerdict(
        NpcController npc,
        out ArrestVerdict verdict,
        out int reward,
        out CitizenProfile profile,
        out ulong matchedWantedId
    )
    {
        verdict = ArrestVerdict.WrongfulArrest;
        reward = k_wrongfulReward;
        profile = null;
        matchedWantedId = 0;

        // 경범죄 이벤트 NPC(난동꾼)는 신원 대조 이전에 마커로 식별한다 (#106).
        MisdemeanorOffender misdemeanor = npc.GetComponent<MisdemeanorOffender>();
        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();

        // 경범죄 마커도 신원도 없으면 판정할 수 없다.
        if (misdemeanor == null && identity == null)
        {
            Debug.LogWarning(
                $"ArrestJudge: 신원(CitizenIdentity) 없음 — 판정 불가: {npc.name}",
                npc
            );
            return false;
        }

        profile = identity != null ? identity.Profile : null;

        if (misdemeanor != null)
        {
            // 난동꾼 즉결 처리 — 진범/오검거 대조를 타지 않고 경범죄로 확정, 이벤트가 정한 수익을 준다.
            // reward는 지급이 아니라 "정산에 실릴 bounty"다 — 실제 자금은 라운드 종료 시 1회 정산된다
            // (#340). 그래서 재검거 중복지급 방지용 Reward 비우기는 필요 없다(한 번만 세므로) —
            // 오히려 비우면 재수감된 난동꾼이 0으로 잡혀 정산에서 누락된다.
            verdict = ArrestVerdict.Misdemeanor;
            reward = misdemeanor.Reward;
        }
        else if (
            WantedList != null
            && WantedList.TryMatchOpen(identity.Appearance, out WantedEntry hit)
        )
        {
            // 조건 부합 — 열려 있는 수배 조건에 이 외형이 맞으면 개체와 무관하게 진범 판정 (#669).
            // 보상은 이 NPC의 CitizenIdentity.Bounty가 아니라 부합한 수배 항목의 현상금이다 —
            // 기준 NPC가 아닌 다른 부합자를 잡으면 identity.Bounty는 대부분 0이다.
            verdict = ArrestVerdict.WantedCriminal;
            reward = hit.Bounty;
            matchedWantedId = hit.NpcId;
        }
        else if (identity.IsForger)
        {
            // 위조범 — 난동꾼과 동일한 즉결 경범죄로 확정하고 소액 위조 보상을 준다 (#320).
            verdict = ArrestVerdict.Misdemeanor;
            reward = ResolveBounty(identity, npc);
        }

        return true;
    }

    // 배정된 현상금을 읽는다 (#395). 0이면 CriminalAssigner의 배정을 타지 않은 NPC라는 뜻이라 —
    // 라운드 목표(금액)가 조용히 미달로 흐르지 않게 경고를 남긴다. 값 자체는 그대로 쓴다.
    private static int ResolveBounty(CitizenIdentity identity, NpcController npc)
    {
        if (identity.Bounty <= 0)
            Debug.LogWarning(
                $"ArrestJudge: {npc.name}에 현상금이 배정되지 않아 0원으로 판정한다 — CriminalAssigner 배정을 타지 않은 NPC인지 확인할 것",
                npc
            );

        return identity.Bounty;
    }

    private static void LogVerdict(ArrestResult result)
    {
        string citizenName = result.Profile != null ? result.Profile.CitizenName : result.Npc.name;
        string tag = result.Verdict switch
        {
            ArrestVerdict.WantedCriminal => "현상수배범 검거",
            ArrestVerdict.Misdemeanor => "경범죄 처리",
            _ => "오검거",
        };
        string deliverer =
            result.DeliveredBy.Count > 0
                ? string.Join(", ", result.DeliveredBy.ConvertAll(e => e.name))
                : "알 수 없음";
        Debug.Log($"[검거 판정] {tag}: {citizenName} (인계: {deliverer}) — 보상 {result.Reward}원");
    }
}

public readonly struct ArrestResult
{
    public readonly NpcController Npc;
    public readonly ArrestVerdict Verdict;
    public readonly CitizenProfile Profile;
    public readonly int Reward;

    /// <summary>이 대상에 밧줄을 걸고 유치장까지 들어온 플레이어 전원 — 아무도 없으면 빈 목록. (#390)
    /// 반출한 수감자가 밧줄 없이 따라 들어와 재판정되는 경로(#492)가 그 빈 목록의 실제 사례다.
    /// 줄다리기로 여러 명이 함께 끌 수 있어 단일 참조에서 목록이 됐다. 검거에 개인 보상은 없고
    /// (팀 자금은 라운드 종료에 유치장 점유로 1회 정산, #340) 이 목록은 <b>페널티 지정</b>에 쓰인다 —
    /// 오검거 개인 카운트와 추격대 대상이 여기서 나온다.</summary>
    public readonly List<PlayerEscorter> DeliveredBy;

    // 이 판정이 첫 인계인지 — 재판정(같은 대상을 유치장에 다시 넣음)이면 false. 할당량·오검거 카운트처럼
    // 1회만 세어야 하는 후처리가 이 값으로 재판정을 걸러 낸다. 탈옥(ClearDelivered) 후 재검거는 다시 true. (#358)
    public readonly bool IsFirstDelivery;

    /// <summary>부합한 수배 항목의 NpcId — 진범(WantedCriminal) 판정일 때만 유효하다. (#669)
    /// <see cref="Npc"/>(잡힌 개체) 자신의 NetworkObjectId가 아니라 <b>닫아야 할 수배 항목</b>의 키다 —
    /// 조건 기반에서는 잡힌 개체와 조건의 기준 개체가 다를 수 있어 WantedListManager가 이 값으로 항목을 찾는다.</summary>
    public readonly ulong MatchedWantedId;

    public ArrestResult(
        NpcController npc,
        ArrestVerdict verdict,
        CitizenProfile profile,
        int reward,
        List<PlayerEscorter> deliveredBy,
        bool isFirstDelivery,
        ulong matchedWantedId
    )
    {
        Npc = npc;
        Verdict = verdict;
        Profile = profile;
        Reward = reward;
        DeliveredBy = deliveredBy ?? new List<PlayerEscorter>();
        IsFirstDelivery = isFirstDelivery;
        MatchedWantedId = matchedWantedId;
    }
}
