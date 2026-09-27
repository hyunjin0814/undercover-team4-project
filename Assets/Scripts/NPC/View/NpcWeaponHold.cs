using UnityEngine;

/// <summary>
/// 무기를 든 NPC(동네 깡패)의 상체 자세 레이어를 켜고, 신병·기절 상태가 되면 레이어를 내리고 무기를 숨긴다.
/// </summary>
public class NpcWeaponHold : MonoBehaviour
{
    [Tooltip("손에 든 무기 오브젝트 — 신병이 되면 숨긴다")]
    [SerializeField]
    private GameObject m_weapon;

    [Tooltip("무기 자세 레이어 이름 — 컨트롤러의 레이어와 같아야 한다 (NpcAnimatorControllerBuilder가 만든다)")]
    [SerializeField]
    private string m_layerName = "WeaponUpperBody";

    [Tooltip("무기를 든 동안 자세 레이어에 줄 가중치 — 낮추면 원래 팔 움직임이 섞인다")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_layerWeight = 1f;

    private NpcController m_controller;
    private Animator m_animator;
    private int m_layerIndex = -1;

    private void Awake()
    {
        m_controller = GetComponentInParent<NpcController>();
        m_animator = GetComponentInChildren<Animator>();

        if (m_animator != null)
            m_layerIndex = m_animator.GetLayerIndex(m_layerName);

        if (m_controller == null || m_animator == null || m_layerIndex < 0)
        {
            Debug.LogWarning(
                $"NpcWeaponHold: 컨트롤러·Animator·'{m_layerName}' 레이어 중 하나를 찾지 못해 무기 자세가 꺼진다",
                this
            );

            enabled = false;
        }
    }

    private void OnEnable()
    {
        m_controller.OnStateChanged += HandleStateChanged;
        m_controller.Stun.OnStunnedChanged += HandleStunnedChanged;
        Apply();
    }

    private void OnDisable()
    {
        m_controller.OnStateChanged -= HandleStateChanged;
        m_controller.Stun.OnStunnedChanged -= HandleStunnedChanged;
    }

    private void HandleStateChanged(NpcState state) => Apply();

    private void HandleStunnedChanged(bool stunned) => Apply();

    private void Apply()
    {
        bool stunned = m_controller.Stun.IsStunned;
        NpcState state = m_controller.CurrentState;

        bool holding = !stunned && KeepsWeapon(state);

        m_animator.SetLayerWeight(m_layerIndex, !stunned && PosesWeapon(state) ? m_layerWeight : 0f);

        if (m_weapon != null && m_weapon.activeSelf != holding)
            m_weapon.SetActive(holding);
    }

    private static bool KeepsWeapon(NpcState state) =>
        state != NpcState.Captured
        && state != NpcState.Escorted
        && state != NpcState.Jailed
        && state != NpcState.Stunned
        && state != NpcState.Dead;

    private static bool PosesWeapon(NpcState state) =>
        KeepsWeapon(state) && state != NpcState.Attack;
}
