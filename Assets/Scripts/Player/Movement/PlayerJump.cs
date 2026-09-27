using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 점프 입력 수집과 공중 상태(IsAirborne) 서버 권위 전파를 담당한다.
/// 실제 수직 임펄스는 PlayerMovement가 준다.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerJump : NetworkBehaviour
{
    [Header("점프")]
    [Tooltip(
        "제자리 점프로 올라가는 최고 높이(m). 실제 초기 속도는 중력에서 역산한다.\n"
            + "맵이 점프를 전제로 설계되지 않아(GDD 미정의) 보수적으로 잡았다 — 올라타면 안 되는 "
            + "구조물이 발견되면 여기부터 낮출 것."
    )]
    [SerializeField]
    private float m_jumpHeight = 0.8f;

    private readonly NetworkVariable<bool> m_isAirborneSynced = new NetworkVariable<bool>();
    private bool m_isAirborne;
    private bool m_reportedAirborne;
    private bool m_jumpRequested;

    private PlayerInputHandler m_inputHandler;

    public float JumpHeight => m_jumpHeight;

    public bool IsAirborne =>
        IsSpawned && !IsServer && !IsOwner ? m_isAirborneSynced.Value : m_isAirborne;

    private void Awake()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner && m_inputHandler != null)
        {
            m_inputHandler.OnJumpPressed += HandleJumpInput;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner && m_inputHandler != null)
        {
            m_inputHandler.OnJumpPressed -= HandleJumpInput;
        }

        m_jumpRequested = false;
    }

    private void HandleJumpInput() => m_jumpRequested = true;

    /// <summary>점프 요청이 있었으면 true를 돌려주고 요청을 비운다.</summary>
    public bool ConsumeJumpRequest()
    {
        if (!m_jumpRequested)
            return false;

        m_jumpRequested = false;
        return true;
    }

    /// <summary>오너가 접지 상태를 알린다. 값이 바뀔 때만 서버로 보낸다.</summary>
    public void ReportGrounded(bool grounded)
    {
        bool airborne = !grounded;

        if (!IsSpawned)
        {
            m_isAirborne = airborne;
            return;
        }

        if (!IsOwner)
            return;

        m_isAirborne = airborne;

        if (m_reportedAirborne == airborne)
            return;

        m_reportedAirborne = airborne;
        ReportAirborneServerRpc(airborne);
    }

    [ServerRpc]
    private void ReportAirborneServerRpc(bool airborne)
    {
        m_isAirborne = airborne;
        m_isAirborneSynced.Value = airborne;
    }
}
