using System;
using UnityEngine;

/// <summary>
/// 폭발 반경·피해·래그돌 임펄스의 감쇠식과 튜닝 값을 담은 순수 값 객체.
/// </summary>
[Serializable]
public class BombBlastProfile
{
    [Tooltip("이 반경(m) 안의 플레이어·NPC가 피해·넉백을 받는다")]
    [SerializeField]
    private float m_explosionRadius = 8f;

    [Tooltip("폭심에서의 피해량 — 반경 끝까지 m_damageEdgeFalloff 비율로 선형 감쇠한다")]
    [SerializeField]
    private int m_explosionDamage = 150;

    [Tooltip("반경 끝에서 남는 피해 비율(폭심 1.0에서 선형 감쇠). 기본값이면 즉사 반경 약 3.3m")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_damageEdgeFalloff = 0.2f;

    [Tooltip("폭심에서 밀려나는 기준 세기(m/s) — 아래 래그돌 배율이 이 값에 곱해진다")]
    [SerializeField]
    private float m_knockbackForce = 12f;

    [Tooltip("수평 세기 대비 위로 띄우는 비율 — 0이면 순수 수평으로만 밀린다")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_knockbackUpwardRatio = 0.45f;

    [Tooltip("반경 끝에서 남는 세기 비율 — 폭심(1.0)에서 반경 끝까지 선형 감쇠한다")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_knockbackEdgeFalloff = 0.25f;

    [Tooltip("래그돌 수평 세기 = 넉백 수평 세기 × 이 값. 연출용이며 사망자·생존자가 같은 값을 쓴다")]
    [Range(0f, 2f)]
    [SerializeField]
    private float m_ragdollImpulseScale = 0.3f;

    [Tooltip("래그돌 상승 세기 = 수평 세기 × 이 값(발사각). 1.0이 45°로 사거리 최대")]
    [Range(0f, 3f)]
    [SerializeField]
    private float m_ragdollLiftRatio = 1.2f;

    public float Radius => m_explosionRadius;

    public int PeakDamage => m_explosionDamage;

    public float DamageEdgeFalloff => m_damageEdgeFalloff;

    /// <summary>폭심→대상 delta의 넉백 속도(m/s)를 계산한다. 반경 밖이면 zero.</summary>
    public Vector3 EvaluateKnockback(Vector3 delta, Vector3 fallbackDirection)
    {
        if (m_explosionRadius <= 0f || m_knockbackForce <= 0f)
            return Vector3.zero;

        delta.y = 0f;
        float distance = delta.magnitude;
        if (distance > m_explosionRadius)
            return Vector3.zero;

        Vector3 direction = distance > 0.01f ? delta / distance : fallbackDirection;
        float scaled = m_knockbackForce * Mathf.Lerp(1f, m_knockbackEdgeFalloff, distance / m_explosionRadius);

        return direction * scaled + Vector3.up * (scaled * m_knockbackUpwardRatio);
    }

    /// <summary>폭심→대상 delta의 피해량을 3차원 거리 감쇠로 계산한다. 반경 밖이면 0.</summary>
    public int EvaluateDamage(Vector3 delta)
    {
        if (m_explosionRadius <= 0f || m_explosionDamage <= 0)
            return 0;

        float distance = delta.magnitude;
        if (distance > m_explosionRadius)
            return 0;

        float scaled = m_explosionDamage
            * Mathf.Lerp(1f, m_damageEdgeFalloff, distance / m_explosionRadius);
        return Mathf.RoundToInt(scaled);
    }

    /// <summary>넉백의 수평 방향을 바탕으로 래그돌용 임펄스(세기·들어올림)를 계산한다.</summary>
    public Vector3 EvaluateRagdollImpulse(Vector3 delta, Vector3 fallbackDirection)
    {
        Vector3 knockback = EvaluateKnockback(delta, fallbackDirection);
        Vector3 horizontal = new Vector3(knockback.x, 0f, knockback.z) * m_ragdollImpulseScale;
        if (horizontal == Vector3.zero)
            return Vector3.zero;

        return horizontal + Vector3.up * (horizontal.magnitude * m_ragdollLiftRatio);
    }
}
