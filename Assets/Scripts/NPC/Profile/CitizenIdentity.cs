using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NPC 한 명의 신원 — 배정된 시민 프로필과 범인 여부를 들고, 공개 가능한 신원만 NetworkVariable로 동기화한다.
/// IsCriminal·Reaction·Appearance는 서버 전용이다.
/// </summary>
public class CitizenIdentity : NetworkBehaviour
{
    [Header("공식 기록 (세력 심볼 조회용)")]
    [Tooltip("클라이언트가 동기화 수신값으로 프로필을 재조립할 때 쓴다 — NPC 프리팹에서 할당")]
    [SerializeField]
    private OfficialRecords m_officialRecords;

    private readonly NetworkVariable<CitizenData> m_syncedData = new NetworkVariable<CitizenData>();

    public CitizenProfile Profile { get; private set; }

    public bool IsAndroidBody =>
        Profile != null && Profile.CitizenType == OfficialRecords.CitizenType.Android;

    public bool IsCriminal { get; private set; }

    public AppearanceProfile Appearance { get; private set; } = AppearanceProfile.Unassigned;

    public ReactionType Reaction { get; private set; } = ReactionType.Compliant;

    public int Bounty { get; private set; }

    public WantedCondition WantedCondition { get; private set; }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
            return;

        m_syncedData.OnValueChanged += HandleSyncedDataChanged;

        if (m_syncedData.Value.IsAssigned)
            RebuildProfile(m_syncedData.Value);
    }

    public override void OnNetworkDespawn()
    {
        m_syncedData.OnValueChanged -= HandleSyncedDataChanged;
    }

    private void HandleSyncedDataChanged(CitizenData previous, CitizenData current)
    {
        if (current.IsAssigned)
            RebuildProfile(current);
    }

    private void RebuildProfile(CitizenData data)
    {
        CitizenProfile profile = ScriptableObject.CreateInstance<CitizenProfile>();
        profile.Initialize(data.Name.ToString(), data.Type, data.Faction, data.SymbolIndex, m_officialRecords);
        profile.m_nameView = data.NameView.ToString();
        Profile = profile;
    }

    /// <summary>프로필 없이 스폰됐으면(라운드 중 스폰) CriminalAssigner에 신원 배정을 요청한다.</summary>
    private void Start()
    {
        if (IsSpawned && !IsServer)
            return;

        if (Profile != null)
            return;

        CriminalAssigner assigner = App.Game.CriminalAssigner;
        if (assigner != null)
            assigner.AssignLateSpawned(this);
    }

    /// <summary>프로필과 범인 여부를 배정하고 공개 가능 부분을 동기화한다. CriminalAssigner 전용.</summary>
    public void AssignProfile(CitizenProfile profile, bool isCriminal)
    {
        Profile = profile;
        IsCriminal = isCriminal;

        if (IsSpawned && IsServer)
            m_syncedData.Value = CitizenData.FromProfile(profile);
    }

    /// <summary>프로필은 두고 범인 여부만 바꾼다(제보 전화 승격 전용).</summary>
    public void SetCriminal(bool isCriminal)
    {
        IsCriminal = isCriminal;
    }

    /// <summary>외형 특징 조합을 배정한다. AppearanceAssigner 전용. (서버 전용 — 동기화 없음)</summary>
    public void AssignAppearance(AppearanceProfile appearance)
    {
        Appearance = appearance;
    }

    /// <summary>검거 반응 유형을 배정한다. 서버 전용.</summary>
    public void AssignReaction(ReactionType reaction)
    {
        Reaction = reaction;
    }

    /// <summary>현상금을 배정한다. CriminalAssigner 전용. (서버 전용 — 동기화 없음, #395)</summary>
    public void AssignBounty(int bounty)
    {
        Bounty = bounty;
    }

    /// <summary>수배 조건을 배정한다. CriminalAssigner 전용. (서버 전용 — 동기화 없음, #766)</summary>
    public void AssignWantedCondition(WantedCondition condition)
    {
        WantedCondition = condition;
    }
}
