using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

/// <summary>
/// 필드에 다수의 NPC를 스폰하는 스포너. (이슈 #37)
/// 여러 스폰 포인트를 돌아가며 지정된 수만큼 NPC를 생성하고,
/// 스폰 위치는 NavMesh 위 지점으로 보정해 배회가 항상 동작하게 한다.
/// 네트워크 세션에서는 서버만 스폰하고 NetworkObject.Spawn으로 전 클라이언트에 복제한다. (#56)
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
    [Tooltip("랜덤 위치에서 이 거리(m) 안에 NavMesh가 없으면 그 위치는 버리고 다시 뽑는다")]
    [SerializeField] private float m_sampleMaxDistance = 4f;

    [Header("프레임당 스폰 수")]
    [Tooltip("한 프레임에 이 수만큼만 생성하고 다음 프레임으로 넘긴다 — 대량 스폰 시 첫 프레임 끊김(히칭) 방지")]
    [SerializeField] private int m_spawnPerFrame = 3;

    [Header("시작 시 자동 스폰")]
    [Tooltip("끄면 라운드 매니저 등 외부에서 StartSpawn()을 호출해 원하는 시점(예: 페이드인 중)에 스폰한다")]
    [SerializeField] private bool m_spawnOnStart = true;

    private readonly List<NpcController> m_spawnedNpcs = new List<NpcController>();
    private bool m_isSpawning;

    /// <summary>스폰된 NPC 목록. (#38 범인 랜덤 배정 등 후속 시스템에서 사용)</summary>
    public IReadOnlyList<NpcController> SpawnedNpcs => m_spawnedNpcs;

    /// <summary>
    /// 스폰 포인트 목록 — 일반 NPC와 같은 지점에서 등장해야 하는 시스템이 빌려 쓴다 (#231 범인 탈출의 침입자).
    /// Awake에서 자식 자동 수집이 끝난 뒤부터 유효하다.
    /// </summary>
    public IReadOnlyList<Transform> SpawnPoints => m_spawnPoints;

    /// <summary>스폰이 모두 끝났는지 여부.</summary>
    public bool IsSpawnCompleted { get; private set; }

    /// <summary>스폰 완료 이벤트 — 범인 배정(#38) 등 "전원 스폰 이후"에 시작해야 하는 시스템이 구독한다.</summary>
    public event Action OnSpawnCompleted;

    protected override void Awake()
    {
        base.Awake(); // App.Game.NpcSpawner 등록

        // 인스펙터에 스폰 포인트를 따로 지정하지 않았으면 자식들을 그대로 사용한다
        if (m_spawnPoints == null || m_spawnPoints.Length == 0)
        {
            m_spawnPoints = new Transform[transform.childCount];
            for (int i = 0; i < transform.childCount; i++)
                m_spawnPoints[i] = transform.GetChild(i);
        }
    }

    private void Start()
    {
        if (!m_spawnOnStart)
            return;

        // 네트워크 씬(NetworkManager 존재)에서는 자동 스폰 금지 — Play 직후는 아직 Host 시작 전이라
        // IsListening이 false여서 전 피어가 각자 "네트워크에 실리지 않은 로컬 NPC"를 만들어버린다.
        // 그러면 클라이언트는 자기 화면의 유령 NPC를 조준하게 되고, 네트워크 오브젝트가 아니라
        // 검거 요청을 서버로 보낼 수 없어 체포가 영원히 실패한다 (#118 회귀 원인).
        // 네트워크 씬의 스폰은 RoundManager가 서버 시작(OnServerStarted) 후 StartSpawn()으로 트리거한다.
        if (NetworkManager.Singleton != null)
        {
            Debug.LogWarning("NpcSpawner: 네트워크 씬에서는 자동 스폰을 건너뛴다 — RoundManager가 서버 시작 후 스폰을 트리거함 (m_spawnOnStart를 꺼 두는 것을 권장)", this);
            return;
        }

        StartSpawn();
    }

    /// <summary>
    /// 스폰을 시작한다. 자동 스폰을 끈 경우 라운드 매니저 등 외부에서
    /// 원하는 시점(페이드인·라운드 준비 화면 중)에 호출한다.
    /// </summary>
    public void StartSpawn()
    {
        // 네트워크 세션에서는 서버만 스폰한다 — 클라이언트는 NGO가 복제해주는 NPC를 받기만 함 (#56)
        if (IsNetworkSessionActive && !NetworkManager.Singleton.IsServer)
            return;

        if (m_isSpawning || IsSpawnCompleted)
            return;

        SpawnAllAsync().Forget();
    }

    /// <summary>
    /// 스폰 상태를 초기화한다 — 서버 재시작(Shutdown 후 재기동) 시 재스폰을 허용하기 위해 RoundManager가 호출한다.
    /// 이전 NPC를 정리하고 완료 래치(IsSpawnCompleted)를 풀어 StartSpawn()이 다시 동작하게 한다.
    /// (네트워크 재시작 시 NGO가 이미 despawn한 NPC는 null이라 파괴를 건너뛴다)
    /// </summary>
    public void ResetSpawnState()
    {
        foreach (NpcController npc in m_spawnedNpcs)
        {
            if (npc != null)
                Destroy(npc.gameObject);
        }

        m_spawnedNpcs.Clear();
        m_isSpawning = false;
        IsSpawnCompleted = false;
    }

    // 네트워크 세션이 켜져 있는지 — 꺼져 있으면 기존처럼 로컬 단독 스폰으로 동작한다
    private static bool IsNetworkSessionActive =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    // 대량 스폰 시 첫 프레임 히칭을 막기 위해 프레임당 m_spawnPerFrame마리씩 나눠 생성한다
    private async UniTaskVoid SpawnAllAsync()
    {
        if (m_npcPrefab == null || m_spawnPoints.Length == 0)
        {
            Debug.LogWarning("NpcSpawner: NPC 프리팹 또는 스폰 포인트가 설정되지 않음", this);
            return;
        }

        m_isSpawning = true;

        int spawned = 0;
        int attempts = 0;
        int maxAttempts = m_spawnCount * 10; // NavMesh 보정 실패가 반복돼도 무한 루프에 빠지지 않도록 상한을 둔다
        int spawnedThisFrame = 0;

        while (spawned < m_spawnCount && attempts < maxAttempts)
        {
            attempts++;

            // 스폰 포인트를 순환하며 사용해 특정 지점에만 몰리는 것을 막는다
            Transform point = m_spawnPoints[spawned % m_spawnPoints.Length];
            Vector2 offset = Random.insideUnitCircle * m_spawnRadius;
            Vector3 candidate = point.position + new Vector3(offset.x, 0f, offset.y);

            // 프리팹을 먼저 고른다 — 아래 NavMesh 보정에 그 에이전트의 통행 마스크를 써야 하기 때문 (#415)
            NpcController prefab = (m_npcPrefabAlt != null && Random.value < m_altRatio) ? m_npcPrefabAlt : m_npcPrefab;
            NavMeshAgent prefabAgent = prefab.GetComponent<NavMeshAgent>();
            int spawnAreaMask = prefabAgent != null ? prefabAgent.areaMask : NavMesh.AllAreas;

            // NavMesh 위 지점으로 보정 — NavMesh 밖에 스폰되면 NavMeshAgent가 동작하지 않아 배회가 멈춘다.
            // 못 가는 영역(Jail)에 붙여 놓으면 경로가 안 잡혀 그 자리에서 고착되므로 마스크를 건다 (#415)
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, m_sampleMaxDistance, spawnAreaMask))
                continue;

            Quaternion rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            // 부모를 지정하지 않고 씬 루트에 생성 — NetworkObject는 비NetworkObject 아래에
            // 부모로 붙인 채 스폰할 수 없다 (NGO가 경고 후 강제로 떼어낸다)
            NpcController npc = Instantiate(prefab, hit.position, rotation);
            // 외형 랜덤 교체는 프리팹의 NpcAppearance가 담당한다 — 서버가 뽑은 인덱스를 전 클라에 동기화 (#56)

            // 네트워크 세션이면 전 클라이언트에 복제 (서버 권위 스폰, #56)
            if (IsNetworkSessionActive)
                npc.GetComponent<NetworkObject>().Spawn(destroyWithScene: true);

            m_spawnedNpcs.Add(npc);
            spawned++;
            spawnedThisFrame++;

            // 프레임당 할당량을 채웠으면 다음 프레임으로 양보 (스포너 파괴 시 자동 취소)
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

    // 씬 뷰에서 스폰 포인트 위치와 분산 반경을 눈으로 확인할 수 있게 기즈모를 그린다
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
            // 스폰 포인트 미지정 시 실제로 사용될 자식들을 미리 보여준다
            foreach (Transform child in transform)
                Gizmos.DrawWireSphere(child.position, m_spawnRadius);
        }
    }
}
