using UnityEngine;

/// <summary>
/// 날씨 실내 판정 — 위로 레이를 쏴 그 자리의 하늘이 막혀 있는지 답하는 공용 규칙.
/// 낙뢰·빙판·강수 마스크가 모두 이 함수를 쓴다.
/// </summary>
public static class WeatherShelter
{
    public const float k_bodyProbeHeight = 1f;

    /// <summary>이 지점 위가 막혀 있는가 — 막혔으면 실내(또는 처마 밑)로 본다.</summary>
    public static bool IsSheltered(Vector3 position, LayerMask blockMask, float probeHeight, float probeRadius = 0f)
    {
        if (probeHeight <= 0f)
            return false;

        if (probeRadius > 0f)
            return Physics.SphereCast(
                position, probeRadius, Vector3.up, out RaycastHit _,
                probeHeight, blockMask, QueryTriggerInteraction.Ignore);

        return Physics.Raycast(
            position,
            Vector3.up,
            probeHeight,
            blockMask,
            QueryTriggerInteraction.Ignore
        );
    }
}
