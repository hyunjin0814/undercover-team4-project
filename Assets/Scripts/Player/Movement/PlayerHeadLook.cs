using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 오너의 시선 pitch를 NetworkVariable로 전파하고, 모든 인스턴스가 LateUpdate에서 머리 본에 반영한다.
/// </summary>
public class PlayerHeadLook : NetworkBehaviour
{
    [Header("본 참조")]
    [SerializeField] private Transform m_neckBone;
    [SerializeField] private Transform m_headBone;

    [Header("튜닝")]
    [Tooltip("목이 담당하는 pitch 비율 — 나머지는 머리가 담당 (나눠 얹어야 목이 자연스럽다)")]
    [Range(0f, 1f)]
    [SerializeField] private float m_neckWeight = 0.4f;

    [Tooltip("본에 반영할 pitch 한계(도) — 카메라(±80°)를 그대로 주면 목이 부러져 보인다")]
    [SerializeField] private float m_maxVisualPitch = 60f;

    [Tooltip("pitch 추종 감쇠율(1/초) — 원격 인스턴스의 틱 단위 스텝을 부드럽게 만든다")]
    [SerializeField] private float m_pitchLerpSpeed = 15f;

    [Tooltip("다운 등으로 오버라이드를 켜고 끌 때 가중치 블렌드 감쇠율(1/초)")]
    [SerializeField] private float m_weightLerpSpeed = 8f;

    private const float k_sendThreshold = 0.1f;

    private readonly NetworkVariable<float> m_syncedPitch = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner
    );

    private PlayerLook m_look;
    private PlayerIncapacitation m_incapacitation;
    private PlayerRagdoll m_ragdoll;
    private float m_displayPitch;
    private float m_weight;

    public float LastAppliedTilt { get; private set; }

    private void Awake()
    {
        m_look = GetComponent<PlayerLook>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_ragdoll = GetComponent<PlayerRagdoll>();
    }

    private void Update()
    {
        if (!IsSpawned || !IsOwner || m_look == null)
            return;

        if (Mathf.Abs(m_look.Pitch - m_syncedPitch.Value) > k_sendThreshold)
            m_syncedPitch.Value = m_look.Pitch;
    }

    private void LateUpdate()
    {
        if (m_neckBone == null || m_headBone == null)
            return;

        if (m_ragdoll != null && m_ragdoll.IsRagdollActive)
        {
            m_weight = 0f;
            return;
        }

        bool useLocal = (!IsSpawned || IsOwner) && m_look != null;
        float target = useLocal ? m_look.Pitch : m_syncedPitch.Value;
        target = Mathf.Clamp(target, -m_maxVisualPitch, m_maxVisualPitch);

        bool active = m_incapacitation == null || !m_incapacitation.IsIncapacitated;

        float pitchT = 1f - Mathf.Exp(-m_pitchLerpSpeed * Time.deltaTime);
        float weightT = 1f - Mathf.Exp(-m_weightLerpSpeed * Time.deltaTime);
        m_displayPitch = Mathf.Lerp(m_displayPitch, target, pitchT);
        m_weight = Mathf.Lerp(m_weight, active ? 1f : 0f, weightT);

        float applied = m_displayPitch * m_weight;
        LastAppliedTilt = applied;
        if (Mathf.Abs(applied) < 0.01f)
            return;

        Quaternion neckTilt = Quaternion.AngleAxis(applied * m_neckWeight, transform.right);
        Quaternion headTilt = Quaternion.AngleAxis(applied * (1f - m_neckWeight), transform.right);
        m_neckBone.rotation = neckTilt * m_neckBone.rotation;
        m_headBone.rotation = headTilt * m_headBone.rotation;
    }
}
