using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

/// <summary>
/// 돌발 이벤트 공용 서버 헬퍼 — 현장 플레이어 탐색, NavMesh 스폰 지점 산출, 스폰물 정리.
/// </summary>
public static class SuddenEventUtil
{
    public static bool IsNetworkSessionActive =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    /// <summary>다운되지 않은 현장 플레이어 중 하나를 무작위로 고른다. 없으면 null.</summary>
    public static Transform FindRandomFieldPlayer()
    {
        IReadOnlyList<PlayerHealth> players = PlayerHealth.All;

        Transform picked = null;
        int candidateCount = 0;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null || !player.IsTargetable)
                continue;

            candidateCount++;
            if (Random.Range(0, candidateCount) == 0)
                picked = player.transform;
        }

        return picked;
    }

    /// <summary>origin에서 maxRadius 이내의 가장 가까운 행동 가능한 현장 플레이어를 찾는다. 없으면 null.</summary>
    public static PlayerHealth FindNearestFieldPlayer(Vector3 origin, float maxRadius)
    {
        IReadOnlyList<PlayerHealth> players = PlayerHealth.All;

        PlayerHealth nearest = null;
        float nearestSqr = maxRadius * maxRadius;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null || !player.IsTargetable)
                continue;

            float sqr = (player.transform.position - origin).sqrMagnitude;
            if (sqr <= nearestSqr)
            {
                nearestSqr = sqr;
                nearest = player;
            }
        }
        return nearest;
    }

    /// <summary>origin에서 maxRadius 이내의 행동 가능한 현장 플레이어를 모두 results에 모은다.</summary>
    public static void CollectFieldPlayers(Vector3 origin, float maxRadius, List<Transform> results)
    {
        Collect(origin, maxRadius, results, damageablesToo: false);
    }

    /// <summary>피해 판정용으로, 비행(Launched) 중인 플레이어까지 포함해 반경 내 플레이어를 모은다.</summary>
    public static void CollectDamageablePlayers(
        Vector3 origin,
        float maxRadius,
        List<Transform> results
    )
    {
        Collect(origin, maxRadius, results, damageablesToo: true);
    }

    private static void Collect(
        Vector3 origin,
        float maxRadius,
        List<Transform> results,
        bool damageablesToo
    )
    {
        results.Clear();

        IReadOnlyList<PlayerHealth> players = PlayerHealth.All;

        float maxSqr = maxRadius * maxRadius;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null)
                continue;
            if (!(damageablesToo ? player.IsDamageable : player.IsTargetable))
                continue;

            if ((player.transform.position - origin).sqrMagnitude <= maxSqr)
                results.Add(player.transform);
        }
    }

    private const float k_eyeHeight = 1.6f;
    private const float k_spawnTargetHeight = 0.9f;
    private const float k_viewDotThreshold = 0.26f;
    private const float k_visProbeRadius = 0.3f;
    private const int k_occluderMask = 1;

    /// <summary>지점이 모든 현장 플레이어의 시야에서 벗어나 있는지 판정한다(스폰 팝인 방지).</summary>
    public static bool IsHiddenFromFieldPlayers(Vector3 point)
    {
        IReadOnlyList<PlayerHealth> players = PlayerHealth.All;

        Vector3 target = point + Vector3.up * k_spawnTargetHeight;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i] == null)
                continue;

            Transform playerTransform = players[i].transform;
            Vector3 eye = playerTransform.position + Vector3.up * k_eyeHeight;
            Vector3 toTarget = target - eye;

            Vector3 flat = toTarget;
            flat.y = 0f;
            Vector3 forward = playerTransform.forward;
            forward.y = 0f;
            if (flat.sqrMagnitude > 0.0001f && forward.sqrMagnitude > 0.0001f
                && Vector3.Dot(forward.normalized, flat.normalized) < k_viewDotThreshold)
                continue;

            float distance = toTarget.magnitude;
            if (distance < 0.5f)
                return false;

            if (!Physics.SphereCast(
                    eye, k_visProbeRadius, toTarget / distance, out RaycastHit _,
                    distance, k_occluderMask, QueryTriggerInteraction.Ignore))
                return false;
        }

        return true;
    }

    /// <summary>프리팹 에이전트의 통행 마스크에서 스폰 금지 영역을 뺀 스폰용 영역 마스크를 돌려준다.</summary>
    public static int SpawnAreaMask(NpcController prefab)
    {
        NavMeshAgent agent = prefab != null ? prefab.GetComponent<NavMeshAgent>() : null;
        return NpcNavAreas.ExcludeSpawnAreas(agent != null ? agent.areaMask : NavMesh.AllAreas);
    }

    /// <summary>origin 주변 링(min~max) 안에서 NavMesh 위 스폰 지점을 찾는다. 반복 실패하면 false.</summary>
    public static bool TryFindSpawnPositionNear(
        Vector3 origin,
        float distanceMin,
        float distanceMax,
        float navSampleMaxDistance,
        int maxAttempts,
        int areaMask,
        out Vector3 result,
        bool hiddenFromPlayers = false
    )
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            Vector2 dir = Random.insideUnitCircle.normalized;
            float distance = Random.Range(distanceMin, distanceMax);
            Vector3 candidate = origin + new Vector3(dir.x, 0f, dir.y) * distance;

            if (
                !NavMesh.SamplePosition(
                    candidate,
                    out NavMeshHit hit,
                    navSampleMaxDistance,
                    areaMask
                )
            )
                continue;

            if (hiddenFromPlayers && !IsHiddenFromFieldPlayers(hit.position))
                continue;

            if (hiddenFromPlayers && FindNearestFieldPlayer(hit.position, distanceMin) != null)
                continue;

            result = hit.position;
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>스폰물을 Despawn(세션) 또는 Destroy(오프라인)로 정리하고, 필요하면 소멸 연출을 남긴다.</summary>
    public static void DespawnOrDestroy(GameObject target, bool playVfx = true)
    {
        if (target == null)
            return;

        if (playVfx)
        {
            NpcDespawnVfx vfx = target.GetComponent<NpcDespawnVfx>();
            if (vfx != null)
                vfx.ServerPlay();
        }

        if (IsNetworkSessionActive)
        {
            NetworkObject netObj = target.GetComponent<NetworkObject>();
            if (netObj != null && netObj.IsSpawned)
            {
                netObj.Despawn();
                return;
            }
        }

        UnityEngine.Object.Destroy(target);
    }
}
