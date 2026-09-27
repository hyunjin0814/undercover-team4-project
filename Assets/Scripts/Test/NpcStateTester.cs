using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// [테스트 전용] Play 중 인스펙터에서 NPC 상태를 강제로 바꿔 애니메이션을 확인한다.
/// </summary>
[RequireComponent(typeof(NpcController))]
public class NpcStateTester : MonoBehaviour
{
    private static readonly int s_stateHash = Animator.StringToHash("State");

    [Header("수동 테스트 (Play 모드 전용)")]
    [Tooltip("켜면 FSM이 멈추고 아래에서 고른 상태의 모션이 강제 재생된다")]
    [SerializeField] private bool m_manualMode;
    [SerializeField] private NpcState m_previewState = NpcState.Idle;

    private NpcController m_controller;
    private NpcAnimationDriver m_driver;
    private NavMeshAgent m_agent;
    private Animator m_animator;
    private bool m_lastManualMode;

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();
        m_driver = GetComponent<NpcAnimationDriver>();
        m_agent = GetComponent<NavMeshAgent>();
        m_animator = GetComponentInChildren<Animator>();
    }

    private void OnValidate()
    {
        if (Application.isPlaying && m_animator != null)
            Apply();
    }

    private void Apply()
    {
        if (m_controller.IsSpawned && !m_controller.IsServer)
            return;

        if (m_manualMode != m_lastManualMode)
        {
            m_controller.enabled = !m_manualMode;
            if (m_driver != null) m_driver.enabled = !m_manualMode;

            if (m_manualMode)
            {
                if (m_agent.isOnNavMesh) m_agent.ResetPath();
            }
            else
            {
                m_animator.SetInteger(s_stateHash, (int)m_controller.StateMachine.CurrentState);
            }

            m_lastManualMode = m_manualMode;
        }

        if (m_manualMode)
            m_animator.SetInteger(s_stateHash, (int)m_previewState);
    }
}
