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
    /// 기준점 주변 링(min~max 거리) 안에서 NavMesh 위 스폰 지점을 찾는다 — 시도 실패가 반복되면 false.
    /// 화면 밖·너무 붙지 않게 플레이어에게서 일정 거리를 두고 스폰하기 위함.
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
                NavMesh.SamplePosition(
                    candidate,
                    out NavMeshHit hit,
                    navSampleMaxDistance,
                    NavMesh.AllAreas
                )
            )
            {
                result = hit.position;
                return true;
            }
        }

        result = default;
        return false;
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
