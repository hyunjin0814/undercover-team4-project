using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 검거 판정 — 본부로 인계된 NPC의 실제 신원을 대조해 진범/오검거를 판정한다. (GDD 7-2, #41)
/// HqDropoffZone의 인계 이벤트를 구독해 자동 판정하고, 결과를 로그 + OnArrestJudged로 알린다.
/// 실제 자금 정산(#42)·오검거 페널티(GDD 7-3)·판정 UI(#43)는 이 이벤트를 구독해 후속 구현한다.
///
/// 범인 배정(CriminalAssigner)이 서버에서만 이뤄지고 아직 클라이언트에 동기화되지 않으므로(#52/#56 TODO),
/// 판정도 서버(또는 오프라인)에서만 수행한다 — 인계 NPC의 네트워크 권위로 게이트한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class ArrestJudge : CommonManagerBase
{
    private const int k_wrongfulReward = 0;

    // 진범·위조범 보상은 여기서 정하지 않는다 (#395) — NPC마다 다른 현상금을 CriminalAssigner가
    // 라운드 시작에 뽑아 CitizenIdentity.Bounty에 확정해 두고, 판정은 그 값을 읽기만 한다.
    // 판정 시점에 뽑으면 재판정(#358)·탈옥 후 재검거(#231)로 금액을 리롤할 수 있게 된다.

    [Header("인계 구역 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private HqDropoffZone m_dropoffZone;

    [Tooltip("인계 도달 시 자동 판정. 본부 인계 상호작용(#40) 도입 전까지 데모용으로 켜둔다")]
    [SerializeField] private bool m_autoJudgeOnDelivery = true;

    public event Action<ArrestResult> OnArrestJudged;

    protected override void Awake()
    {
        base.Awake(); // App.Game.ArrestJudge 등록

        // HqDropoffZone은 장소 오브젝트라 App 대상이 아님 — 씬 탐색 유지 (같은 도메인 부품)
        if (m_dropoffZone == null)
            m_dropoffZone = FindFirstObjectByType<HqDropoffZone>();
    }

    private void OnEnable()
    {
        if (m_dropoffZone != null)
            m_dropoffZone.OnNpcDelivered += HandleNpcDelivered;
    }

    private void OnDisable()
    {
        if (m_dropoffZone != null)
            m_dropoffZone.OnNpcDelivered -= HandleNpcDelivered;
    }

    // 판정 완료 표식은 NpcController.IsDelivered가 들고 있다 (#230) — NPC와 수명을 같이하므로
    // 씬 전환·라운드 재시작 시 수동으로 비울 static 상태가 없다.
    // (App 등록 해제는 베이스 OnDestroy가 처리 — 여기서 오버라이드할 것이 없다)

    private void HandleNpcDelivered(NpcController npc)
    {
        if (m_autoJudgeOnDelivery)
            Judge(npc);
    }

    public ArrestResult? Judge(NpcController npc)
    {
        if (npc == null) return null;
        if (npc.IsSpawned && !npc.IsServer) return null;

        // 경범죄 이벤트 NPC(난동꾼)는 신원 대조 이전에 마커로 식별한다 (#106).
        MisdemeanorOffender misdemeanor = npc.GetComponent<MisdemeanorOffender>();
        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();

        // 경범죄 마커도 신원도 없으면 판정할 수 없다.
        if (misdemeanor == null && identity == null)
        {
            Debug.LogWarning($"ArrestJudge: 신원(CitizenIdentity) 없음 — 판정 불가: {npc.name}", npc);
            return null;
        }

        // 첫 인계 여부를 표식 세우기 전에 잡아 둔다 — 할당량·오검거 카운트가 재판정으로 부풀지 않게 (#358).
        bool firstDelivery = !npc.IsDelivered;

        // 판정 완료로 표시 — 본부 방치 도주 타이머(#230)를 멈춘다. 재판정 자체는 허용하므로(#358)
        // 여기서 중복을 막지는 않는다(틱 스팸은 HqDropoffZone의 Escorted/Roped 게이트가 걸러 준다).
        npc.MarkDelivered();

        ArrestVerdict verdict;
        int reward;
        if (misdemeanor != null)
        {
            // 난동꾼 즉결 처리 — 진범/오검거 대조를 타지 않고 경범죄로 확정, 이벤트가 정한 수익을 준다.
            // reward는 지급이 아니라 "유치장 수감 시 실릴 정산 bounty"다 — 실제 자금은 라운드 종료 시
            // 유치장 점유로 1회 정산된다(#340). 그래서 재검거 중복지급 방지용 Reward 비우기는 필요 없다
            // (점유를 한 번만 세므로) — 오히려 비우면 재수감된 난동꾼이 0으로 잡혀 정산에서 누락된다.
            verdict = ArrestVerdict.Misdemeanor;
            reward = misdemeanor.Reward;
        }
        else if (identity.IsCriminal)
        {
            // 진범 우선 — 진범이면서 위조범인 NPC도 현상수배범으로 판정한다 (위조 판정에 가려지지 않음, #320).
            verdict = ArrestVerdict.WantedCriminal;
            reward = ResolveBounty(identity, npc);
        }
        else if (identity.IsForger)
        {
            // 위조범 — 난동꾼과 동일한 즉결 경범죄로 확정하고 소액 위조 보상을 준다 (#320).
            verdict = ArrestVerdict.Misdemeanor;
            reward = ResolveBounty(identity, npc);
        }
        else
        {
            verdict = ArrestVerdict.WrongfulArrest;
            reward = k_wrongfulReward;
        }

        CitizenProfile profile = identity != null ? identity.Profile : null;

        // 줄다리기로 여러 명이 함께 끌고 왔을 수 있다 (#390) — 관여한 전원이 인계자다.
        // 오검거 페널티가 이 목록 전원에게 걸린다: 밧줄이 걸린 채 인계존까지 들어갔다는 것은
        // 막지 못했다는 뜻이고, 손을 떼는 수단(E 놓고 걸어가 줄 끊기 / 자기 줄 풀기)이 양쪽에 있다.
        List<PlayerEscorter> deliverers = PlayerEscorter.FindEscortersOf(npc);
        var result = new ArrestResult(npc, verdict, profile, reward, deliverers, firstDelivery);

        LogVerdict(result);

        // 연행 상태 물리적 해제 (플레이어에게서 분리) — NPC는 Captured로 그 자리에 선다.
        // 반드시 OnArrestJudged보다 **먼저** 해야 한다: 구독자(CustodyRouter, #228)가 판정 결과에 따라
        // 다음 상태(유치장 이송·석방)로 전이시키는데, 해제를 뒤에 하면 StopEscort의 Captured 전이가
        // 그 행선지를 덮어써 NPC가 그 자리에 멈춰버린다.
        if (deliverers.Count > 0)
        {
            // [리뷰 반영] RequestRelease()는 클라이언트 오너 권한이 필요하므로,
            // 비호스트 유저 검거 시 동작하지 않습니다. 따라서 서버 권위로 즉시 풀어버리는 ReleaseDrag()를 호출합니다.
            // 판정된 그 NPC의 줄만 전원에게서 푼다 — 같이 끌고 온 다른 대상은 계속 끌린다 (#390).
            foreach (PlayerEscorter deliverer in deliverers)
                deliverer.ReleaseDrag(npc);
        }
        else
        {
            npc.StopEscort();
        }

        // 수갑 회수(#307/#229)는 제거됐다 — 밧줄은 소모형이 아니라 NPC에 채워둔 자원이 없다. (#369)

        OnArrestJudged?.Invoke(result);

        return result;
    }

    // 배정된 현상금을 읽는다 (#395). 0이면 CriminalAssigner의 배정을 타지 않은 NPC라는 뜻이라 —
    // 라운드 목표(금액)가 조용히 미달로 흐르지 않게 경고를 남긴다. 값 자체는 그대로 쓴다.
    private static int ResolveBounty(CitizenIdentity identity, NpcController npc)
    {
        if (identity.Bounty <= 0)
            Debug.LogWarning($"ArrestJudge: {npc.name}에 현상금이 배정되지 않아 0원으로 판정한다 — CriminalAssigner 배정을 타지 않은 NPC인지 확인할 것", npc);

        return identity.Bounty;
    }

    private static void LogVerdict(ArrestResult result)
    {
        string citizenName = result.Profile != null ? result.Profile.CitizenName : result.Npc.name;
        string tag = result.Verdict switch
        {
            ArrestVerdict.WantedCriminal => "현상수배범 검거",
            ArrestVerdict.Misdemeanor => "경범죄 처리",
            _ => "오검거"
        };
        string deliverer = result.DeliveredBy.Count > 0
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

    /// <summary>이 대상에 밧줄을 걸고 인계존까지 들어온 플레이어 전원 — 아무도 없으면 빈 목록(자동 판정 등). (#390)
    /// 줄다리기로 여러 명이 함께 끌 수 있어 단일 참조에서 목록이 됐다. 검거에 개인 보상은 없고
    /// (팀 자금은 라운드 종료에 유치장 점유로 1회 정산, #340) 이 목록은 <b>페널티 지정</b>에 쓰인다 —
    /// 오검거 개인 카운트와 추격대 대상이 여기서 나온다.</summary>
    public readonly List<PlayerEscorter> DeliveredBy;

    // 이 판정이 첫 인계인지 — 재판정(같은 대상을 다시 인계존에 넣음)이면 false. 할당량·오검거 카운트처럼
    // 1회만 세어야 하는 후처리가 이 값으로 재판정을 걸러 낸다. 탈옥(ClearDelivered) 후 재검거는 다시 true. (#358)
    public readonly bool IsFirstDelivery;

    public ArrestResult(NpcController npc, ArrestVerdict verdict, CitizenProfile profile,
        int reward, List<PlayerEscorter> deliveredBy, bool isFirstDelivery)
    {
        Npc = npc;
        Verdict = verdict;
        Profile = profile;
        Reward = reward;
        DeliveredBy = deliveredBy ?? new List<PlayerEscorter>();
        IsFirstDelivery = isFirstDelivery;
    }
}