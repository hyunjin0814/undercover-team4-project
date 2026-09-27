using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// NPC 넉백 도메인 부품 — 외력에 의한 포물선 비행을 처리하며, 비행 중에는 FSM과 NavMeshAgent를 멈춘다.
/// </summary>
public class NpcKnockback : NetworkBehaviour
{
    private NpcController m_owner;

    private Vector3 m_knockbackVelocity;
    private float m_knockbackElapsed;
    private bool m_knockbackActive;
    private NpcState m_knockbackLandingState;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
    }

    public bool IsKnockedBack => m_knockbackActive;

    /// <summary>초기 속도(m/s)로 NPC를 날려 보낸다. 수감·침입 중이거나 시체면 제외한다. 서버(또는 오프라인) 전용.</summary>
    public void ServerApplyKnockback(Vector3 velocity)
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_knockbackActive)
            return;
        if (velocity.sqrMagnitude < 0.01f)
            return;

        NpcState state = m_owner.StateMachine.CurrentState;
        if (state == NpcState.Dead || state == NpcState.Jailed || state == NpcState.Intruding)
            return;

        m_owner.Stun.ClearStunOverlay();

        m_knockbackLandingState =
            state == NpcState.Captured || state == NpcState.Escorted
                ? NpcState.Captured
                : NpcState.Stunned;

        if (state == NpcState.Escorted)
            m_owner.Custody.StopEscort();
        else
            m_owner.StateMachine.ChangeState(m_knockbackLandingState);

        m_knockbackActive = true;
        m_knockbackVelocity = velocity;
        m_knockbackElapsed = 0f;

        NavMeshAgent agent = m_owner.Agent;
        if (agent.enabled)
        {
            if (agent.isOnNavMesh)
                agent.ResetPath();
            agent.enabled = false;
        }
    }

    /// <summary>착지 처리 없이 비행을 중단한다(사망 전용).</summary>
    internal void ServerAbortFlight()
    {
        m_knockbackActive = false;
        m_knockbackVelocity = Vector3.zero;
    }

    /// <summary>스턴 오버레이를 켠 채 래그돌로 진입시키고 임펄스를 줘 날려 보낸다. 서버(또는 오프라인) 전용.</summary>
    public void ServerLaunchRagdoll(Vector3 impulse, float stunSeconds, Transform threat)
    {
        if (IsSpawned && !IsServer)
            return;
        if (impulse.sqrMagnitude < 0.01f)
            return;

        NpcState state = m_owner.StateMachine.CurrentState;
        if (state == NpcState.Dead || state == NpcState.Jailed || state == NpcState.Intruding)
            return;

        if (m_knockbackActive)
            ServerAbortFlight();

        if (m_owner.Stun.HasStunOverlay)
            m_owner.Stun.ClearStunOverlay();
        m_owner.Stun.EnterStunned(threat, stunSeconds);

        m_owner.Ragdoll?.WakeCorpse();
        m_owner.Ragdoll?.EnterRagdoll(impulse);
    }

    internal void Tick()
    {
        NpcCommonConfig config = m_owner.CommonConfig;

        m_knockbackElapsed += Time.deltaTime;
        m_knockbackVelocity.y += config.KnockbackGravity * Time.deltaTime;

        Vector3 next = transform.position + m_knockbackVelocity * Time.deltaTime;

        Vector3 horizontalStep = new Vector3(
            next.x - transform.position.x,
            0f,
            next.z - transform.position.z
        );
        float stepDistance = horizontalStep.magnitude;
        if (
            stepDistance > 0.0001f
            && m_owner.SweepHitsObstacle(horizontalStep / stepDistance, stepDistance, out _)
        )
        {
            next.x = transform.position.x;
            next.z = transform.position.z;
            m_knockbackVelocity.x = 0f;
            m_knockbackVelocity.z = 0f;
        }

        transform.position = next;

        bool timedOut = m_knockbackElapsed >= config.KnockbackMaxFlightSeconds;
        if (!timedOut && m_knockbackVelocity.y > 0f)
            return;

        if (
            NavMesh.SamplePosition(
                transform.position,
                out NavMeshHit ground,
                config.KnockbackLandSampleDistance,
                m_owner.BaseAreaMask
            )
        )
        {
            if (!timedOut && transform.position.y > ground.position.y + 0.05f)
                return;

            EndKnockback(ground.position);
            return;
        }

        if (timedOut)
            EndKnockback(transform.position);
    }

    private const float k_landSnapMaxHorizontal = 1.5f;

    private void EndKnockback(Vector3 landing)
    {
        m_knockbackActive = false;
        m_knockbackVelocity = Vector3.zero;

        Vector3 offset = landing - transform.position;
        offset.y = 0f;
        if (offset.sqrMagnitude > k_landSnapMaxHorizontal * k_landSnapMaxHorizontal)
            landing = transform.position;

        NavMeshAgent agent = m_owner.Agent;
        agent.enabled = true;
        agent.Warp(landing);

        if (!agent.isOnNavMesh)
        {
            Debug.LogWarning(
                "NpcKnockback: 넉백 착지 지점을 NavMesh에 붙이지 못했다 — 행방불명 처리 대기: "
                    + $"{name} @{transform.position.ToString("F1")}",
                this
            );
            return;
        }

        m_owner.StateMachine.ChangeState(m_knockbackLandingState);
    }
}
