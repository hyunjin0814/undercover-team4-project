using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

/// <summary>
/// 돌발 이벤트 핸들러들이 공유하는 서버 권위 헬퍼 — 현장 플레이어 탐색·NavMesh 스폰 지점 산출·스폰물 정리. (#106)
/// 모두 서버(또는 오프라인)에서만 호출되는 것을 전제로 한다.
/// </summary>
public static class SuddenEventUtil
{
    /// <summary>네트워크 세션이 켜져 있는지 — 꺼져 있으면 스폰물을 NGO에 싣지 않고 로컬로만 다룬다. (NpcSpawner와 동일 판정)</summary>
    public static bool IsNetworkSessionActive =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;

    /// <summary>
    /// 행동 가능한 현장 플레이어 중 하나를 무작위로 고른다 — 없으면 null.
    /// 다운된 플레이어는 제외한다: 전원 다운이면 습격·소란을 걸 대상이 없으므로 이벤트 자체가 성립하지 않는다. (#105)
    /// (본부/현장 구분 도입 전이라 씬에 존재하는 <see cref="PlayerData"/>를 현장 플레이어로 본다)
    /// </summary>
    public static Transform FindRandomFieldPlayer()
    {
        PlayerData[] players = UnityEngine.Object.FindObjectsByType<PlayerData>(
            FindObjectsSortMode.None
        );

        // 후보를 배열 앞쪽에 모아두고 그 안에서 고른다 — 추가 할당 없이 다운된 플레이어를 걸러낸다
        int candidateCount = 0;
        for (int i = 0; i < players.Length; i++)
        {
            if (players[i].IsTargetable)
                players[candidateCount++] = players[i];
        }

        if (candidateCount == 0)
            return null;
        return players[Random.Range(0, candidateCount)].transform;
    }

    /// <summary>
    /// 기준점에서 <paramref name="maxRadius"/>(m) 이내의 가장 가까운 행동 가능한 현장 플레이어 — 없으면 null.
    /// 다운된 플레이어는 제외한다. (#105, #106)
    ///
    /// 물리 쿼리(OverlapSphere) 대신 플레이어 목록을 직접 훑는다 — 플레이어는 최대 6명이라 훨씬 저렴하고,
    /// 도시 씬처럼 콜라이더가 빽빽한 곳에서 논알록 버퍼가 넘쳐 표적을 놓치는 문제가 없다.
    /// </summary>
    public static PlayerData FindNearestFieldPlayer(Vector3 origin, float maxRadius)
    {
        PlayerData[] players = UnityEngine.Object.FindObjectsByType<PlayerData>(
            FindObjectsSortMode.None
        );

        PlayerData nearest = null;
        float nearestSqr = maxRadius * maxRadius; // 반경 밖은 애초에 후보가 되지 않는다
        for (int i = 0; i < players.Length; i++)
        {
            PlayerData player = players[i];
            if (!player.IsTargetable)
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

    /// <summary>
    /// 기준점에서 <paramref name="maxRadius"/>(m) 이내의 행동 가능한 현장 플레이어를 전부 <paramref name="results"/>에 모은다.
    /// 다운된 플레이어는 제외한다. 호출 시 리스트를 비우므로 호출자는 버퍼를 재사용할 수 있다. (#213)
    ///
    /// <see cref="FindNearestFieldPlayer"/>와 같은 기준(PlayerData 목록 직접 순회 + IsTargetable)을 쓴다 —
    /// 한쪽만 물리 쿼리를 쓰면 두 경로의 대상 집합이 어긋난다.
    /// </summary>
    public static void CollectFieldPlayers(Vector3 origin, float maxRadius, List<Transform> results)
    {
        results.Clear();

        PlayerData[] players = UnityEngine.Object.FindObjectsByType<PlayerData>(
            FindObjectsSortMode.None
        );

        float maxSqr = maxRadius * maxRadius;
        for (int i = 0; i < players.Length; i++)
        {
            PlayerData player = players[i];
            if (!player.IsTargetable)
                continue;

            if ((player.transform.position - origin).sqrMagnitude <= maxSqr)
                results.Add(player.transform);
        }
    }

    // 도달 가능성 검사용 경로 버퍼 — 서버(또는 오프라인)에서만 쓰므로 공유해도 안전하다
    private static readonly NavMeshPath s_reachPath = new NavMeshPath();

    /// <summary>
    /// 기준점 주변 링(min~max 거리) 안에서 <b>기준점에서 걸어갈 수 있는</b> NavMesh 스폰 지점을 찾는다 —
    /// 시도 실패가 반복되면 false.
    ///
    /// 화면 밖·너무 붙지 않게 일정 거리를 두는 것과 별개로, <b>NavMesh 위라는 것만으로는 부족하다</b>:
    /// 도시 NavMesh는 건물 내부·펜스 안쪽처럼 서로 끊긴 섬으로 쪼개져 있어서, 그냥 SamplePosition만
    /// 통과시키면 플레이어가 절대 갈 수 없는 곳에 폭탄·NPC가 생긴다(측정상 위치에 따라 20~100%).
    /// 그래서 <see cref="NavMesh.CalculatePath"/>가 <see cref="NavMeshPathStatus.PathComplete"/>를
    /// 낼 때만 후보로 받는다 — "그 플레이어가 지금 서 있는 곳에서 걸어서 닿는가"가 판정 기준이다.
    ///
    /// 근본 해결은 아니다(NavMesh 베이크가 건물 내부까지 굽는 것 자체가 원인) — 이건 잘못된 지점을
    /// 스폰 단계에서 걸러내는 방어다.
    /// </summary>
    public static bool TryFindSpawnPositionNear(
        Vector3 origin,
        float distanceMin,
        float distanceMax,
        float navSampleMaxDistance,
        int maxAttempts,
        out Vector3 result
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
                    NavMesh.AllAreas
                )
            )
                continue;

            if (!IsReachableFrom(origin, hit.position))
                continue; // NavMesh 위이긴 하나 기준 플레이어가 걸어갈 수 없는 섬 — 버린다

            result = hit.position;
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>
    /// <paramref name="from"/>에서 <paramref name="to"/>까지 NavMesh를 따라 실제로 걸어갈 수 있는가.
    ///
    /// 출발점이 NavMesh에 못 붙으면(플레이어가 NavMesh 밖 바닥을 밟고 있는 등) 판정을 포기하고 true를
    /// 준다 — 여기서 false를 주면 그 플레이어 주변에서는 어떤 이벤트도 발생하지 못하고 조용히 죽는다.
    /// 잘못된 위치 하나보다 이벤트가 통째로 멈추는 쪽이 나쁘다.
    /// </summary>
    public static bool IsReachableFrom(Vector3 from, Vector3 to)
    {
        if (!NavMesh.SamplePosition(from, out NavMeshHit start, 4f, NavMesh.AllAreas))
            return true;

        // PathPartial(중간까지만 감) · PathInvalid(경로 없음) 둘 다 "못 간다"로 본다
        return NavMesh.CalculatePath(start.position, to, NavMesh.AllAreas, s_reachPath)
            && s_reachPath.status == NavMeshPathStatus.PathComplete;
    }

    /// <summary>스폰물을 정리한다 — 네트워크 세션이면 Despawn, 아니면 Destroy. null·미스폰 상황을 안전하게 처리한다.</summary>
    public static void DespawnOrDestroy(GameObject target)
    {
        if (target == null)
            return;

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
