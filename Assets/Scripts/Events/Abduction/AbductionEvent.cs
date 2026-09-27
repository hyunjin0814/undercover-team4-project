using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 혼자 다니는 현장 플레이어를 납치범 NPC 2명이 붙잡아 맨홀로 끌고 가는 돌발 이벤트(GDD 6-4).
/// 이 파일은 발동 조건·스폰·수명을, AbductionEvent.Carry.cs는 포획 이후를 담당한다. 서버(또는 오프라인) 전용.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public partial class AbductionEvent : MonoBehaviour, ISuddenEvent
{
    [Header("표시")]
    [SerializeField] private string m_displayName = "납치";

    [Header("납치범")]
    [Tooltip("납치범으로 스폰할 NPC 프리팹 — 시민과 같은 프리팹을 쓰면 구분되지 않는다")]
    [SerializeField] private NpcController m_abductorPrefab;

    [Tooltip("한 번에 몇 명이 달려드는가. 2명이면 양옆에서 끌고 가는 대형이 된다")]
    [Min(1)]
    [SerializeField] private int m_abductorCount = 2;

    private const float k_spawnSlotSpacing = 1.2f;

    [Tooltip("표적에서 이 거리(m) 밖에 스폰한다 — 눈앞 팝인 방지. 2명이 이 한 지점에서 함께 나온다")]
    [SerializeField] private float m_spawnDistanceMin = 10f;

    [SerializeField] private float m_spawnDistanceMax = 18f;

    [SerializeField] private float m_navSampleMaxDistance = 4f;

    [SerializeField] private int m_maxSpawnAttempts = 8;

    [Header("경범죄 수익 (검거 시)")]
    [Tooltip("납치범을 검거하면 지급될 보상 범위 — 스폰 시점에 확정한다(재검거 리롤 방지, #395)")]
    [SerializeField] private int m_rewardMin = 500;

    [SerializeField] private int m_rewardMax = 4000;

    [Header("혼자 판정")]
    [Tooltip("표적 선정 — 반경 안에 동료가 없는 상태가 일정 시간 이어진 현장 플레이어를 고른다")]
    [SerializeField] private LonePlayerWatch m_loneWatch = new LonePlayerWatch();

    [Header("맨홀 지점")]
    [Tooltip("끌고 갈 목적지 후보. 붙잡힌 자리에서 가장 가까운 지점을 고른다 — NavMesh 위에 둘 것. 비우면 발동하지 않는다")]
    [SerializeField] private Transform[] m_outskirtPoints;

    [Header("호송")]
    [SerializeField] private float m_convergeArriveDistance = 2.5f;
    [SerializeField] private float m_convergeTimeoutSeconds = 20f;
    [SerializeField] private float m_carrierGap = 1.1f;
    [SerializeField] private float m_arriveDistance = 2f;
    [SerializeField] private float m_travelTimeoutSeconds = 90f;

    [Header("맨홀 결말")]
    [Tooltip("뚜껑이 열리는 동안 기다리는 시간(초) — 이 구간이 마지막 구조 창이다 (#775, 잠정치)")]
    [Min(0.1f)]
    [SerializeField] private float m_manholeOpenSeconds = 3f;

    [Tooltip("맨홀 아래로 내려가는 깊이(m) — 지형 밑으로 충분히 내려가 보이지 않을 만큼")]
    [Min(0.1f)]
    [SerializeField] private float m_descendDepth = 3f;

    [Tooltip("내려가는 속도(m/s) — NavMesh 밖이라 에이전트가 아니라 이 값으로 직접 민다")]
    [Min(0.1f)]
    [SerializeField] private float m_descendSpeed = 2f;

    [Tooltip("내려가기 전에 피해자 시점을 지상 3인칭으로 빼 두는 시간(초) — 전환 보간이 끝날 만큼 (#775)")]
    [Min(0f)]
    [SerializeField] private float m_descendViewLeadSeconds = 0.6f;

    [Header("수명")]
    [Tooltip("이 시간(초) 안에 붙잡지 못하면 납치범이 포기한다 — 잔류 시민으로 남아 언제든 검거 가능")]
    [SerializeField] private float m_maxChaseSeconds = 60f;

    private readonly List<NpcController> m_abductors = new List<NpcController>();

    private bool m_active;

    private Transform m_forcedTarget;

    private Transform m_chaseTarget;
    private Transform m_carryTarget;
    private float m_chaseDeadline;

    private bool m_descending;

    private bool m_finishing;

    public string DisplayName => m_displayName;

    public bool IsActive => m_active;

    public bool AnnounceOnBegin => true;

    public string NoticeKey => "Hud.Event.Notice.Abduction";

    private static bool HasServerAuthority =>
        NetworkManager.Singleton == null
        || !NetworkManager.Singleton.IsListening
        || NetworkManager.Singleton.IsServer;

    private void Awake()
    {
        m_loneWatch.ResolveSceneRefs();

        PlayerIncapacitation.OnAnyIncapacitatedChanged += HandleVictimCauseChanged;

        if (m_outskirtPoints == null || m_outskirtPoints.Length == 0)
            Debug.LogWarning("AbductionEvent: 맨홀 지점이 배선되지 않아 발동하지 않는다", this);
    }

    private void OnDestroy()
    {
        PlayerIncapacitation.OnAnyIncapacitatedChanged -= HandleVictimCauseChanged;

        for (int i = 0; i < m_abductors.Count; i++)
        {
            NpcController abductor = m_abductors[i];
            if (abductor == null)
                continue;

            abductor.Penalty.OnPenaltyCaught -= HandleAbductionCaught;
            abductor.Health.OnDamaged -= HandleAbductorDamaged;
            abductor.Stun.OnStunned -= HandleAbductorStunned;
        }

        m_abductors.Clear();
    }

    private void Update()
    {
        if (!HasServerAuthority)
            return;

        if (m_active)
            return;

        m_loneWatch.Tick(Time.deltaTime);
    }

    public bool CanTrigger()
    {
        if (m_abductorPrefab == null || m_outskirtPoints == null || m_outskirtPoints.Length == 0)
            return false;

        return m_loneWatch.FindTarget() != null;
    }

    /// <summary>강제 발동 시 혼자 20초 조건 없이 표적을 하나 고른다(개발자 단축키 전용).</summary>
    public bool ServerPrepareForceTrigger()
    {
        if (m_abductorPrefab == null || m_outskirtPoints == null || m_outskirtPoints.Length == 0)
            return false;

        m_forcedTarget = m_loneWatch.FindForcedTarget();
        if (m_forcedTarget == null)
        {
            Debug.LogWarning("AbductionEvent: 강제 발동할 표적이 없다 — 살아 있는 플레이어가 없다", this);
            return false;
        }

        Debug.Log($"[납치] 강제 발동 준비 — 표적 {m_forcedTarget.name} (혼자 판정을 건너뛴다)");
        return true;
    }

    public void ServerBegin()
    {
        Transform target = m_forcedTarget != null ? m_forcedTarget : m_loneWatch.FindTarget();
        m_forcedTarget = null;

        if (target == null)
            return;

        if (!TryFindGroupSpawnPosition(target, out Vector3 groupPosition))
        {
            Debug.LogWarning("AbductionEvent: 스폰 지점을 찾지 못해 발동 취소", this);
            return;
        }

        m_abductors.Clear();
        for (int i = 0; i < m_abductorCount; i++)
        {
            NpcController abductor = SpawnAbductor(groupPosition, target.position, i);
            if (abductor != null)
                m_abductors.Add(abductor);
        }

        if (m_abductors.Count == 0)
        {
            Debug.LogWarning("AbductionEvent: 납치범을 한 명도 스폰하지 못해 발동 취소", this);
            return;
        }

        for (int i = 0; i < m_abductors.Count; i++)
        {
            m_abductors[i].Penalty.OnPenaltyCaught += HandleAbductionCaught;
            m_abductors[i].Health.OnDamaged += HandleAbductorDamaged;
            m_abductors[i].Stun.OnStunned += HandleAbductorStunned;
            m_abductors[i].Penalty.StartPenaltyChase(target, NpcDutyKind.Abduction);
        }

        if (m_abductors.Count < m_abductorCount)
        {
            Debug.LogWarning(
                $"AbductionEvent: 납치범 {m_abductorCount}명 중 {m_abductors.Count}명만 스폰됐다 — 스폰 지점을 못 찾았다", this);
        }

        m_active = true;
        m_chaseTarget = target;
        m_chaseDeadline = Time.time + m_maxChaseSeconds;
        m_loneWatch.Reset();

        Debug.Log($"[납치] 발동 — 표적 {target.name}, 납치범 {m_abductors.Count}명");
    }

    private bool TryFindGroupSpawnPosition(Transform target, out Vector3 position)
    {
        int areaMask = SuddenEventUtil.SpawnAreaMask(m_abductorPrefab);

        return SuddenEventUtil.TryFindSpawnPositionNear(
                   target.position, m_spawnDistanceMin, m_spawnDistanceMax,
                   m_navSampleMaxDistance, m_maxSpawnAttempts, areaMask,
                   out position, hiddenFromPlayers: true)
               || SuddenEventUtil.TryFindSpawnPositionNear(
                   target.position, m_spawnDistanceMin, m_spawnDistanceMax,
                   m_navSampleMaxDistance, m_maxSpawnAttempts, areaMask,
                   out position, hiddenFromPlayers: false);
    }

    private NpcController SpawnAbductor(Vector3 groupPosition, Vector3 targetPosition, int index)
    {
        Vector3 toTarget = targetPosition - groupPosition;
        toTarget.y = 0f;
        Vector3 forward = toTarget.sqrMagnitude > 0.01f ? toTarget.normalized : Vector3.forward;
        Vector3 side = Vector3.Cross(Vector3.up, forward);

        float slot = (index - (m_abductorCount - 1) * 0.5f) * k_spawnSlotSpacing;
        Vector3 spawnPosition = groupPosition + side * slot;

        if (NavMesh.SamplePosition(spawnPosition, out NavMeshHit hit, m_navSampleMaxDistance, NavMesh.AllAreas))
            spawnPosition = hit.position;
        else
            spawnPosition = groupPosition;

        NpcController abductor = Instantiate(
            m_abductorPrefab, spawnPosition, Quaternion.LookRotation(forward, Vector3.up));

        abductor.gameObject.AddComponent<MisdemeanorOffender>().Reward =
            BountyRoll.Roll(m_rewardMin, m_rewardMax);

        if (SuddenEventUtil.IsNetworkSessionActive)
            abductor.GetComponent<NetworkObject>().Spawn();

        return abductor;
    }

    public void ServerTick()
    {
        if (!m_active)
            return;

        PruneDead(m_abductors);

        if (m_abductors.Count == 0 && m_carryTarget == null)
        {
            Debug.Log("[납치] 종료 — 납치범이 남지 않았다");
            Finish();
            return;
        }

        if (m_carryTarget == null && Time.time >= m_chaseDeadline)
        {
            Debug.Log("[납치] 종료 — 제 시간에 붙잡지 못해 포기");
            ReleaseAllAbductors();
            Finish();
        }
    }

    public void ServerReset()
    {
        if (m_descending)
        {
            DisposeAbductors();
            CloseAllManholes();
            Finish();
            return;
        }

        Transform released = m_carryTarget;
        m_carryTarget = null;

        if (released != null)
        {
            PlayerIncapacitation incap = released.GetComponent<PlayerIncapacitation>();
            if (incap != null && incap.Cause == IncapacitationCause.Abducted)
                incap.Recover();
        }

        ReleaseAllAbductors();
        CloseAllManholes();
        Finish();
    }

    private void CloseAllManholes()
    {
        if (m_outskirtPoints == null)
            return;

        for (int i = 0; i < m_outskirtPoints.Length; i++)
        {
            if (m_outskirtPoints[i] == null)
                continue;

            AbductionManhole manhole = m_outskirtPoints[i].GetComponentInChildren<AbductionManhole>();
            if (manhole != null)
                manhole.ServerClose();
        }
    }

    private void ReleaseAbductor(NpcController abductor)
    {
        if (abductor != null)
        {
            abductor.Penalty.OnPenaltyCaught -= HandleAbductionCaught;
            abductor.Health.OnDamaged -= HandleAbductorDamaged;
            abductor.Stun.OnStunned -= HandleAbductorStunned;
            abductor.Penalty.EndPenaltyDuty();
            MisdemeanorLoiterer.Attach(abductor, m_displayName);
        }

        m_abductors.Remove(abductor);
    }

    private void ReleaseAllAbductors()
    {
        for (int i = m_abductors.Count - 1; i >= 0; i--)
            ReleaseAbductor(m_abductors[i]);
    }

    /// <summary>하강을 마친(또는 실패한) 납치범을 씬에서 제거한다.</summary>
    private void DisposeAbductors()
    {
        for (int i = m_abductors.Count - 1; i >= 0; i--)
        {
            NpcController abductor = m_abductors[i];
            if (abductor == null)
                continue;

            abductor.Penalty.OnPenaltyCaught -= HandleAbductionCaught;
            abductor.Health.OnDamaged -= HandleAbductorDamaged;
            abductor.Stun.OnStunned -= HandleAbductorStunned;

            foreach (PlayerEscorter escorter in PlayerEscorter.FindEscortersOf(abductor))
                escorter.ReleaseDrag(abductor);

            SuddenEventUtil.DespawnOrDestroy(abductor.gameObject, playVfx: false);
        }

        m_abductors.Clear();
    }

    private void Finish()
    {
        m_active = false;
        m_chaseTarget = null;
        m_descending = false;
        m_finishing = false;
        m_loneWatch.Reset();
    }

    private static void PruneDead(List<NpcController> list) => list.RemoveAll(npc => npc == null);
}
