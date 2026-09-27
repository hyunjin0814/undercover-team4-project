using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using Random = UnityEngine.Random;

/// <summary>
/// 스폰 완료 후 모든 NPC에 시민 프로필을 배정하고 지정 수만큼 예비 용의자를 정한다.
/// 앞 N명만 수배로 공개하고 나머지는 제보 전화로 한 명씩 공개된다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class CriminalAssigner : CommonManagerBase
{
    private NpcSpawner Spawner => App.Game.NpcSpawner;

    [Header("공식 기록 (세력 심볼 조회용)")]
    [SerializeField]
    private OfficialRecords m_officialRecords;

    [Header("수배 용의자 (#127 · #102)")]
    [Tooltip(
        "이번 라운드의 예비 용의자 풀 크기 = 최대 수배 수. 라운드 시작에 전원 확정되지만 공개는 나눠서 된다(제보 전화). NPC 수보다 크면 NPC 수로 잘라 배정한다(경고 로그)"
    )]
    [Min(1)]
    [FormerlySerializedAs("m_criminalCount")]
    [SerializeField]
    private int m_maxWantedCount = 3;

    [Tooltip(
        "라운드 시작에 이미 수배로 공개된 용의자 수. 나머지는 미공개로 대기하다가 제보 전화를 받을 때마다 1명씩 공개된다 (#102). 0이면 수배 없이 시작한다"
    )]
    [Min(0)]
    [SerializeField]
    private int m_initialRevealCount = 1;

    [Header("현상금 (#395)")]
    [Tooltip(
        "진범 1명의 현상금 하한. 라운드 시작 배정 시점에 [하한, 상한]에서 100원 단위로 뽑아 확정한다 — 판정 시점에 뽑으면 재검거 리롤이 가능해진다"
    )]
    [Min(0)]
    [SerializeField]
    private int m_criminalBountyMin = 8000;

    [Tooltip("진범 1명의 현상금 상한")]
    [Min(0)]
    [SerializeField]
    private int m_criminalBountyMax = 15000;

    [Tooltip(
        "진범 1명이 '생포 필수(AliveOnly)'로 뽑힐 확률. 나머지는 생사 불문(DeadOrAlive)이며 시체 인계 시 감액된다 (#766)"
    )]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_aliveOnlyChance = 0.35f;

    [Header("예비 용의자 반응 가중치 (#76 · 트리거 변경 #400)")]
    [Tooltip(
        "합이 1일 필요 없음 — 비율로 추첨한다. 스캔·피격당할 때 이 유형대로 반응한다 (#400). 범인은 도주/저항 성향이 높다"
    )]
    [SerializeField]
    private float m_compliantWeight = 0.2f;

    [SerializeField]
    private float m_fleeWeight = 0.4f;

    [SerializeField]
    private float m_resistWeight = 0.4f;

    [Header("일반 시민 반응 가중치 (#78 · 재조정 #400)")]
    [Tooltip(
        "무고 시민의 반응 추첨 비율. 도주/저항은 진범을 헷갈리게 하는 미끼일 뿐 잡아도 오검거다 — 이 비율로 미끼 행동의 빈도(난이도)를 조절한다. 반응 트리거가 스캔으로 옮겨지면서(#400) 반응이 진범 tell이 될 위험이 커져 비순응 비율을 절반까지 올렸다 (GDD 6-2)"
    )]
    [SerializeField]
    private float m_citizenCompliantWeight = 0.5f;

    [SerializeField]
    private float m_citizenFleeWeight = 0.25f;

    [SerializeField]
    private float m_citizenResistWeight = 0.25f;

    private readonly List<NpcController> m_criminalNpcs = new List<NpcController>();
    private readonly List<CitizenProfile> m_wantedProfiles = new List<CitizenProfile>();

    private SuspectRevealer m_revealer;

    private int m_totalAssignedBounty;

    private CitizenProfileFactory m_factory;

    private bool m_initialAssignmentDone;

    public int TotalAssignedBounty => m_totalAssignedBounty;

    public IReadOnlyList<NpcController> CriminalNpcs => m_criminalNpcs;

    public IReadOnlyList<CitizenProfile> WantedProfiles => m_wantedProfiles;

    public event Action<IReadOnlyList<NpcController>> OnCriminalAssigned;

    protected override void Awake()
    {
        base.Awake();

        m_revealer = new SuspectRevealer(
            m_criminalNpcs,
            m_compliantWeight,
            m_fleeWeight,
            m_resistWeight,
            m_criminalBountyMin,
            m_criminalBountyMax
        );
    }

    private void Start()
    {
        if (Spawner == null)
        {
            Debug.LogWarning("CriminalAssigner: NpcSpawner를 찾지 못해 배정 불가", this);
            return;
        }

        if (Spawner.IsSpawnCompleted)
            AssignAll();
        else
            Spawner.OnSpawnCompleted += AssignAll;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();

        if (Spawner != null)
            Spawner.OnSpawnCompleted -= AssignAll;
    }

    private void AssignAll()
    {
        IReadOnlyList<NpcController> npcs = Spawner.SpawnedNpcs;
        if (npcs.Count == 0)
        {
            Debug.LogWarning("CriminalAssigner: 스폰된 NPC가 없어 배정 불가", this);
            return;
        }

        int suspectCount = Mathf.Clamp(m_maxWantedCount, 1, npcs.Count);
        if (m_maxWantedCount > npcs.Count)
            Debug.LogWarning(
                $"CriminalAssigner: 최대 수배 수({m_maxWantedCount})가 NPC 수({npcs.Count})보다 많아 {suspectCount}명으로 잘라 배정한다",
                this
            );

        int revealCount = Mathf.Clamp(m_initialRevealCount, 0, suspectCount);

        HashSet<int> criminalIndices = PickCriminalIndices(npcs.Count, suspectCount);

        m_factory = new CitizenProfileFactory(m_officialRecords);

        m_criminalNpcs.Clear();
        m_wantedProfiles.Clear();
        m_totalAssignedBounty = 0;

        var log = new AssignmentLog(npcs.Count, suspectCount, revealCount);

        for (int i = 0; i < npcs.Count; i++)
        {
            CitizenIdentity identity = npcs[i].GetComponent<CitizenIdentity>();
            if (identity == null)
            {
                Debug.LogWarning(
                    $"CriminalAssigner: {npcs[i].name}에 CitizenIdentity가 없어 신원 배정을 건너뜀 — NPC 프리팹에 부착 필요",
                    npcs[i]
                );
                continue;
            }

            CitizenProfile profile = m_factory.Create();

            bool isSuspect = criminalIndices.Contains(i);
            bool isCriminal = isSuspect && m_criminalNpcs.Count < revealCount;
            identity.AssignProfile(profile, isCriminal);

            ReactionType reaction = isCriminal
                ? ReactionRoll.Roll(m_compliantWeight, m_fleeWeight, m_resistWeight)
                : ReactionRoll.Roll(
                    m_citizenCompliantWeight,
                    m_citizenFleeWeight,
                    m_citizenResistWeight
                );
            identity.AssignReaction(reaction);

            int bounty = isCriminal ? BountyRoll.Roll(m_criminalBountyMin, m_criminalBountyMax) : 0;
            identity.AssignBounty(bounty);
            m_totalAssignedBounty += bounty;

            if (isSuspect)
            {
                identity.AssignWantedCondition(
                    !TutorialDirector.IsActive && Random.value < m_aliveOnlyChance
                        ? WantedCondition.AliveOnly
                        : WantedCondition.DeadOrAlive
                );

                m_criminalNpcs.Add(npcs[i]);
                m_wantedProfiles.Add(profile);
            }

            log.Add(identity, isSuspect);
        }

        m_initialAssignmentDone = true;

        OnCriminalAssigned?.Invoke(m_criminalNpcs);
        log.Flush(m_totalAssignedBounty);
    }

    /// <summary>라운드 시작 이후 스폰된 NPC에게 이름 등 기본 신원만 배정한다. 서버(또는 오프라인) 전용.</summary>
    public void AssignLateSpawned(CitizenIdentity identity)
    {
        if (identity == null)
            return;

        if (identity.Profile != null)
            return;

        if (!m_initialAssignmentDone || m_factory == null)
            return;

        CitizenProfile profile = m_factory.Create();

        identity.AssignProfile(profile, false);

        Debug.Log($"[신원] 늦은 배정: {identity.name} — {profile.CitizenName}");
    }

    public bool HasPendingSuspect => m_revealer != null && m_revealer.HasPending;

    /// <summary>대기 중인 예비 용의자 1명을 수배로 공개하고 현상금 총합에 반영한다. 성공하면 true.</summary>
    public bool PromoteNext()
    {
        if (m_revealer == null || !m_revealer.TryPromoteNext(out int bountyDelta))
            return false;

        m_totalAssignedBounty += bountyDelta;
        return true;
    }

    /// <summary>0~total-1 인덱스를 셔플해 앞에서 count개를 뽑는다 — 중복 없는 진범 인덱스.</summary>
    private static HashSet<int> PickCriminalIndices(int total, int count)
    {
        int[] indices = new int[total];
        for (int i = 0; i < total; i++)
            indices[i] = i;

        for (int i = total - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        var result = new HashSet<int>();
        for (int i = 0; i < count; i++)
            result.Add(indices[i]);
        return result;
    }
}
