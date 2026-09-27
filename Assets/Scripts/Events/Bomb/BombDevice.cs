using System;
using Unity.Netcode;
using UnityEngine;

public enum BombState
{
    Idle,
    Emerging,
    Dormant,
    Armed,
    Exploded,
}

/// <summary>
/// 추격 폭탄의 상태 기계(서버 권위) — 무장하면 가장 가까운 현장 플레이어를 쫓고 제한시간이 끝나면 터진다.
/// 추격은 BombChaseDriver, 폭발은 BombBlast에 맡기는 파사드이며, 진압봉으로 맞으면 즉발한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(BombChaseDriver))]
[RequireComponent(typeof(BombBlast))]
public class BombDevice : NetworkBehaviour
{
    [Header("등장 (인스펙터 조절)")]
    [Tooltip("상자에서 나오는 데 걸리는 시간(초) — 이 동안은 움직이지도 카운트다운하지도 않는다. " +
             "연출(BombEmergeView)이 아니라 이 값이 진짜 무장 시각을 정한다")]
    [SerializeField]
    private float m_emergeSeconds = 2f;

    [Header("타이머 (인스펙터 조절)")]
    [Tooltip("무장부터 폭발까지의 제한시간(초) — 쫓기는 시간과 직결")]
    [SerializeField]
    private float m_countdownSeconds = 30f;

    [Header("테스트")]
    [Tooltip("켜면 시작 시 스스로 무장한다 — 이벤트 없이 씬에 놓고 테스트할 때 쓰는 임시 스위치")]
    [SerializeField]
    private bool m_armOnStart;

    private readonly NetworkVariable<int> m_stateSynced = new NetworkVariable<int>((int)BombState.Idle);
    private readonly NetworkVariable<double> m_explodeTimeSynced = new NetworkVariable<double>();

    private BombState m_state = BombState.Idle;
    private float m_armAtLocal;
    private float m_explodeAtLocal;

    private BombChaseDriver m_chase;
    private BombBlast m_blast;

    private bool m_explodedRaised;

    private static BombDevice s_active;

    public static BombDevice Active => s_active;

    public BombState State =>
        m_state == BombState.Exploded || !IsSpawned || IsServer ? m_state : (BombState)m_stateSynced.Value;

    public bool IsCountingDown => State == BombState.Armed;

    public bool CanBeStruck => IsCountingDown;

    public float ExplosionRadius => m_blast.ExplosionRadius;

    public float RemainingSeconds
    {
        get
        {
            if (!IsCountingDown)
                return 0f;
            if (IsSpawned && !IsServer)
            {
                double now = NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : 0d;
                return Mathf.Max(0f, (float)(m_explodeTimeSynced.Value - now));
            }
            return Mathf.Max(0f, m_explodeAtLocal - Time.time);
        }
    }

    public event Action OnExploded;

    private bool IsAuthority => !IsSpawned || IsServer;

    private void Awake()
    {
        m_chase = GetComponent<BombChaseDriver>();
        m_blast = GetComponent<BombBlast>();

        s_active = this;
    }

    public override void OnDestroy()
    {
        if (s_active == this)
            s_active = null;
        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        m_stateSynced.OnValueChanged += HandleStateSyncedChanged;

        if (!IsServer)
        {
            m_chase.DisableAgent();
            m_state = (BombState)m_stateSynced.Value;
            return;
        }

        if (m_armOnStart && m_state == BombState.Idle)
            ServerDeploy();
    }

    private void Start()
    {
        if (m_armOnStart && !SuddenEventUtil.IsNetworkSessionActive && m_state == BombState.Idle)
            ServerDeploy();
    }

    public override void OnNetworkDespawn()
    {
        m_stateSynced.OnValueChanged -= HandleStateSyncedChanged;
    }

    private void Update()
    {
        if (!IsAuthority)
            return;

        if (m_state == BombState.Emerging)
        {
            if (Time.time >= m_armAtLocal)
                SetState(BombState.Dormant);
            return;
        }

        if (m_state == BombState.Dormant)
        {
            if (m_chase.PollWakeTrigger())
            {
                ServerArm();
                return;
            }

            m_chase.TickRoam();
            return;
        }

        if (m_state != BombState.Armed)
            return;

        m_chase.Tick();

        if (Time.time >= m_explodeAtLocal)
            ServerExplode();
    }

    /// <summary>폭탄을 배치해 등장(Emerging) → 대기(Dormant) 흐름을 시작한다. 서버(또는 오프라인) 전용.</summary>
    public void ServerDeploy()
    {
        if (!IsAuthority)
            return;

        if (m_emergeSeconds <= 0f)
        {
            ServerArm();
            return;
        }

        m_armAtLocal = Time.time + m_emergeSeconds;
        SetState(BombState.Emerging);
    }

    /// <summary>폭탄을 무장하고 카운트다운을 시작한다.</summary>
    public void ServerArm()
    {
        if (!IsAuthority)
            return;

        m_explodeAtLocal = Time.time + m_countdownSeconds;
        m_chase.ResetRetargetClock();

        if (IsSpawned && IsServer)
        {
            double now = NetworkManager.Singleton != null ? NetworkManager.Singleton.ServerTime.Time : 0d;
            m_explodeTimeSynced.Value = now + m_countdownSeconds;
        }

        SetState(BombState.Armed);
    }

    /// <summary>진압봉 타격 시 그 자리에서 즉시 폭발시킨다. 카운트다운 중에만 받는다. 서버(또는 오프라인) 전용.</summary>
    public void ServerDetonate()
    {
        if (!IsAuthority)
            return;
        if (!IsCountingDown)
            return;

        Debug.Log("[폭탄] 진압봉에 맞음 — 즉발");
        ServerExplode();
    }

    private void ServerExplode()
    {
        if (m_state == BombState.Exploded)
            return;

        m_chase.Stop();
        m_blast.ServerExplode();
        SetState(BombState.Exploded);

        if (IsSpawned && IsServer)
            ExplodedClientRpc();
    }

    private void SetState(BombState state)
    {
        if (m_state == state)
            return;

        m_state = state;
        if (IsSpawned && IsServer)
            m_stateSynced.Value = (int)state;

        HandleStateEntered(state);
    }

    private void HandleStateEntered(BombState state)
    {
        if (state == BombState.Exploded)
            RaiseExplodedOnce();
    }

    private void RaiseExplodedOnce()
    {
        if (m_explodedRaised)
            return;

        m_explodedRaised = true;
        OnExploded?.Invoke();
    }

    [ClientRpc]
    private void ExplodedClientRpc()
    {
        m_state = BombState.Exploded;
        RaiseExplodedOnce();
    }

    private void HandleStateSyncedChanged(int previous, int current)
    {
        if (IsServer)
            return;
        m_state = (BombState)current;
        HandleStateEntered((BombState)current);
    }
}
