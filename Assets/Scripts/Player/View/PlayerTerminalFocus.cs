using UnityEngine;

/// <summary>
/// 본부 단말 앞으로 카메라를 옮기는 로컬 포커스 시점. 매 틱 포커스 성립을 되물어 자동으로 풀린다.
/// </summary>
[RequireComponent(typeof(PlayerLook))]
public class PlayerTerminalFocus : MonoBehaviour
{
    [Tooltip("1인칭↔단말 화면 전환 보간 속도. 클수록 빨리 붙는다 — PlayerSpectateCamera와 같은 기준")]
    [SerializeField]
    private float m_blendSpeed = 7f;

    private BlackoutRecoveryTerminal m_terminal;
    private float m_blend;
    private bool m_locked;

    private PlayerLook m_look;
    private PlayerMovement m_movement;
    private PlayerIncapacitation m_incapacitation;

    public bool IsFocusing => m_terminal != null;

    public BlackoutRecoveryTerminal Terminal => m_terminal;

    private void Awake()
    {
        m_look = GetComponent<PlayerLook>();
        m_movement = GetComponent<PlayerMovement>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
    }

    private void OnDisable() => Release();

    /// <summary>단말 화면 앞으로 간다 — 단말의 E 상호작용이 부른다.</summary>
    public void Begin(BlackoutRecoveryTerminal terminal)
    {
        if (terminal == null || terminal.FocusPoint == null)
            return;

        if (m_terminal == terminal)
        {
            Release();
            return;
        }

        Release();

        m_terminal = terminal;
        terminal.SetLocalFocused(true);
        ApplyLocks(true);
    }

    /// <summary>1인칭으로 돌아간다 — 화면의 나가기, 복구 완료, 상태 이탈이 모두 여기로 모인다.</summary>
    public void Release()
    {
        if (m_terminal == null && !m_locked)
            return;

        if (m_terminal != null)
            m_terminal.SetLocalFocused(false);

        m_terminal = null;
        ApplyLocks(false);
    }

    /// <summary>포커스 보간을 진행하고 현재 진행도를 돌려준다.</summary>
    public float Tick()
    {
        if (m_terminal != null && !CanKeepFocus())
            Release();

        float target = m_terminal != null ? 1f : 0f;
        m_blend = Mathf.Lerp(m_blend, target, 1f - Mathf.Exp(-m_blendSpeed * Time.deltaTime));

        if (m_terminal == null && m_blend < 0.001f)
            m_blend = 0f;

        return m_blend;
    }

    /// <summary>카메라가 있어야 할 월드 포즈 — 보간이 0이면 false.</summary>
    public bool TryGetPose(out Vector3 position, out Quaternion rotation)
    {
        Transform point = m_terminal != null ? m_terminal.FocusPoint : null;
        if (point == null)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        position = point.position;
        rotation = point.rotation;
        return true;
    }

    private bool CanKeepFocus()
    {
        if (m_terminal == null || !m_terminal.IsOnline || m_terminal.FocusPoint == null)
            return false;

        return m_incapacitation == null || !m_incapacitation.IsIncapacitated;
    }

    /// <summary>시점·이동 잠금을 함께 넣고 뺀다(커서는 풀지 않는다).</summary>
    private void ApplyLocks(bool active)
    {
        if (active == m_locked)
            return;

        m_locked = active;

        if (m_look != null)
        {
            if (active)
                m_look.PushLookSuspend();
            else
                m_look.PopLookSuspend();
        }

        if (m_movement != null)
            m_movement.SetViewLocked(active);
    }
}
