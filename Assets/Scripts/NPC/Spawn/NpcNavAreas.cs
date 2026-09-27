using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// NPC 통행의 도로 규칙 모음 — 평소 상태에서는 도로를 마스크에서 빼고, 추격·도주 등에서는 허용한다.
/// </summary>
public static class NpcNavAreas
{
    public const string k_roadAreaName = "Road";

    public const string k_jailAreaName = "Jail";

    public const string k_hqAreaName = "HQ";

    private static int s_roadArea = int.MinValue;
    private static int s_roadMask = -1;
    private static int s_jailMask = -1;
    private static int s_hqMask = -1;

    public static int RoadMask => ResolveMask(k_roadAreaName, ref s_roadMask);

    public static int RoadArea
    {
        get
        {
            if (s_roadArea == int.MinValue)
                s_roadArea = NavMesh.GetAreaFromName(k_roadAreaName);
            return s_roadArea;
        }
    }

    public static int JailMask => ResolveMask(k_jailAreaName, ref s_jailMask);

    public static int HqMask => ResolveMask(k_hqAreaName, ref s_hqMask);

    private static int ResolveMask(string areaName, ref int cache)
    {
        if (cache < 0)
        {
            int area = NavMesh.GetAreaFromName(areaName);
            cache = area >= 0 ? 1 << area : 0;
        }
        return cache;
    }

    /// <summary>도로를 뺀 마스크를 돌려준다(목적지 선택 전용).</summary>
    public static int ExcludeRoad(int areaMask)
    {
        int masked = areaMask & ~RoadMask;

        return masked != 0 ? masked : areaMask;
    }

    /// <summary>도로와 본부 실내를 뺀 스폰 지점 추첨용 마스크를 돌려준다.</summary>
    public static int ExcludeSpawnAreas(int areaMask)
    {
        int masked = areaMask & ~(RoadMask | HqMask);

        return masked != 0 ? masked : areaMask;
    }

    /// <summary>이 상태에서 도로 통행을 허용하는지 판정한다(평소 시민 생활만 금지).</summary>
    public static bool AllowsRoad(NpcState state)
    {
        return state != NpcState.Idle && state != NpcState.Walk;
    }

    /// <summary>position에서 clearance(m) 안에 도로가 있는지 판정한다.</summary>
    public static bool HasRoadWithin(Vector3 position, float clearance)
    {
        if (RoadMask == 0 || clearance <= 0f)
            return false;

        return NavMesh.SamplePosition(position, out NavMeshHit _, clearance, RoadMask);
    }

    private const float k_onAreaProbeRadius = 0.5f;

    /// <summary>발밑 폴리곤의 영역 비트마스크를 돌려준다. NavMesh 밖이면 0.</summary>
    public static int AreaMaskAt(Vector3 position)
    {
        return NavMesh.SamplePosition(position, out NavMeshHit hit, k_onAreaProbeRadius, NavMesh.AllAreas)
            ? hit.mask
            : 0;
    }

    /// <summary>지금 도로 위에 서 있는지 판정한다.</summary>
    public static bool IsOnRoad(Vector3 position)
    {
        if (RoadMask == 0)
            return false;

        return (AreaMaskAt(position) & RoadMask) != 0;
    }
}
