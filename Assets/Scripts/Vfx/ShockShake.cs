using UnityEngine;

/// <summary>
/// 감전 경련 흔들림 파형(사인) — 카메라와 1인칭 팔이 같은 주파수를 공유한다.
/// </summary>
public static class ShockShake
{
    public const float k_frequency = 33f;

    /// <summary>지금 시각의 흔들림 포즈를 낸다 — 상태가 없는 순수 함수다.</summary>
    public static void Evaluate(
        float intensity,
        float degrees,
        float offset,
        out Vector3 euler,
        out Vector3 position
    )
    {
        if (intensity <= 0.001f)
        {
            euler = Vector3.zero;
            position = Vector3.zero;
            return;
        }

        float t = Time.time * k_frequency;

        euler = new Vector3(
            Mathf.Sin(t) * degrees * intensity,
            Mathf.Sin(t * 1.37f + 1.1f) * degrees * intensity,
            Mathf.Sin(t * 0.83f + 2.3f) * degrees * intensity
        );
        position = new Vector3(
            Mathf.Sin(t * 1.11f + 0.7f) * offset * intensity,
            Mathf.Sin(t * 1.53f) * offset * intensity,
            0f
        );
    }
}
