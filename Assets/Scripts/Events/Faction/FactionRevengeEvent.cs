using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 세력 소탕 돌발 이벤트 — 수감된 범인의 세력이 복수대를 보내 한 플레이어를 집중 공격한다(GDD 6-4).
/// NpcResistState를 그대로 쓰며 인원·포기하지 않음·고정 외형만 다르다.
/// </summary>
public class FactionRevengeEvent : SpawnedNpcEventBase
{
    private const int k_neverTriggered = int.MinValue;

    [Header("세력 소탕 — 발동 조건")]
    [Tooltip("이 라운드부터 발동한다 — 유치장에 세력 있는 수감자가 쌓이는 라운드 후반용 이벤트 (수치 보류, GDD 12장)")]
    [Min(1)]
    [SerializeField] private int m_minRound = 3;

    [Header("복수대 인원")]
    [Tooltip("라운드별 복수대 인원 표. 비우면 아래 폴백 인원을 그대로 쓴다")]
    [SerializeField] private FactionRevengeTable m_memberTable;

    [Tooltip("표를 안 붙였을 때 쓸 복수대 인원")]
    [Min(1)]
    [SerializeField] private int m_fallbackMemberCount = 3;

    [Header("외형")]
    [Tooltip("복수대 전원이 쓸 모델 이름 — AppearanceModelCatalog의 ModelName과 대조한다. 비우면 프리팹 기본(무작위 바디)")]
    [SerializeField] private string m_modelName = "Character_Muscle_Male_01";

    private int m_lastTriggeredRound = k_neverTriggered;

    private OfficialRecords.Faction m_faction;

    private bool m_forced;

    public override string NoticeKey => "Hud.Event.Notice.FactionRevenge";

    protected override ERiotBehavior RiotBehavior => ERiotBehavior.Resist;

    protected override int SpawnCount =>
        m_memberTable != null
            ? m_memberTable.GetMemberCount(CurrentRound, m_fallbackMemberCount)
            : m_fallbackMemberCount;

    private static int CurrentRound
    {
        get
        {
            RoundProgress progress = App.Game.RoundProgress;
            return progress != null ? progress.Current : RoundProgress.k_firstRound;
        }
    }

    public override bool CanTrigger()
    {
        int round = CurrentRound;
        if (round < m_minRound || round == m_lastTriggeredRound)
            return false;

        if (!TryPickJailedFaction(out _))
            return false;

        return base.CanTrigger();
    }

    /// <summary>강제 발동 시 라운드·횟수·수감자 조건을 건너뛴다(표적은 필요).</summary>
    public override bool ServerPrepareForceTrigger()
    {
        if (!base.CanTrigger())
        {
            Debug.LogWarning("FactionRevengeEvent: 강제 발동할 표적이 없다 — 행동 가능한 현장 플레이어가 없다", this);
            return false;
        }

        m_forced = true;
        Debug.Log("[세력 소탕] 강제 발동 준비 — 라운드 게이트·수감자 조건을 건너뛴다");
        return true;
    }

    public override void ServerBegin()
    {
        if (!TryPickJailedFaction(out m_faction))
            m_faction = PickAnyFaction();

        base.ServerBegin();

        if (!IsActive)
        {
            m_forced = false;
            return;
        }

        if (!m_forced)
            m_lastTriggeredRound = CurrentRound;
        m_forced = false;

        Debug.Log($"[세력 소탕] 복수대가 현장으로 몰려온다 ({CurrentRound}라운드) — 발동 근거: 유치장의 {m_faction} 수감자");
    }

    /// <summary>라운드 종료 시 스폰물을 정리하고 라운드당 1회 게이트를 푼다.</summary>
    public override void ServerReset()
    {
        base.ServerReset();
        m_lastTriggeredRound = k_neverTriggered;
    }

    /// <summary>외형을 한 모델로 고정한다 — 프리팹 기본은 무작위 바디라 그대로 두면 잡다한 군중이 된다.</summary>
    protected override void OnSpawned(NpcController npc)
    {
        if (string.IsNullOrEmpty(m_modelName))
            return;

        NpcCatalogAppearance appearance = npc.GetComponent<NpcCatalogAppearance>();
        int index = IndexOfModel(appearance, m_modelName);
        if (index >= 0)
        {
            appearance.SetModelIndex(index);
            return;
        }

        Debug.LogWarning(
            $"FactionRevengeEvent: 외형 모델 '{m_modelName}'을 카탈로그에서 찾지 못해 프리팹 기본 외형으로 둔다", this);
    }

    protected override void ApplyBehavior(NpcController npc)
    {
        npc.Reaction.StartResist(m_threat, relentless: true);
    }

    private static bool TryPickJailedFaction(out OfficialRecords.Faction faction)
    {
        faction = OfficialRecords.Faction.None;

        JailZone jail = App.Game.Jail;
        if (jail == null)
            return false;

        int candidates = 0;
        foreach (NpcController inmate in jail.Inmates)
        {
            if (inmate == null)
                continue;

            CitizenIdentity identity = inmate.GetComponent<CitizenIdentity>();
            if (identity == null || identity.Profile == null)
                continue;

            if (identity.Profile.Faction == OfficialRecords.Faction.None)
                continue;

            candidates++;
            if (Random.Range(0, candidates) == 0)
                faction = identity.Profile.Faction;
        }

        return candidates > 0;
    }

    private static OfficialRecords.Faction PickAnyFaction()
    {
        var all = (OfficialRecords.Faction[])System.Enum.GetValues(typeof(OfficialRecords.Faction));

        int candidates = 0;
        OfficialRecords.Faction picked = OfficialRecords.Faction.None;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == OfficialRecords.Faction.None)
                continue;

            candidates++;
            if (Random.Range(0, candidates) == 0)
                picked = all[i];
        }

        return picked;
    }

    private static int IndexOfModel(NpcCatalogAppearance appearance, string modelName)
    {
        AppearanceModelCatalog catalog = appearance != null ? appearance.Catalog : null;
        if (catalog == null)
            return -1;

        int suffixMatch = -1;
        int suffixCount = 0;
        for (int i = 0; i < catalog.Count; i++)
        {
            string name = catalog.GetModelName(i);
            if (string.IsNullOrEmpty(name))
                continue;

            if (string.Equals(name, modelName, System.StringComparison.OrdinalIgnoreCase))
                return i;

            if (!name.EndsWith(modelName, System.StringComparison.OrdinalIgnoreCase))
                continue;

            suffixCount++;
            if (suffixMatch < 0)
                suffixMatch = i;
        }

        if (suffixCount > 1)
            Debug.LogWarning($"FactionRevengeEvent: '{modelName}'이 접미사로 {suffixCount}개 모델에 걸려 "
                + $"'{catalog.GetModelName(suffixMatch)}'을 골랐다 — 전체 이름으로 적을 것");

        return suffixMatch;
    }
}
