using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 도로를 직진하다 정해진 거리 후 풀로 회수되는 차 한 대.
/// 위치는 복제하지 않고 주행 파라미터로 각 피어가 계산하며, 명중 처리는 서버와 오너가 나눠 한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(AudioSource))]
public class TrafficVehicle : NetworkBehaviour
{
    [Header("차체 판정")]
    [Tooltip("치임 판정 상자의 크기(m) — 실제 모델보다 조금 작게 두면 아슬아슬하게 피하는 맛이 산다")]
    [SerializeField] private Vector3 m_hitBoxSize = new Vector3(2.2f, 1.8f, 4.5f);

    [Header("명중 효과")]
    [Tooltip("치였을 때 사람·시민이 받는 피해 — 최대 HP(100)를 넘겨 확실히 죽인다. 좌우를 안 보고 건넌 대가라 어중간하게 깎지 않는다 (폭탄 폭심 150과 같은 결)")]
    [Min(0)]
    [SerializeField] private int m_damage = 120;

    [Tooltip("치였을 때 진행 방향으로 밀리는 세기")]
    [SerializeField] private float m_knockbackForward = 14f;

    [Tooltip("치였을 때 위로 뜨는 세기 — 0이면 바닥으로만 밀린다")]
    [SerializeField] private float m_knockbackUp = 6f;

    [Tooltip("즉사 시 래그돌 임펄스 = 넉백 벡터 × 이 값. 1.0이 넉백 그대로다")]
    [Range(0f, 3f)]
    [SerializeField] private float m_ragdollImpulseScale = 1f;

    [Header("예고")]
    [Tooltip("스폰~회수 내내 켜져 있는 헤드라이트 — 소리를 못 듣는 상황(먹통·소음)에서 유일한 예고다")]
    [SerializeField] private Light[] m_headlights;

    [Tooltip("엔진음(루프) — 차체의 AudioSource가 직접 튼다. 카탈로그에 클립이 없으면 조용히 무음")]
    [SerializeField] private EAudioClip m_engineSound = EAudioClip.VehicleEngine;

    [Tooltip("경적 연출 — FxManager 조합표에서 소리를 배선한다. None이면 울리지 않는다")]
    [SerializeField] private EFx m_hornFx = EFx.VehicleHorn;

    [Tooltip("전방 이 거리(m) 안에 플레이어가 있으면 경적을 울린다 — 22m/s에서 30m가 충돌 1.4초 전이다")]
    [Min(0f)]
    [SerializeField] private float m_hornDistance = 30f;

    [Tooltip("경적을 울릴 좌우 폭(m) — 진행선에서 이만큼 벗어난 사람은 대상이 아니다. 도로 반폭 + 여유")]
    [Min(0.5f)]
    [SerializeField] private float m_hornHalfWidth = 3f;

    [Tooltip("경적을 다시 울리는 간격(초) — 앞에 사람이 계속 있으면 이 간격으로 되풀이한다")]
    [Min(0.1f)]
    [SerializeField] private float m_hornInterval = 1.2f;

    private Vector3 m_startPoint;
    private float m_startTime;
    private float m_runDistance;
    private Vector3 m_endPoint;
    private Vector3 m_direction;
    private float m_speed;
    private bool m_driving;
    private float m_nextHornAt;

    private struct RunState : INetworkSerializable, System.IEquatable<RunState>
    {
        public Vector3 StartPoint;
        public Vector3 Direction;
        public float Speed;
        public float RunDistance;
        public int StartTick;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer)
            where T : IReaderWriter
        {
            serializer.SerializeValue(ref StartPoint);
            serializer.SerializeValue(ref Direction);
            serializer.SerializeValue(ref Speed);
            serializer.SerializeValue(ref RunDistance);
            serializer.SerializeValue(ref StartTick);
        }

        public bool Equals(RunState other) =>
            StartPoint == other.StartPoint
            && Direction == other.Direction
            && Speed == other.Speed
            && RunDistance == other.RunDistance
            && StartTick == other.StartTick;
    }

    private readonly NetworkVariable<RunState> m_runSynced = new NetworkVariable<RunState>();

    private AudioSource m_engineSource;

    private bool m_tickHooked;

    private readonly HashSet<Transform> m_hitPeople = new HashSet<Transform>();
    private readonly HashSet<NpcController> m_hitNpcs = new HashSet<NpcController>();

    private static readonly List<Transform> s_hornScan = new List<Transform>();

    private static int s_hitLayers;

    private static readonly Collider[] s_overlap = new Collider[64];

    private Collider[] m_ownColliders;

    private Collider[] OwnColliders =>
        m_ownColliders ??= GetComponentsInChildren<Collider>(includeInactive: true);

    private static readonly System.Collections.Generic.List<PlayerHealth> s_launched =
        new System.Collections.Generic.List<PlayerHealth>(6);

    private float LaunchedScanRadius => m_hitBoxSize.magnitude * 0.5f;

    private static int HitLayers
    {
        get
        {
            if (s_hitLayers == 0)
            {
                int ragdoll = LayerMask.NameToLayer("Ragdoll");
                s_hitLayers = ragdoll >= 0 ? ~(1 << ragdoll) : ~0;
            }
            return s_hitLayers;
        }
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer || NetworkManager == null)
            return;

        NetworkManager.NetworkTickSystem.Tick += OnServerTick;
        m_tickHooked = true;
    }

    public override void OnNetworkDespawn()
    {
        if (!m_tickHooked)
            return;

        if (NetworkManager != null)
            NetworkManager.NetworkTickSystem.Tick -= OnServerTick;
        m_tickHooked = false;
    }

    private void OnServerTick() => ServerDriveStep((float)NetworkManager.ServerTime.Time);

    public bool IsFinished { get; private set; }

    private void OnEnable()
    {
        SetHeadlights(true);
        PlayEngineLoop();
        GameSettings.OnSfxVolumeChanged += HandleSfxVolumeChanged;
    }

    private void OnDisable()
    {
        GameSettings.OnSfxVolumeChanged -= HandleSfxVolumeChanged;
        SetHeadlights(false);
        StopEngineLoop();

        m_hitPeople.Clear();
        m_hitNpcs.Clear();

        m_driving = false;
        m_nextHornAt = 0f;
        IsFinished = false;
    }

    /// <summary>runDistance만큼 speed로 직진을 시작한다. 서버(또는 오프라인) 전용.</summary>
    public void ServerBeginRun(float runDistance, float speed)
    {
        m_direction = transform.forward;
        m_direction.y = 0f;

        if (m_direction.sqrMagnitude < 0.001f || runDistance <= 0f || speed <= 0f)
        {
            IsFinished = true;
            return;
        }

        m_direction.Normalize();
        transform.rotation = Quaternion.LookRotation(m_direction, Vector3.up);

        m_startPoint = transform.position;
        m_runDistance = runDistance;
        m_endPoint = m_startPoint + m_direction * runDistance;
        m_speed = speed;
        m_driving = true;

        if (IsSpawned && IsServer)
        {
            int startTick = NetworkManager.ServerTime.Tick;
            m_startTime = (float)TickToSeconds(startTick);
            m_runSynced.Value = new RunState
            {
                StartPoint = m_startPoint,
                Direction = m_direction,
                Speed = speed,
                RunDistance = runDistance,
                StartTick = startTick,
            };
        }
        else
        {
            m_startTime = Time.time;
        }

        m_nextHornAt = 0f;
        IsFinished = false;
    }

    private void Update()
    {
        if (IsSpawned)
        {
            ApplyFramePosition();
            return;
        }

        if (m_driving)
            ServerDriveStep(Time.time);
    }

    /// <summary>이 프레임 시각의 위치를 주행 파라미터로 새로 계산해 그린다(누적하지 않는다).</summary>
    private void ApplyFramePosition()
    {
        if (NetworkManager == null)
            return;

        RunState run = m_runSynced.Value;
        if (run.Speed <= 0f || run.RunDistance <= 0f)
            return;

        double elapsed = NetworkManager.ServerTime.Time - TickToSeconds(run.StartTick);
        if (elapsed < 0d)
            return;

        float travelled = Mathf.Min((float)(elapsed * run.Speed), run.RunDistance);
        transform.position = run.StartPoint + run.Direction * travelled;
    }

    private double TickToSeconds(int tick)
    {
        NetworkTickSystem ticks = NetworkManager != null ? NetworkManager.NetworkTickSystem : null;
        return ticks != null && ticks.TickRate > 0 ? tick / (double)ticks.TickRate : 0d;
    }

    private void ServerDriveStep(float now)
    {
        if (!m_driving)
            return;

        float travelled = m_speed * (now - m_startTime);
        bool arrived = travelled >= m_runDistance;

        transform.position = arrived ? m_endPoint : m_startPoint + m_direction * travelled;

        ServerApplyHits();

        if (arrived)
        {
            m_driving = false;
            IsFinished = true;

            if (IsSpawned && IsServer)
                m_runSynced.Value = default;
            return;
        }

        ServerTickHorn();
    }

    private void SetHeadlights(bool on)
    {
        if (m_headlights == null)
            return;

        for (int i = 0; i < m_headlights.Length; i++)
        {
            if (m_headlights[i] != null)
                m_headlights[i].enabled = on;
        }
    }

    private void PlayEngineLoop()
    {
        if (!EnsureEngineSource())
            return;

        if (!m_engineSource.isPlaying)
            m_engineSource.Play();
    }

    private void HandleSfxVolumeChanged(float _)
    {
        if (m_engineSource != null)
            m_engineSource.volume = SoundManager.SfxVolumeOf(App.Sound?.GetSfxEntry(m_engineSound));
    }

    private void StopEngineLoop()
    {
        if (m_engineSource != null && m_engineSource.isPlaying)
            m_engineSource.Stop();
    }

    private bool EnsureEngineSource()
    {
        if (m_engineSource != null)
            return true;
        if (m_engineSound == EAudioClip.None)
            return false;

        AudioLibrary.Entry entry = App.Sound?.GetSfxEntry(m_engineSound);
        if (entry?.Clip == null)
            return false;

        AudioSource source = GetComponent<AudioSource>();
        if (source == null)
            return false;

        source.clip = entry.Clip;
        source.volume = SoundManager.SfxVolumeOf(entry);
        source.minDistance = entry.MinDistance;
        source.maxDistance = Mathf.Max(entry.MaxDistance, entry.MinDistance + 0.1f);
        source.spatialBlend = 1f;
        source.rolloffMode = AudioRolloffMode.Linear;

        source.dopplerLevel = 0f;

        source.loop = true;
        m_engineSource = source;
        return true;
    }

    /// <summary>앞에 플레이어가 있으면 쿨다운에 맞춰 경적을 울린다. 서버 전용.</summary>
    private void ServerTickHorn()
    {
        if (m_hornFx == EFx.None || Time.time < m_nextHornAt)
            return;

        if (!IsPlayerAhead())
            return;

        m_nextHornAt = Time.time + m_hornInterval;
        App.Game.Fx?.PlayEverywhere(m_hornFx, transform.position);
    }

    private bool IsPlayerAhead()
    {
        SuddenEventUtil.CollectFieldPlayers(transform.position, m_hornDistance, s_hornScan);

        for (int i = 0; i < s_hornScan.Count; i++)
        {
            Vector3 offset = s_hornScan[i].position - transform.position;
            offset.y = 0f;

            float ahead = Vector3.Dot(offset, m_direction);
            if (ahead <= 0f)
                continue;

            Vector3 lateral = offset - m_direction * ahead;
            if (lateral.sqrMagnitude <= m_hornHalfWidth * m_hornHalfWidth)
                return true;
        }

        return false;
    }

    private void ServerApplyHits()
    {
        int count = Physics.OverlapBoxNonAlloc(
            transform.position + Vector3.up * (m_hitBoxSize.y * 0.5f),
            m_hitBoxSize * 0.5f,
            s_overlap,
            transform.rotation,
            HitLayers,
            QueryTriggerInteraction.Ignore
        );

        if (count == s_overlap.Length)
            Debug.LogWarning("TrafficVehicle: 치임 판정 버퍼가 찼다 — 뒤로 밀린 대상이 잘렸을 수 있다", this);

        for (int i = 0; i < count; i++)
        {
            Collider hit = s_overlap[i];
            if (hit == null)
                continue;

            NpcController npc = hit.GetComponentInParent<NpcController>();
            if (npc != null)
            {
                ServerHitNpc(npc);
                continue;
            }

            PlayerHealth player = hit.GetComponentInParent<PlayerHealth>();
            if (player != null)
                ServerHitPlayer(player);
        }

        ServerApplyLaunchedHits();
    }

    private void ServerApplyLaunchedHits()
    {
        Vector3 center = transform.position + Vector3.up * (m_hitBoxSize.y * 0.5f);
        PlayerHealth.CollectLaunched(center, LaunchedScanRadius, s_launched);

        Vector3 half = m_hitBoxSize * 0.5f;
        Quaternion inverse = Quaternion.Inverse(transform.rotation);

        for (int i = 0; i < s_launched.Count; i++)
        {
            PlayerHealth player = s_launched[i];
            Vector3 local = inverse * (player.transform.position - center);
            if (
                Mathf.Abs(local.x) <= half.x
                && Mathf.Abs(local.y) <= half.y
                && Mathf.Abs(local.z) <= half.z
            )
                ServerHitPlayer(player);
        }
    }

    private void ServerHitNpc(NpcController npc)
    {
        if (!m_hitNpcs.Add(npc))
            return;

        bool wasAlive = !npc.Death.IsDead;
        npc.Health.TakeEnvironmentalDamage(m_damage, gameObject);

        if (!npc.Death.IsDead)
        {
            npc.Knockback.ServerApplyKnockback(BuildKnockback());
            return;
        }

        if (wasAlive && npc.Ragdoll != null)
        {
            npc.Ragdoll.IgnoreCollisionWith(OwnColliders, true);
            npc.Ragdoll.EnterRagdoll(BuildRagdollImpulse());
        }
    }

    private void ServerHitPlayer(PlayerHealth player)
    {
        if (!m_hitPeople.Add(player.transform))
            return;

        if (player.CurrentHp <= 0)
            return;

        player.TakeLethalDamage(m_damage, gameObject);

        player.GetComponent<PlayerEscorter>()?.ReleaseAllDrags();

        if (player.CurrentHp == 0)
        {
            ServerNotifyDeathRagdoll(player, BuildRagdollImpulse());
            return;
        }

        if (!IsSpawned)
        {
            player.GetComponent<PlayerMovement>()?.AddKnockback(BuildKnockback());
            return;
        }

        NetworkObject victim = player.GetComponent<NetworkObject>();
        if (victim != null)
        {
            HitClientRpc(
                BuildKnockback(),
                RpcTarget.Single(victim.OwnerClientId, RpcTargetUse.Temp)
            );
        }
    }

    private Vector3 BuildKnockback() => m_direction * m_knockbackForward + Vector3.up * m_knockbackUp;

    private Vector3 BuildRagdollImpulse() => BuildKnockback() * m_ragdollImpulseScale;

    /// <summary>차량 즉사자의 래그돌 임펄스를 전 피어에 알린다. 서버(또는 오프라인) 전용.</summary>
    private void ServerNotifyDeathRagdoll(PlayerHealth player, Vector3 impulse)
    {
        NetworkObject victim = player.GetComponent<NetworkObject>();
        if (victim == null)
        {
            ApplyDeathRagdoll(player.gameObject, impulse);
            return;
        }

        if (!IsSpawned || !IsServer)
        {
            ApplyDeathRagdoll(victim.gameObject, impulse);
            return;
        }

        ulong authority = ResolveImpulseAuthority(player, victim);

        ApplyDeathRagdoll(
            victim.gameObject,
            authority == NetworkManager.ServerClientId ? impulse : Vector3.zero
        );

        DeathRagdollRpc(victim, impulse, authority);
    }

    /// <summary>임펄스를 실어야 할 물리 권위 피어(소유권 이관 대기 중이면 옛 오너, 아니면 서버)를 판정한다.</summary>
    private static ulong ResolveImpulseAuthority(PlayerHealth player, NetworkObject victim)
    {
        PlayerIncapacitation incap = player.GetComponent<PlayerIncapacitation>();
        if (incap != null && incap.IsOwnershipHandoverPending)
            return incap.BodyOwnerClientId;

        return victim.OwnerClientId;
    }

    [Rpc(SendTo.NotServer)]
    private void DeathRagdollRpc(
        NetworkObjectReference victim,
        Vector3 impulse,
        ulong authority
    )
    {
        if (!victim.TryGet(out NetworkObject resolved))
            return;

        NetworkManager nm = NetworkManager.Singleton;
        bool mine = nm != null && nm.LocalClientId == authority;
        ApplyDeathRagdoll(resolved.gameObject, mine ? impulse : Vector3.zero);
    }

    private void ApplyDeathRagdoll(GameObject victim, Vector3 impulse)
    {
        if (victim == null || !victim.TryGetComponent(out PlayerRagdoll ragdoll))
            return;

        ragdoll.IgnoreCollisionWith(OwnColliders, true);
        ragdoll.EnterRagdoll(impulse);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void HitClientRpc(Vector3 knockback, RpcParams rpcParams)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.LocalClient.PlayerObject == null)
            return;

        nm.LocalClient.PlayerObject.GetComponent<PlayerMovement>()?.AddKnockback(knockback);
    }
}
