using UnityEngine;

/// <summary>
/// 테이저 기절 동안 NPC 몸을 시안으로 물들이고 전기 아크를 튀기는 표현의 켜고 끔을 담당한다.
/// 아크는 ShockArcEmitter, 발광은 BodyTint가 처리한다.
/// </summary>
[RequireComponent(typeof(NpcController))]
[RequireComponent(typeof(ShockArcEmitter))]
[RequireComponent(typeof(BodyTint))]
public class NpcShockView : MonoBehaviour
{
    [Header("명중 순간 발광")]
    [Tooltip("맞은 순간 몸이 물드는 색 — 셰이더에 _BaseColor가 있을 때만 적용된다")]
    [SerializeField]
    private Color m_flashColor = new Color(0.3f, 0.95f, 1f, 1f);

    [SerializeField]
    private float m_flashSeconds = 0.08f;

    private NpcController m_controller;
    private ShockArcEmitter m_emitter;
    private BodyTint m_tint;

    private bool m_active;
    private float m_calmFromTime;
    private float m_flashUntil;

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();
        m_emitter = GetComponent<ShockArcEmitter>();
        m_tint = GetComponent<BodyTint>();
    }

    private void OnEnable()
    {
        m_controller.Stun.OnTaserStunStarted += HandleTaserStunStarted;
        m_controller.Stun.OnStunnedChanged += HandleStunnedChanged;
    }

    private void OnDisable()
    {
        m_controller.Stun.OnTaserStunStarted -= HandleTaserStunStarted;
        m_controller.Stun.OnStunnedChanged -= HandleStunnedChanged;
        Stop();
    }

    private void HandleTaserStunStarted(float seconds)
    {
        m_active = true;
        m_flashUntil = Time.time + m_flashSeconds;

        m_calmFromTime = Time.time + Mathf.Max(0f, seconds - ShockArcEmitter.k_calmTailSeconds);

        m_emitter.SetCalm(false);
        m_emitter.SetEmitting(true);
    }

    private void HandleStunnedChanged(bool stunned)
    {
        if (!stunned)
            Stop();
    }

    private void Stop()
    {
        if (!m_active)
            return;

        m_active = false;
        m_flashUntil = 0f;
        m_emitter.SetEmitting(false);
        m_tint.ClearFlash();
    }

    private void Update()
    {
        if (!m_active)
            return;

        if (!m_controller.Stun.IsStunned)
        {
            Stop();
            return;
        }

        if (Time.time >= m_calmFromTime)
            m_emitter.SetCalm(true);

        TickFlash();
    }

    private void TickFlash()
    {
        if (m_flashUntil <= 0f)
            return;

        if (Time.time >= m_flashUntil)
        {
            m_flashUntil = 0f;
            m_tint.ClearFlash();
            return;
        }

        float t = (m_flashUntil - Time.time) / Mathf.Max(0.0001f, m_flashSeconds);
        m_tint.SetFlash(Color.Lerp(Color.white, m_flashColor, t));
    }
}
