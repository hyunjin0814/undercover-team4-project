using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 한 지점에 여러 NPC가 모여 설 자리를 황금각 배치로 순번대로 나눠 준다(원한 구역용).
/// </summary>
public static class GatherSlot
{
    private const float k_goldenAngle = 2.39996323f;

    /// <summary>slot번째 자리의 기준점 대비 수평 오프셋을 돌려준다.</summary>
    public static Vector3 Offset(int slot, float spacing)
    {
        if (slot <= 0)
            return Vector3.zero;

        float radius = spacing * Mathf.Sqrt(slot);
        float angle = slot * k_goldenAngle;
        return new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
    }

    /// <summary>anchor에 offset을 얹은 자리를 areaMask 내 NavMesh로 스냅한다. 실패하면 anchor를 돌려준다.</summary>
    public static Vector3 Resolve(Vector3 anchor, Vector3 offset, int areaMask, float snapRadius)
    {
        if (offset == Vector3.zero)
            return anchor;

        return NavMesh.SamplePosition(anchor + offset, out NavMeshHit hit, snapRadius, areaMask)
            ? hit.position
            : anchor;
    }
}
