using UnityEngine;

/// <summary>
/// 감정표현 휠 기하 계산 — 마우스 방향을 8칸 중 하나로 환산한다(12시가 0번, 시계방향).
/// </summary>
public static class EmoteWheelGeometry
{
    public const int k_slotCount = 8;

    public const float k_deadZone = 0.35f;

    private const float k_degreesPerSlot = 360f / k_slotCount;

    /// <summary>정규화된 방향 벡터를 슬롯 인덱스로 환산한다. 데드존 안이면 -1.</summary>
    public static int SlotFromDirection(Vector2 direction, float deadZone = k_deadZone)
    {
        if (direction.magnitude < deadZone)
            return -1;

        float degrees = Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg;

        float shifted = Mathf.Repeat(degrees + k_degreesPerSlot * 0.5f, 360f);
        return Mathf.FloorToInt(shifted / k_degreesPerSlot) % k_slotCount;
    }

    /// <summary>슬롯의 중심 각도(도) — 12시가 0, 시계방향. 휠 UI의 아이콘 배치가 쓴다.</summary>
    public static float SlotCenterDegrees(int slot) => slot * k_degreesPerSlot;
}
