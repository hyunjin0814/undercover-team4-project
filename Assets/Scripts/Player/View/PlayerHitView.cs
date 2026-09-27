using Unity.Netcode;
using UnityEngine;

/// <summary>
/// PlayerHealth.OnDamaged를 구독해 오너 화면에 피격 연출(비네트·방향 표시 등)을 재생한다.
/// </summary>
[RequireComponent(typeof(PlayerHealth))]
public class PlayerHitView : NetworkBehaviour
{
    private PlayerHealth m_health;
    private PlayerIncapacitation m_incapacitation;
    private PlayerInteractor m_interactor;
    private PlayerHandView m_handView;
    private PlayerLook m_look;
    private ShockArcEmitter m_shockArcs;

    private bool IsLocalOwner => !IsSpawned || m_isLocalPlayer;

    private bool m_isLocalPlayer;

    public override void OnNetworkSpawn() => m_isLocalPlayer = IsOwner;

    private void Awake()
    {
        m_health = GetComponent<PlayerHealth>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_interactor = GetComponent<PlayerInteractor>();
        m_handView = GetComponent<PlayerHandView>();
        m_look = GetComponent<PlayerLook>();
        m_shockArcs = GetComponent<ShockArcEmitter>();

        m_health.OnDamaged += HandleDamaged;
    }

    public override void OnDestroy()
    {
        if (m_health != null)
            m_health.OnDamaged -= HandleDamaged;

        base.OnDestroy();
    }

    public override void OnNetworkDespawn()
    {
        if (IsLocalOwner)
        {
            App.UI.DamageVignette?.ClearAll();
            App.UI.SpeedVignette?.ClearAll();
            ApplyShock(0f);
        }

        base.OnNetworkDespawn();
    }

    private void HandleDamaged(DamageHit hit)
    {
        if (!IsLocalOwner)
            return;

        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return;

        DamageVignetteUI vignette = App.UI.DamageVignette;
        if (vignette != null)
        {
            if (TryResolveDirection(hit, out float angle))
                vignette.PlayHit(hit.Amount, angle);
            else
                vignette.PlayHit(hit.Amount);
        }

        m_handView?.PlayHitShake();
    }

    /// <summary>가해자 월드 좌표를 화면 정면 기준 각도로 환산한다(오른쪽이 양수).</summary>
    private bool TryResolveDirection(DamageHit hit, out float angleDegrees)
    {
        angleDegrees = 0f;
        if (!hit.HasAttacker)
            return false;

        Transform aim = m_interactor != null ? m_interactor.AimOrigin : transform;
        if (aim == null)
            return false;

        Vector3 forward = aim.forward;
        forward.y = 0f;

        Vector3 toAttacker = hit.AttackerPosition - transform.position;
        toAttacker.y = 0f;

        if (forward.sqrMagnitude < 0.0001f || toAttacker.sqrMagnitude < 0.0001f)
            return false;

        angleDegrees = Vector3.SignedAngle(forward, toAttacker, Vector3.up);
        return true;
    }

    private void Update()
    {
        TickShock();

        if (!IsLocalOwner)
            return;

        int maxHp = Mathf.Max(1, m_health.MaxHp);
        App.UI.DamageVignette?.UpdateHealthState(
            (float)m_health.CurrentHp / maxHp,
            m_incapacitation != null && m_incapacitation.IsIncapacitated
        );
    }

    /// <summary>테이저 기절 연출을 매 프레임 갱신한다 — 원인이 <see cref="IncapacitationCause.Stun"/>일 때만 켜진다.</summary>
    private void TickShock()
    {
        if (m_incapacitation == null || !m_incapacitation.IsStunned)
        {
            ApplyShock(0f);
            return;
        }

        float remaining = m_incapacitation.RemainingStunSeconds;
        ApplyShock(Mathf.Clamp01(remaining / ShockArcEmitter.k_calmTailSeconds));
    }

    private void ApplyShock(float intensity)
    {
        if (m_shockArcs != null)
        {
            m_shockArcs.SetEmitting(intensity > 0.001f);
            m_shockArcs.SetCalm(intensity < 0.999f);
        }

        if (!IsLocalOwner)
            return;

        App.UI.TaserShock?.SetShock(intensity);
        m_look?.SetShakeIntensity(intensity);
        m_handView?.SetConvulsion(intensity);
    }
}
