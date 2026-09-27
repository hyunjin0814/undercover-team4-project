using System.Collections.Generic;
using Unity.AI.Navigation;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 맵 전체 간격으로 랜덤 레인에서 차를 배출하는 스포너. 풀은 NGO 프리팹 핸들러에 연결된다.
/// 전 피어에 있어야 하며 배출 스케줄은 서버에서만 돈다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class TrafficManager : MonoBehaviour
{
    [Header("차량 프리팹")]
    [Tooltip("레인이 차종을 지정하지 않으면(-1) 이 목록에서 매번 랜덤으로 고른다. 전부 DefaultNetworkPrefabs에 등록돼 있어야 한다")]
    [SerializeField] private TrafficVehicle[] m_vehiclePrefabs;

    [Header("레인")]
    [Tooltip("비워 두면 이 오브젝트의 자식에서 TrafficLane을 전부 모아 쓴다")]
    [SerializeField] private TrafficLane[] m_lanes;

    [Header("배출 간격")]
    [Tooltip("맵 전체에서 차 한 대가 나오는 평균 간격(초) — 레인당이 아니다 (#673). 이 값이 곧 플레이어가 차를 보는 빈도다")]
    [Min(0.5f)]
    [SerializeField] private float m_spawnIntervalSeconds = 10f;

    [Tooltip("위 간격에 얹는 흔들림(비율) — 0.25면 ±25%(7.5~12.5초)다. 0이면 정확히 같은 간격으로 나와 박자가 읽힌다")]
    [Range(0f, 0.9f)]
    [SerializeField] private float m_intervalJitter = 0.25f;

    [Tooltip("한 번의 배출에서 내보내는 대수 — 최소/최대 사이에서 매번 뽑는다. 2 이상이면 서로 다른 레인에 동시에 나온다 (같은 레인에 겹쳐 내면 앞뒤로 붙는다)")]
    [Min(1)]
    [SerializeField] private int m_vehiclesPerSpawnMin = 2;

    [Min(1)]
    [SerializeField] private int m_vehiclesPerSpawnMax = 3;

    [Header("배출 간격 하한의 근거")]
    [Tooltip("건너는 사람의 이동 속도(m/s) — 전력질주(8)가 아니라 걷기 기준이어야 걸어서 건너는 사람도 산다")]
    [Min(0.1f)]
    [SerializeField] private float m_crossSpeed = 5f;

    [Tooltip("건너는 시간 위에 더하는 여유(초) — 차를 보고 건널지 말지 판단하는 시간이다")]
    [Min(0f)]
    [SerializeField] private float m_gapMarginSeconds = 2f;

    [Header("풀")]
    [Tooltip("(차종마다 미리 만들어 둘 인스턴스 수 — 첫 배출의 Instantiate 히칭을 없앤다.\n" +
            "⚠ 0으로 두는 것이 맞다 (#634, 2026-08-13 확정). 0보다 크면 Awake가 맵 씬 안에 비활성 " +
            "NetworkObject를 만들어 두는데, NGO의 씬 동기화가 그걸 'in-scene placed'로 입양해 버린다. " +
            "그러면 클라의 despawn이 프리팹 핸들러를 건너뛰어 차가 풀로 안 돌아오고, 같은 인스턴스가 " +
            "여러 NetworkObjectId로 스폰돼 \\\"Object-N is already spawned!\\\"가 쏟아진다. " +
            "되살리려면 VehiclePool.Prewarm 주석을 먼저 읽을 것")]
    [Min(0)]
    [SerializeField] private int m_prewarmPerPrefab;

    [Header("교통 on/off")]
    [Tooltip("끄면 새 차가 나오지 않는다 (디버그·튜토리얼용). 이미 달리는 차는 끝까지 간다")]
    [SerializeField] private bool m_enabled = true;

    private RoundManager Round => App.Game.Round;

    private readonly List<VehiclePool> m_pools = new List<VehiclePool>();
    private readonly List<ActiveVehicle> m_active = new List<ActiveVehicle>();

    private float m_nextSpawnAt;
    private float[] m_lastSpawnAt;
    private readonly List<int> m_eligible = new List<int>();
    private bool m_flowing;
    private bool m_handlersRegistered;
    private RoundPhase m_lastPhase = RoundPhase.Preparing;

    private static bool IsAuthority
    {
        get
        {
            NetworkManager net = NetworkManager.Singleton;
            return net == null || !net.IsListening || net.IsServer;
        }
    }

    private static bool IsNetworkSessionActive =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    private void Awake()
    {
        if (m_lanes == null || m_lanes.Length == 0)
            m_lanes = GetComponentsInChildren<TrafficLane>(true);

        m_lastSpawnAt = new float[m_lanes.Length];

        ResolveLaneCrossWidths();
        BuildPools();
    }

    private void ResolveLaneCrossWidths()
    {
        NavMeshModifierVolume[] volumes = FindObjectsByType<NavMeshModifierVolume>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None
        );

        for (int i = 0; i < m_lanes.Length; i++)
        {
            if (m_lanes[i] != null)
                m_lanes[i].ResolveCrossWidth(volumes);
        }
    }

    private void Start() => EnsureHandlersRegistered();

    private void OnDestroy()
    {
        NetworkManager net = NetworkManager.Singleton;
        if (m_handlersRegistered && net != null && net.PrefabHandler != null)
        {
            for (int i = 0; i < m_pools.Count; i++)
                net.PrefabHandler.RemoveHandler(m_pools[i].Prefab.gameObject);
        }
        m_handlersRegistered = false;
    }

    private void BuildPools()
    {
        if (m_vehiclePrefabs == null)
            return;

        for (int i = 0; i < m_vehiclePrefabs.Length; i++)
        {
            TrafficVehicle prefab = m_vehiclePrefabs[i];
            if (prefab == null)
                continue;

            var pool = new VehiclePool(prefab);
            pool.Prewarm(m_prewarmPerPrefab);
            m_pools.Add(pool);
        }

        if (m_pools.Count == 0)
            Debug.LogWarning("TrafficManager: 차량 프리팹이 하나도 없다 — 도로가 비어 있게 된다", this);
    }

    private void EnsureHandlersRegistered()
    {
        if (m_handlersRegistered)
            return;

        NetworkManager net = NetworkManager.Singleton;
        if (net == null || net.PrefabHandler == null)
            return;

        for (int i = 0; i < m_pools.Count; i++)
            net.PrefabHandler.AddHandler(m_pools[i].Prefab.gameObject, m_pools[i]);

        m_handlersRegistered = true;
    }

    private void Update()
    {
        EnsureHandlersRegistered();

        if (!IsAuthority)
            return;

        RoundManager round = Round;
        RoundPhase phase = round != null ? round.Phase : RoundPhase.InProgress;
        if (phase != m_lastPhase)
        {
            HandlePhaseChanged(phase);
            m_lastPhase = phase;
        }

        if (!m_flowing)
            return;

        RecycleFinished();
        TrySpawn();
    }

    private void HandlePhaseChanged(RoundPhase phase)
    {
        if (phase == RoundPhase.InProgress)
        {
            m_flowing = true;

            m_nextSpawnAt = Time.time + Random.Range(0f, NextInterval());

            for (int i = 0; i < m_lastSpawnAt.Length; i++)
                m_lastSpawnAt[i] = float.NegativeInfinity;
            return;
        }

        m_flowing = false;
        RecycleAll();
    }

    private float NextInterval() =>
        m_spawnIntervalSeconds * Random.Range(1f - m_intervalJitter, 1f + m_intervalJitter);

    private void TrySpawn()
    {
        if (!m_enabled || m_pools.Count == 0 || Time.time < m_nextSpawnAt)
            return;

        int count = Random.Range(m_vehiclesPerSpawnMin, Mathf.Max(m_vehiclesPerSpawnMin, m_vehiclesPerSpawnMax) + 1);

        for (int i = 0; i < count; i++)
        {
            TrafficLane lane = PickLane();

            if (lane == null)
                break;

            SpawnOn(lane);
        }

        m_nextSpawnAt = Time.time + NextInterval();
    }

    private TrafficLane PickLane()
    {
        m_eligible.Clear();

        for (int i = 0; i < m_lanes.Length; i++)
        {
            TrafficLane lane = m_lanes[i];
            if (lane == null)
                continue;

            if (Time.time - m_lastSpawnAt[i] >= lane.MinGapSeconds(m_crossSpeed, m_gapMarginSeconds))
                m_eligible.Add(i);
        }

        if (m_eligible.Count == 0)
            return null;

        int picked = m_eligible[Random.Range(0, m_eligible.Count)];
        m_lastSpawnAt[picked] = Time.time;
        return m_lanes[picked];
    }

    private void SpawnOn(TrafficLane lane)
    {
        Vector3 direction = lane.Direction;
        if (direction == Vector3.zero)
        {
            Debug.LogWarning($"TrafficLane '{lane.name}': 진행 방향이 수평이 아니다 — 배출을 건너뛴다", lane);
            return;
        }

        ServerSpawnRunaway(lane.StartPoint, direction, lane.RunDistance, lane.Speed, lane.VehicleIndex);
    }

#if UNITY_EDITOR
    /// <summary>[개발용] 원하는 위치에서 차 한 대를 풀에서 꺼내 달리게 한다. 서버·오프라인 전용.</summary>
    internal TrafficVehicle DevSpawnRunaway(
        Vector3 start,
        Vector3 direction,
        float runDistance,
        float speed
    ) => ServerSpawnRunaway(start, direction, runDistance, speed, vehicleIndex: -1);
#endif

    private TrafficVehicle ServerSpawnRunaway(
        Vector3 start,
        Vector3 direction,
        float runDistance,
        float speed,
        int vehicleIndex
    )
    {
        VehiclePool pool = PickPool(vehicleIndex);
        if (pool == null)
            return null;

        Quaternion rotation = Quaternion.LookRotation(direction, Vector3.up);
        TrafficVehicle vehicle = pool.Rent(start, rotation);

        if (IsNetworkSessionActive)
        {
            NetworkObject netObj = vehicle.GetComponent<NetworkObject>();

            if (netObj != null && netObj.IsSpawned)
                Debug.LogError($"TrafficManager: 이미 스폰된 차를 다시 배출하려 했다 ({vehicle.name})", vehicle);
            else if (netObj != null)
                netObj.Spawn(destroyWithScene: true);
        }

        vehicle.ServerBeginRun(runDistance, speed);
        m_active.Add(new ActiveVehicle(vehicle, pool));
        return vehicle;
    }

    private VehiclePool PickPool(int index)
    {
        if (m_pools.Count == 0)
            return null;
        if (index < 0)
            return m_pools[Random.Range(0, m_pools.Count)];

        return index < m_pools.Count ? m_pools[index] : m_pools[m_pools.Count - 1];
    }

    private void RecycleFinished()
    {
        for (int i = m_active.Count - 1; i >= 0; i--)
        {
            TrafficVehicle vehicle = m_active[i].Vehicle;
            if (vehicle != null && !vehicle.IsFinished)
                continue;

            Recycle(m_active[i]);
            m_active.RemoveAt(i);
        }
    }

    private void RecycleAll()
    {
        for (int i = m_active.Count - 1; i >= 0; i--)
            Recycle(m_active[i]);

        m_active.Clear();
    }

    private void Recycle(ActiveVehicle entry)
    {
        TrafficVehicle vehicle = entry.Vehicle;
        if (vehicle == null)
            return;

        NetworkObject netObj = vehicle.GetComponent<NetworkObject>();
        if (netObj != null && netObj.IsSpawned)
        {
            netObj.Despawn(destroy: true);
            return;
        }

        entry.Pool.Return(vehicle);
    }

    private readonly struct ActiveVehicle
    {
        public readonly TrafficVehicle Vehicle;
        public readonly VehiclePool Pool;

        public ActiveVehicle(TrafficVehicle vehicle, VehiclePool pool)
        {
            Vehicle = vehicle;
            Pool = pool;
        }
    }

    private sealed class VehiclePool : INetworkPrefabInstanceHandler
    {
        private static readonly Vector3 k_parkPosition = new Vector3(0f, -1000f, 0f);

        private readonly TrafficVehicle m_prefab;
        private readonly Queue<TrafficVehicle> m_idle = new Queue<TrafficVehicle>();

        public VehiclePool(TrafficVehicle prefab)
        {
            m_prefab = prefab;
        }

        public TrafficVehicle Prefab => m_prefab;

        /// <summary>차를 미리 만들어 둔다. 현재는 쓰지 않는다(씬 소속 문제로 풀 반납이 깨진다).</summary>
        public void Prewarm(int count)
        {
            for (int i = 0; i < count; i++)
            {
                TrafficVehicle vehicle = Object.Instantiate(m_prefab, k_parkPosition, Quaternion.identity);
                vehicle.gameObject.SetActive(false);
                m_idle.Enqueue(vehicle);
            }
        }

        public TrafficVehicle Rent(Vector3 position, Quaternion rotation)
        {
            TrafficVehicle vehicle = null;
            while (m_idle.Count > 0 && vehicle == null)
            {
                vehicle = m_idle.Dequeue();

                if (vehicle != null && IsStillSpawned(vehicle))
                {
                    Debug.LogError(
                        $"TrafficManager: 스폰 상태인 차가 풀에 있다 — 반납 경로가 어긋났다 ({vehicle.name})",
                        vehicle);
                    vehicle = null;
                }
            }

            if (vehicle == null)
                return Object.Instantiate(m_prefab, position, rotation);

            vehicle.transform.SetPositionAndRotation(position, rotation);
            vehicle.gameObject.SetActive(true);
            return vehicle;
        }

        public void Return(TrafficVehicle vehicle)
        {
            if (vehicle == null)
                return;

            if (m_idle.Contains(vehicle))
            {
                Debug.LogError($"TrafficManager: 이미 반납된 차를 또 반납했다 ({vehicle.name})", vehicle);
                return;
            }

            vehicle.gameObject.SetActive(false);
            m_idle.Enqueue(vehicle);
        }

        private static bool IsStillSpawned(TrafficVehicle vehicle)
        {
            NetworkObject netObj = vehicle.GetComponent<NetworkObject>();
            return netObj != null && netObj.IsSpawned;
        }

        NetworkObject INetworkPrefabInstanceHandler.Instantiate(
            ulong ownerClientId,
            Vector3 position,
            Quaternion rotation
        ) => Rent(position, rotation).GetComponent<NetworkObject>();

        void INetworkPrefabInstanceHandler.Destroy(NetworkObject networkObject)
        {
            if (networkObject != null)
                Return(networkObject.GetComponent<TrafficVehicle>());
        }
    }
}
