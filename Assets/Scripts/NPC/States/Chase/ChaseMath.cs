using UnityEngine;

/// <summary>
/// 추격 계산에 쓰는 거리 셈 — 부품 셋과 상태가 같은 자를 쓰게 모아 둔다.
/// </summary>
public static class ChaseMath
{
    /// <summary>Y를 뺀 수평(XZ) 거리를 돌려준다.</summary>
    public static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
