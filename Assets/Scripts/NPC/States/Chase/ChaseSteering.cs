using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 추격 중 NavMeshAgent 조향 값 덮어쓰기와 리드 조준을 담당한다. 진입 시 원래 값을 기억했다 되돌린다.
/// </summary>
public class ChaseSteering
{
    private readonly NpcChaseConfig m_config;

    private float m_baseTurnSpeed;
    private float m_baseAcceleration;
    private bool m_baseAutoBraking;

    private Transform m_leadTarget;
    private Vector3 m_lastTargetPosition;
    private float m_lastTargetSampleTime;

    public ChaseSteering(NpcChaseConfig config)
    {
        m_config = config;
    }

    /// <summary>진입 시점의 조향 값을 기억한다 — <see cref="Apply"/>로 되돌릴 기준. 상태의 Enter에서 한 번.</summary>
    public void CaptureBaseline(NavMeshAgent agent)
    {
        m_baseTurnSpeed = agent.angularSpeed;
        m_baseAcceleration = agent.acceleration;
        m_baseAutoBraking = agent.autoBraking;
    }

    /// <summary>조향 값을 추격용/평상시로 전환한다(추격 중에는 오토브레이킹을 끈다).</summary>
    public void Apply(NavMeshAgent agent, bool chasing)
    {
        agent.angularSpeed = chasing ? m_config.TurnSpeed : m_baseTurnSpeed;
        agent.acceleration = chasing ? m_config.Acceleration : m_baseAcceleration;
        agent.autoBraking = !chasing && m_baseAutoBraking;
    }

    /// <summary>표적 속도로 조금 뒤의 위치를 예측해 돌려준다(상한 있음).</summary>
    public Vector3 PredictAimPoint(Transform target, float distance, float agentSpeed, float now)
    {
        Vector3 current = target.position;
        float span = now - m_lastTargetSampleTime;

        if (m_leadTarget != target || span <= 0f)
        {
            RememberSample(target, current, now);
            return current;
        }

        Vector3 velocity = (current - m_lastTargetPosition) / span;
        velocity.y = 0f;
        RememberSample(target, current, now);

        float speed = Mathf.Max(agentSpeed, 0.1f);
        float lead = Mathf.Min(distance / speed, m_config.MaxLeadSeconds);
        return current + velocity * lead;
    }

    /// <summary>몸 방향을 실제 이동 속도 쪽으로 돌린다 — 재탐색 우회 중 표적 쪽을 보는 문제 수정.</summary>
    public void TickFacing(NavMeshAgent agent, Transform transform)
    {
        Vector3 velocity = agent.velocity;
        velocity.y = 0f;

        if (velocity.sqrMagnitude < 0.01f)
            return;

        Quaternion target = Quaternion.LookRotation(velocity);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation, target, agent.angularSpeed * Time.deltaTime);
    }

    /// <summary>리드 표본을 버린다 — 표적이 바뀌거나 상태에 새로 진입할 때.</summary>
    public void ClearLeadSample()
    {
        m_leadTarget = null;
        m_lastTargetSampleTime = 0f;
    }

    private void RememberSample(Transform target, Vector3 position, float now)
    {
        m_leadTarget = target;
        m_lastTargetPosition = position;
        m_lastTargetSampleTime = now;
    }
}
