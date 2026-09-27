using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 스폰 포인트마다 탈출 가능 여부 판정용 기준점을 잡는다(탐침 간 경로 다수결).
/// 후보에서 기준점까지 경로가 완주해야 스폰을 허용한다.
/// </summary>
public static class NpcSpawnAnchors
{
    public const int k_defaultProbeCount = 8;

    private const int k_probeAttemptsPerSample = 25;

    public readonly struct Anchor
    {
        public readonly Vector3 Position;

        public readonly bool IsValid;

        public Anchor(Vector3 position, bool isValid)
        {
            Position = position;
            IsValid = isValid;
        }
    }

    /// <summary>스폰 포인트마다 기준점을 하나씩 잡는다. 스폰 시작 전 1회 호출한다.</summary>
    public static Anchor[] Resolve(IReadOnlyList<Transform> spawnPoints, float spawnRadius, float sampleMaxDistance, int spawnAreaMask, int pathAreaMask, int probeCount)
    {
        Anchor[] anchors = new Anchor[spawnPoints.Count];
        List<Vector3> probes = new List<Vector3>();
        NavMeshPath path = new NavMeshPath();
        int clampedProbeCount = Mathf.Max(1, probeCount);

        for (int i = 0; i < spawnPoints.Count; i++)
        {
            Transform point = spawnPoints[i];
            if (point == null)
            {
                anchors[i] = new Anchor(Vector3.zero, false);
                continue;
            }

            CollectProbes(probes, point.position, spawnRadius, sampleMaxDistance, spawnAreaMask, clampedProbeCount);
            if (probes.Count == 0)
            {
                anchors[i] = new Anchor(Vector3.zero, false);
                continue;
            }

            anchors[i] = new Anchor(PickMostConnected(probes, pathAreaMask, path), true);
        }

        return anchors;
    }

    /// <summary>후보가 기준점까지 완주하는 경로를 갖는지 판정한다.</summary>
    public static bool IsConnected(Vector3 candidate, Vector3 anchor, int pathAreaMask, NavMeshPath path)
    {
        return NavMesh.CalculatePath(candidate, anchor, pathAreaMask, path) && path.status == NavMeshPathStatus.PathComplete;
    }

    /// <summary>기준점끼리 서로 닿지 않는 쌍의 수를 센다.</summary>
    public static int CountDisconnectedPairs(Anchor[] anchors, int pathAreaMask)
    {
        NavMeshPath path = new NavMeshPath();
        int disconnected = 0;

        for (int i = 0; i < anchors.Length; i++)
        {
            if (!anchors[i].IsValid)
                continue;

            for (int j = i + 1; j < anchors.Length; j++)
            {
                if (!anchors[j].IsValid)
                    continue;

                if (!IsConnected(anchors[i].Position, anchors[j].Position, pathAreaMask, path))
                    disconnected++;
            }
        }

        return disconnected;
    }

    private static void CollectProbes(List<Vector3> probes, Vector3 center, float spawnRadius, float sampleMaxDistance, int spawnAreaMask, int probeCount)
    {
        probes.Clear();
        int maxAttempts = probeCount * k_probeAttemptsPerSample;

        for (int attempt = 0; attempt < maxAttempts && probes.Count < probeCount; attempt++)
        {
            Vector2 offset = Random.insideUnitCircle * spawnRadius;
            Vector3 candidate = center + new Vector3(offset.x, 0f, offset.y);

            if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, sampleMaxDistance, spawnAreaMask))
                probes.Add(hit.position);
        }
    }

    private static Vector3 PickMostConnected(List<Vector3> probes, int pathAreaMask, NavMeshPath path)
    {
        int[] reachCount = new int[probes.Count];

        for (int i = 0; i < probes.Count; i++)
        {
            for (int j = i + 1; j < probes.Count; j++)
            {
                if (!IsConnected(probes[i], probes[j], pathAreaMask, path))
                    continue;

                reachCount[i]++;
                reachCount[j]++;
            }
        }

        int best = 0;
        for (int i = 1; i < probes.Count; i++)
        {
            if (reachCount[i] > reachCount[best])
                best = i;
        }

        return probes[best];
    }
}
