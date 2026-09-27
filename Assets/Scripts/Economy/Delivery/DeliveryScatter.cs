using UnityEngine;

/// <summary>
/// 배달 아이템을 지점 둘레에 흩뿌리고 바닥에 스냅하는 위치 계산 헬퍼.
/// </summary>
public static class DeliveryScatter
{
    private const float k_groundProbeHeight = 2f;
    private const float k_groundClearance = 0.02f;

    public static Vector3 Resolve(
        Vector3 anchor,
        int index,
        float spreadRadius,
        LayerMask groundMask
    )
    {
        Vector3 position = anchor;
        if (index > 0)
        {
            float angle = (index % 6) * 60f * Mathf.Deg2Rad;
            float radius = spreadRadius * (1f + index / 6);
            position += new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
        }

        return SnapToGround(position, groundMask);
    }

    public static Vector3 SnapToGround(Vector3 candidate, LayerMask groundMask)
    {
        Vector3 probeOrigin = candidate + Vector3.up * k_groundProbeHeight;
        RaycastHit[] hits = Physics.RaycastAll(
            probeOrigin,
            Vector3.down,
            k_groundProbeHeight * 2f,
            groundMask,
            QueryTriggerInteraction.Ignore
        );

        RaycastHit? closestGround = null;
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.GetComponentInParent<CharacterController>() != null)
                continue;

            if (closestGround == null || hit.distance < closestGround.Value.distance)
                closestGround = hit;
        }

        return closestGround != null
            ? closestGround.Value.point + Vector3.up * k_groundClearance
            : candidate;
    }
}
