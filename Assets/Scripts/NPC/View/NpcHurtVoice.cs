using UnityEngine;

/// <summary>
/// 인간 NPC가 피해를 받을 때 3D 신음을 한 번 재생한다.
/// 안드로이드와 기절 중인 NPC는 제외한다.
/// </summary>
[RequireComponent(typeof(NpcController))]
public class NpcHurtVoice : MonoBehaviour
{
    [Tooltip("신음 사이 최소 간격(초) — 진압봉 연타·폭발 다중 히트에서 소리가 겹쳐 우스워지는 것을 막는다")]
    [Min(0f)]
    [SerializeField] private float m_minInterval = 0.5f;

    private NpcController m_controller;
    private CitizenIdentity m_identity;
    private float m_nextVoiceTime;

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();
        m_identity = GetComponent<CitizenIdentity>();
    }

    private void OnEnable() => m_controller.Health.OnHit += HandleHit;

    private void OnDisable() => m_controller.Health.OnHit -= HandleHit;

    private void HandleHit(DamageHit hit)
    {
        if (m_controller.Stun.IsStunned) return;
        if (m_identity != null && m_identity.IsAndroidBody) return;
        if (Time.time < m_nextVoiceTime) return;

        m_nextVoiceTime = Time.time + m_minInterval;
        App.Sound?.PlaySfxAt(EAudioClip.NpcHurtHuman, transform.position);
    }
}
