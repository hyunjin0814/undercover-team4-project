using UnityEngine;

/// <summary>
/// 래그돌 골반 밑 지면 탐색 헬퍼 — NPC·플레이어 래그돌의 정착 판정과 정렬이 함께 쓴다.
/// </summary>
public static class RagdollGround
{
    public const float k_groundedHipsHeight = 0.5f;

    private const float k_probeLift = 0.5f;

    public static bool TryGroundUnder(
        Vector3 hipsPosition, float probeDistance, LayerMask mask, out Vector3 point)
    {
        bool hitGround = Physics.Raycast(
            hipsPosition + Vector3.up * k_probeLift,
            Vector3.down,
            out RaycastHit hit,
            k_probeLift + probeDistance,
            mask,
            QueryTriggerInteraction.Ignore
        );

        point = hitGround ? hit.point : hipsPosition;
        return hitGround;
    }
}
