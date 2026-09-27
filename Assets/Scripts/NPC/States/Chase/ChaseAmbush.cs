using UnityEngine;

/// <summary>
/// 납치 기습 계산 — 표적의 뒤로 돌아가는 접근 지점과 후방 포획 각도를 판정한다.
/// </summary>
public class ChaseAmbush
{
    private readonly NpcChaseConfig m_config;

    public ChaseAmbush(NpcChaseConfig config)
    {
        m_config = config;
    }

    /// <summary>from이 표적 몸통 기준 후방 부채꼴 안인지 판정한다(XZ 평면).</summary>
    public bool IsBehind(Transform target, Vector3 from)
    {
        Vector3 toNpc = Flat(from - target.position);
        if (toNpc.sqrMagnitude < 0.0001f)
            return true;

        return Vector3.Angle(Back(target), toNpc) <= m_config.AmbushRearHalfAngle;
    }

    /// <summary>표적이 from을 정면 반각 안에서 보고 있는지 판정한다.</summary>
    public bool IsWatched(Transform target, Vector3 from)
    {
        Vector3 toNpc = Flat(from - target.position);
        if (toNpc.sqrMagnitude < 0.0001f)
            return false;

        Vector3 forward = Flat(target.forward);
        if (forward.sqrMagnitude < 0.0001f)
            return false;

        return Vector3.Angle(forward, toNpc) <= m_config.AmbushViewHalfAngle;
    }

    /// <summary>표적 시선 밖이면 접근 지점을, 보이는 동안에는 반대쪽 지점을 돌려준다.</summary>
    public Vector3 ApproachPoint(Transform target, Vector3 from)
    {
        Vector3 back = Back(target);
        Vector3 toNpc = Flat(from - target.position);
        Vector3 dir = toNpc.sqrMagnitude > 0.0001f ? toNpc.normalized : back;

        Vector3 side = Vector3.Cross(Vector3.up, back);
        float sign = Vector3.Dot(dir, side) >= 0f ? 1f : -1f;

        if (!IsWatched(target, from))
        {
            return target.position
                   + back * m_config.AmbushApproachDistance
                   + side * (sign * m_config.AmbushSideSpread);
        }

        return from + dir * m_config.AmbushWalkAwayDistance;
    }

    private static Vector3 Back(Transform target)
    {
        Vector3 back = Flat(-target.forward);
        return back.sqrMagnitude > 0.0001f ? back.normalized : Vector3.forward;
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v;
    }
}
