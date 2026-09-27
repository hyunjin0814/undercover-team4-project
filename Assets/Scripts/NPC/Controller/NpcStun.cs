using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

public enum NpcStunCause
{
    Knockdown,

    Taser,
}

/// <summary>
/// 기절을 FSM 전이 없이 현재 상태 위에 얹는 동기화 플래그(스턴 오버레이)로 다룬다.
/// IsStunned가 오버레이와 Stunned 상태를 함께 답한다.
/// </summary>
public class NpcStun : NetworkBehaviour
{
    private NpcController m_owner;

    private readonly NetworkVariable<bool> m_syncedStunned = new NetworkVariable<bool>();
    private bool m_stunned;

    private readonly NetworkVariable<bool> m_syncedRising = new NetworkVariable<bool>();

    public bool IsStunned => HasStunOverlay || m_owner.CurrentState == NpcState.Stunned;

    public bool IsRising => IsSpawned ? m_syncedRising.Value : m_rising;

    private bool m_rising;

    internal bool HasStunOverlay => IsSpawned ? m_syncedStunned.Value : m_stunned;

    public float StunSeconds => m_owner.StunConfig.StunSeconds;

    public event Action<bool> OnStunnedChanged;

    public event Action<float> OnTaserStunStarted;

    public event Action<NpcController, Transform> OnStunned;

    private float m_stunElapsed;
    private float m_stunDuration;
    private bool m_standingUp;
    private bool m_agentStoppedBefore;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
    }

    public override void OnNetworkSpawn()
    {
        m_syncedStunned.OnValueChanged += HandleSyncedStunnedChanged;
    }

    public override void OnNetworkDespawn()
    {
        m_syncedStunned.OnValueChanged -= HandleSyncedStunnedChanged;
    }

    internal void SetRising(bool value)
    {
        m_rising = value;
        if (IsSpawned && IsServer)
            m_syncedRising.Value = value;
    }

    private void SetStunned(bool value)
    {
        m_stunned = value;

        if (!IsSpawned)
        {
            OnStunnedChanged?.Invoke(value);
            return;
        }

        if (!IsServer)
            return;

        m_syncedStunned.Value = value;
    }

    private void HandleSyncedStunnedChanged(bool previous, bool current)
    {
        OnStunnedChanged?.Invoke(current);
    }

    private void BroadcastTaserStun(float seconds)
    {
        if (!IsSpawned)
        {
            OnTaserStunStarted?.Invoke(seconds);
            return;
        }

        TaserStunStartedRpc(seconds);
    }

    [Rpc(SendTo.Everyone)]
    private void TaserStunStartedRpc(float seconds) => OnTaserStunStarted?.Invoke(seconds);

    /// <summary>상태 전이 없이 스턴 오버레이 플래그를 켠다(테이저·체력 0).</summary>
    public void EnterStunned(
        Transform threat = null,
        float? seconds = null,
        NpcStunCause cause = NpcStunCause.Knockdown
    )
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_owner.CurrentState == NpcState.Stunned)
            return;

        float duration = seconds ?? m_owner.StunConfig.StunSeconds;

        if (HasStunOverlay)
        {
            if (duration <= m_stunDuration - m_stunElapsed)
                return;

            m_owner.Reaction.ThreatTarget = threat;
            m_stunDuration = duration;
            m_stunElapsed = 0f;
            m_standingUp = false;
            SetRising(false);

            return;
        }

        m_owner.Reaction.ThreatTarget = threat;
        m_stunDuration = duration;
        m_stunElapsed = 0f;
        m_standingUp = false;
        SetRising(false);
        SetStunned(true);

        if (cause == NpcStunCause.Taser)
            BroadcastTaserStun(m_stunDuration);

        NavMeshAgent agent = m_owner.Agent;
        m_agentStoppedBefore = m_owner.AgentReady && agent.isStopped;
        if (m_owner.AgentReady)
        {
            agent.isStopped = true;
            agent.ResetPath();
        }

        OnStunned?.Invoke(m_owner, threat);
    }

    /// <summary>스턴 중 매 프레임 진행한다. 코어의 스턴 게이트가 FSM Tick 대신 호출한다.</summary>
    internal void Tick()
    {
        m_stunElapsed += Time.deltaTime;

        if (!m_standingUp && m_stunElapsed >= m_stunDuration)
        {
            m_standingUp = true;

            if (!m_owner.Rope.IsRoped && !m_owner.Rope.IsTethered)
            {
                SetRising(true);
                m_owner.RaiseStandUp();
            }
        }

        if (m_stunElapsed >= m_stunDuration + m_owner.StunConfig.StandUpSeconds)
            ExitStun(resumeReaction: true);
    }

    /// <summary>체력 회복·상태 전이 없이 스턴 오버레이만 걷어낸다(넉백 겹침용).</summary>
    internal void ClearStunOverlay()
    {
        if (!HasStunOverlay)
            return;

        SetStunned(false);
        SetRising(false);
        m_stunElapsed = 0f;
        m_standingUp = false;
    }

    /// <summary>스턴을 해제하고 체력 1로 일으킨다.</summary>
    public void ExitStun(bool resumeReaction)
    {
        if (IsSpawned && !IsServer)
            return;
        if (!HasStunOverlay)
            return;

        SetStunned(false);
        SetRising(false);

        NavMeshAgent agent = m_owner.Agent;
        if (m_owner.AgentReady)
            agent.isStopped = m_agentStoppedBefore;

        m_owner.Health.ServerRestoreToOne();

        if (!resumeReaction)
            return;

        if (NpcStateRules.IsReactive(m_owner.CurrentState))
            m_owner.Reaction.ResumeReaction(m_owner.Reaction.ThreatTarget);
    }
}
