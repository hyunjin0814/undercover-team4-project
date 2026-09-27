using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 다음 이동 목적지 추첨 — 무작위 후보를 NavMesh에 스냅하고 경로가 완주하는 것만 고른다.
/// 도로는 목적지에서만 제외하며, 배회·질주 상태가 공유한다.
/// </summary>
public static class NpcMovePoint
{
    private static NavMeshPath s_pathProbe;

    /// <summary>도달 가능한 다음 목적지를 뽑는다. 없으면 NavMesh 위 차선 지점을 돌려준다.</summary>
    public static bool TryPick(
        NavMeshAgent agent,
        float minDistance,
        float maxDistance,
        float sampleRadius,
        int maxAttempts,
        int maxProbes,
        out Vector3 point,
        out bool reachable
    )
    {
        point = default;
        reachable = false;

        if (agent == null || !agent.isOnNavMesh)
            return false;

        Vector3 origin = agent.transform.position;
        int mask = NpcNavAreas.ExcludeRoad(agent.areaMask);

        Vector3? fallback = null;
        int probes = 0;

        for (int i = 0; i < maxAttempts && probes < maxProbes; i++)
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float distance = Random.Range(minDistance, maxDistance);
            Vector3 direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
            Vector3 candidate = origin + direction * distance;

            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, sampleRadius, mask))
                continue;

            fallback ??= hit.position;

            probes++;
            if (!IsReachable(agent, origin, hit.position))
                continue;

            point = hit.position;
            reachable = true;
            return true;
        }

        if (fallback == null)
            return false;

        point = fallback.Value;
        return true;
    }

    private static bool IsReachable(NavMeshAgent agent, Vector3 origin, Vector3 point)
    {
        s_pathProbe ??= new NavMeshPath();

        return NavMesh.CalculatePath(origin, point, agent.areaMask, s_pathProbe)
            && s_pathProbe.status == NavMeshPathStatus.PathComplete;
    }
}
