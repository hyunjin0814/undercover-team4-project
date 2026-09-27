using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

/// <summary>
/// 여러 스폰 포인트를 돌며 지정 수만큼 NPC를 NavMesh 위에 스폰한다.
/// 세션에서는 서버만 스폰하고 NetworkObject.Spawn으로 복제한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class NpcSpawner : CommonManagerBase
{
    [Header("NPC 프리팹")]
    [SerializeField] private NpcController m_npcPrefab;

    [Header("혼합 스폰 (선택)")]
    [Tooltip("지정하면 이 프리팹을 m_altRatio 확률로 섞어 스폰한다 (예: Generic NPC). 비우면 m_npcPrefab만 스폰")]
    [SerializeField] private NpcController m_npcPrefabAlt;

    [Range(0f, 1f)]
    [Tooltip("전체 스폰 중 m_npcPrefabAlt(Generic) 비율")]
    [SerializeField] private float m_altRatio = 0.5f;

    [Header("총 스폰 수")]
    [SerializeField] private int m_spawnCount = 15;

    [Header("스폰 포인트")]
    [Tooltip("비워두면 이 오브젝트의 자식 Transform들을 스폰 포인트로 사용한다")]
    [SerializeField] private Transform[] m_spawnPoints;

    [Header("스폰 분산 반경")]
    [Tooltip("각 스폰 포인트를 중심으로 이 반경(m) 안에 랜덤하게 흩어 배치한다")]
    [SerializeField] private float m_spawnRadius = 5f;

    [Header("NavMesh 보정 최대 거리")]
    [Tooltip("랜덤 위치를 이 거리(m) 안의 가장 가까운 통행 가능 지점으로 끌어당긴다. 그 안에 아무것도 없을 때만 위치를 버리고 다시 뽑는다")]
    [SerializeField] private float m_sampleMaxDistance = 4f;

    [Header("스냅 허용 거리")]
    [Tooltip("보정으로 후보가 수평으로 이 거리(m)보다 멀리 끌려가면 그 위치를 버리고 다시 뽑는다. 0 이하면 검사하지 않는다 (#660)")]
    [SerializeField] private float m_maxSnapDistance = 1.5f;

    [Header("도로 여유 거리")]
    [Tooltip("스폰 자리에서 이 거리(m) 안에 도로가 있으면 버리고 다시 뽑는다. 연석에 발을 걸친 채 시작해 차에 치이는 것을 막는다. 0 이하면 검사하지 않는다 (#660)")]
    [SerializeField] private float m_roadClearance = 0.5f;

    [Header("최소 스폰 간격")]
    [Tooltip("이미 스폰된 NPC와 이 거리(m)보다 가까우면 그 위치를 버리고 다시 뽑는다. 0 이하면 검사하지 않는다 (#660)")]
    [SerializeField] private float m_minSpawnSeparation = 1.5f;

    [Header("고립 지점 검증")]
    [Tooltip("끊긴 NavMesh 조각(건물 안쪽 주머니·2층 문턱 선반)에 스폰되지 않도록, 후보에서 빠져나오는 경로가 있는지 확인하고 없으면 다시 뽑는다 (#660)")]
    [SerializeField] private bool m_validateConnectivity = true;

    [Tooltip("스폰 포인트마다 기준점을 고를 때 쓰는 탐침 수. 실측상 6 미만은 기준점 자체가 섬에 앉을 수 있다")]
    [Min(2)]
    [SerializeField] private int m_anchorProbeCount = NpcSpawnAnchors.k_defaultProbeCount;

    [Header("프레임당 스폰 수")]
    [Tooltip("한 프레임에 이 수만큼만 생성하고 다음 프레임으로 넘긴다 — 대량 스폰 시 첫 프레임 끊김(히칭) 방지")]
    [SerializeField] private int m_spawnPerFrame = 3;

    [Header("시작 시 자동 스폰")]
    [Tooltip("끄면 라운드 매니저 등 외부에서 StartSpawn()을 호출해 원하는 시점(예: 페이드인 중)에 스폰한다")]
    [SerializeField] private bool m_spawnOnStart = true;

    private readonly List<NpcController> m_spawnedNpcs = new List<NpcController>();
    private bool m_isSpawning;

    private readonly List<Vector3> m_spawnedPositions = new List<Vector3>();

    private NpcSpawnAnchors.Anchor[] m_anchors;
    private NavMeshPath m_pathBuffer;

    private bool m_spawnFrozen;

    public IReadOnlyList<NpcController> SpawnedNpcs => m_spawnedNpcs;

    public IReadOnlyList<Transform> SpawnPoints => m_spawnPoints;

    public bool IsSpawnCompleted { get; private set; }

    public event Action OnSpawnCompleted;

    protected override void Awake()
    {
        base.Awake();

        if (m_spawnPoints == null || m_spawnPoints.Length == 0)
        {
            m_spawnPoints = new Transform[transform.childCount];
            for (int i = 0; i < transform.childCount; i++)
                m_spawnPoints[i] = transform.GetChild(i);
        }

        int validCount = 0;
        for (int i = 0; i < m_spawnPoints.Length; i++)
        {
            if (m_spawnPoints[i] != null)
                m_spawnPoints[validCount++] = m_spawnPoints[i];
        }

        if (validCount != m_spawnPoints.Length)
        {
            Debug.LogWarning($"NpcSpawner: 스폰 포인트 {m_spawnPoints.Length - validCount}칸이 비어 있어 제외한다", this);
            Array.Resize(ref m_spawnPoints, validCount);
        }
    }

    private void Start()
    {
        if (!m_spawnOnStart)
            return;

        if (NetworkManager.Singleton != null)
        {
            Debug.LogWarning("NpcSpawner: 네트워크 씬에서는 자동 스폰을 건너뛴다 — RoundManager가 서버 시작 후 스폰을 트리거함 (m_spawnOnStart를 꺼 두는 것을 권장)", this);
            return;
        }

        StartSpawn();
    }

    /// <summary>NPC 스폰을 시작한다. spawnFrozen이면 정지 상태로 스폰한다.</summary>
    public void StartSpawn(bool spawnFrozen = false)
    {
        if (IsNetworkSessionActive && !NetworkManager.Singleton.IsServer)
            return;

        if (m_isSpawning || IsSpawnCompleted)
            return;

        m_spawnFrozen = spawnFrozen;
        SpawnAllAsync().Forget();
    }

    private static bool IsNetworkSessionActive =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    private async UniTaskVoid SpawnAllAsync()
    {
        if (m_npcPrefab == null || m_spawnPoints.Length == 0)
        {
            Debug.LogWarning("NpcSpawner: NPC 프리팹 또는 스폰 포인트가 설정되지 않음", this);
            return;
        }

        m_isSpawning = true;

        NavMeshAgent baseAgent = m_npcPrefab.GetComponent<NavMeshAgent>();
        int agentAreaMask = baseAgent != null ? baseAgent.areaMask : NavMesh.AllAreas;

        if (m_validateConnectivity)
            ResolveAnchors(agentAreaMask);

        int spawned = 0;
        int attempts = 0;
        int maxAttempts = m_spawnCount * 20;
        int spawnedThisFrame = 0;

        while (spawned < m_spawnCount && attempts < maxAttempts)
        {
            attempts++;

            int pointIndex = spawned % m_spawnPoints.Length;
            Transform point = m_spawnPoints[pointIndex];
            Vector2 offset = Random.insideUnitCircle * m_spawnRadius;
            Vector3 candidate = point.position + new Vector3(offset.x, 0f, offset.y);

            NpcController prefab = (m_npcPrefabAlt != null && Random.value < m_altRatio) ? m_npcPrefabAlt : m_npcPrefab;
            NavMeshAgent prefabAgent = prefab.GetComponent<NavMeshAgent>();
            int spawnAreaMask = NpcNavAreas.ExcludeSpawnAreas(
                prefabAgent != null ? prefabAgent.areaMask : NavMesh.AllAreas
            );

            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, m_sampleMaxDistance, spawnAreaMask))
                continue;

            if (m_maxSnapDistance > 0f)
            {
                Vector3 snapDelta = hit.position - candidate;
                snapDelta.y = 0f;
                if (snapDelta.sqrMagnitude > m_maxSnapDistance * m_maxSnapDistance)
                    continue;
            }

            if (m_minSpawnSeparation > 0f && IsTooCloseToSpawned(hit.position))
                continue;

            if (m_roadClearance > 0f && NpcNavAreas.HasRoadWithin(hit.position, m_roadClearance))
                continue;

            if (m_validateConnectivity && m_anchors != null && m_anchors[pointIndex].IsValid)
            {
                int pathAreaMask = prefabAgent != null ? prefabAgent.areaMask : NavMesh.AllAreas;
                if (!NpcSpawnAnchors.IsConnected(hit.position, m_anchors[pointIndex].Position, pathAreaMask, m_pathBuffer))
                    continue;
            }

            Quaternion rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            NpcController npc = Instantiate(prefab, hit.position, rotation);

            if (IsNetworkSessionActive)
                npc.GetComponent<NetworkObject>().Spawn(destroyWithScene: true);

            if (m_spawnFrozen)
                npc.SetFrozen(true);

            m_spawnedNpcs.Add(npc);
            m_spawnedPositions.Add(hit.position);
            spawned++;
            spawnedThisFrame++;

            if (spawnedThisFrame >= Mathf.Max(1, m_spawnPerFrame))
            {
                spawnedThisFrame = 0;
                await UniTask.Yield(destroyCancellationToken);
            }
        }

        if (spawned < m_spawnCount)
            Debug.LogWarning($"NpcSpawner: {m_spawnCount}마리 중 {spawned}마리만 스폰됨 — 스폰 포인트가 NavMesh 근처에 있는지 확인 필요", this);

        m_isSpawning = false;
        IsSpawnCompleted = true;
        OnSpawnCompleted?.Invoke();
    }

    private bool IsTooCloseToSpawned(Vector3 position)
    {
        float sqrMinSeparation = m_minSpawnSeparation * m_minSpawnSeparation;

        for (int i = 0; i < m_spawnedPositions.Count; i++)
        {
            if ((m_spawnedPositions[i] - position).sqrMagnitude < sqrMinSeparation)
                return true;
        }

        return false;
    }

    private void ResolveAnchors(int agentAreaMask)
    {
        m_pathBuffer = new NavMeshPath();
        m_anchors = NpcSpawnAnchors.Resolve(m_spawnPoints, m_spawnRadius, m_sampleMaxDistance,
            NpcNavAreas.ExcludeSpawnAreas(agentAreaMask), agentAreaMask, m_anchorProbeCount);

        int disconnected = NpcSpawnAnchors.CountDisconnectedPairs(m_anchors, agentAreaMask);
        if (disconnected > 0)
            Debug.LogWarning($"NpcSpawner: 기준점 {disconnected}쌍이 서로 닿지 않는다 — 스폰 포인트 하나가 고립 구역에 있을 수 있다 (#660)", this);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;

        if (m_spawnPoints != null && m_spawnPoints.Length > 0)
        {
            foreach (Transform point in m_spawnPoints)
            {
                if (point != null)
                    Gizmos.DrawWireSphere(point.position, m_spawnRadius);
            }
        }
        else
        {
            foreach (Transform child in transform)
                Gizmos.DrawWireSphere(child.position, m_spawnRadius);
        }
    }
}
