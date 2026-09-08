using UnityEngine;

/// <summary>
/// 래그돌 골반 밑 지면 탐색 — NPC·플레이어 래그돌이 같은 식으로 쓰던 것을 모았다.
/// 정착 자격 판정과 정착 정렬이 <b>같은 것</b>을 써야 한다(다르면 그 차이가 얼리는 순간 낙차로 남는다).
/// </summary>
public static class RagdollGround
{
    /// <summary>
    /// "몸이 바닥에 있다"로 보는 골반 높이(m) — 이 안이면 대리값(루트·캡슐)의 높이를 골반이 아니라
    /// <b>지면</b>이 준다. 골반 높이를 쓰는 것은 공중에 있는 동안만이다.
    ///
    /// 누운 시체의 골반은 약 0.15~0.25m이고 서 있거나 날아가는 몸은 그보다 훨씬 높다. 정확한 경계가
    /// 필요한 값이 아니라 <b>그 둘을 가르기만</b> 하면 되는 값이다.
    ///
    /// ⚠ NPC와 플레이어가 <b>같은 값이어야 한다</b> — 예전에는 양쪽에 상수가 한 벌씩 있고 주석이
    /// "같은 값이다"로 계약을 유지하고 있었다.
    /// </summary>
    public const float k_groundedHipsHeight = 0.5f;

    private const float k_probeLift = 0.5f; // 골반이 바닥에 파묻혀 있어도 레이가 지면 위에서 출발하게

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
