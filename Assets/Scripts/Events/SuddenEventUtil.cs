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
    /// (본부/현장 구분 도입 전이라 씬에 존재하는 <see cref="PlayerHealth"/>를 현장 플레이어로 본다)
    /// </summary>
    public static Transform FindRandomFieldPlayer()
    {
        PlayerHealth[] players = UnityEngine.Object.FindObjectsByType<PlayerHealth>(
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
    public static PlayerHealth FindNearestFieldPlayer(Vector3 origin, float maxRadius)
    {
        PlayerHealth[] players = UnityEngine.Object.FindObjectsByType<PlayerHealth>(
            FindObjectsSortMode.None
        );

        PlayerHealth nearest = null;
        float nearestSqr = maxRadius * maxRadius; // 반경 밖은 애초에 후보가 되지 않는다
        for (int i = 0; i < players.Length; i++)
        {
            PlayerHealth player = players[i];
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
    /// <see cref="FindNearestFieldPlayer"/>와 같은 기준(PlayerHealth 목록 직접 순회 + IsTargetable)을 쓴다 —
    /// 한쪽만 물리 쿼리를 쓰면 두 경로의 대상 집합이 어긋난다.
    /// </summary>
    public static void CollectFieldPlayers(Vector3 origin, float maxRadius, List<Transform> results)
    {
        results.Clear();

        PlayerHealth[] players = UnityEngine.Object.FindObjectsByType<PlayerHealth>(
            FindObjectsSortMode.None
        );

        float maxSqr = maxRadius * maxRadius;
        for (int i = 0; i < players.Length; i++)
        {
            PlayerHealth player = players[i];
            if (!player.IsTargetable)
                continue;

            if ((player.transform.position - origin).sqrMagnitude <= maxSqr)
                results.Add(player.transform);
        }
    }

    // ---- 스폰 시야 검사 (#332 A) — 팝인이 보이지 않을 지점만 통과시키기 위한 파라미터 ----
    // 플레이어 눈높이(m) — 1인칭 카메라 근사. 후보 지점은 NPC 몸통 높이로 본다.
    private const float k_eyeHeight = 1.6f;
    private const float k_spawnTargetHeight = 0.9f;
    // 수평 시야 반각의 코사인 — 서버는 원격 플레이어의 카메라 피치를 모르므로(요 회전만 동기화)
    // 수평 각도로만 판정한다. cos(75°)≈0.26 — 실제 화면보다 넉넉히 잡아 '보일지도 모르는' 쪽을 탈락시킨다.
    private const float k_viewDotThreshold = 0.26f;
    // 가림 판정 구 스윕 반지름(m) — 가는 레이는 창살·소품 틈을 지나 '가려짐'으로 오판한다(Synty 지오메트리).
    // 어느 정도 부피가 있는 것에 막혀야 진짜 가려진 것으로 본다.
    private const float k_visProbeRadius = 0.3f;
    // 가림물 레이어 — 환경(Default)만. 캐릭터·상호작용물은 가림물이 아니다 (#313의 벽 판정과 같은 기준)
    private const int k_occluderMask = 1;

    /// <summary>
    /// 후보 지점이 모든 현장 플레이어의 시야에서 벗어나 있는가 (#332 A) — 스폰 팝인 방지용.
    /// 시야 판정은 수평 각도(넉넉한 반각) + 환경 가림 구 스윕: 각도 밖이면 안 보이는 것으로,
    /// 각도 안이면 지형이 가려줄 때만 숨은 것으로 본다. 다운된 플레이어는 화면이 유효하므로 제외하지 않는다.
    /// </summary>
    public static bool IsHiddenFromFieldPlayers(Vector3 point)
    {
        PlayerHealth[] players = UnityEngine.Object.FindObjectsByType<PlayerHealth>(
            FindObjectsSortMode.None
        );

        Vector3 target = point + Vector3.up * k_spawnTargetHeight;
        for (int i = 0; i < players.Length; i++)
        {
            Transform playerTransform = players[i].transform;
            Vector3 eye = playerTransform.position + Vector3.up * k_eyeHeight;
            Vector3 toTarget = target - eye;

            // 수평 각도 판정 — 뒤·측면이면 이 플레이어에게는 안 보인다
            Vector3 flat = toTarget;
            flat.y = 0f;
            Vector3 forward = playerTransform.forward;
            forward.y = 0f;
            if (flat.sqrMagnitude > 0.0001f && forward.sqrMagnitude > 0.0001f
                && Vector3.Dot(forward.normalized, flat.normalized) < k_viewDotThreshold)
                continue;

            // 시야각 안 — 환경이 가려줄 때만 숨은 것으로 본다
            float distance = toTarget.magnitude;
            if (distance < 0.5f)
                return false; // 발밑 수준 — 무조건 보인다

            if (!Physics.SphereCast(
                    eye, k_visProbeRadius, toTarget / distance, out RaycastHit _,
                    distance, k_occluderMask, QueryTriggerInteraction.Ignore))
                return false; // 가림 없이 훤히 보인다 — 이 지점은 탈락
        }

        return true;
    }

    /// <summary>
    /// 스폰할 프리팹이 설 수 있는 영역 마스크 — 프리팹 에이전트의 통행 마스크에서 도로만 더 뺀다
    /// (라운드 시작부터 차도 한복판에 서 있지 않게, #634). <see cref="NpcSpawner"/>와 같은 계산이다.
    ///
    /// <b>안 걸면 못 가는 영역에 스폰된다</b> (#744) — 시민 마스크는 셀(Jail)·본부 실내(HQ)를 빼고
    /// 있는데 <c>NavMesh.AllAreas</c>로 고르면 그 안에 솟고, 딛고 선 폴리곤이 마스크 밖이라 굳는다.
    /// </summary>
    public static int SpawnAreaMask(NpcController prefab)
    {
        NavMeshAgent agent = prefab != null ? prefab.GetComponent<NavMeshAgent>() : null;
        return NpcNavAreas.ExcludeRoad(agent != null ? agent.areaMask : NavMesh.AllAreas);
    }

    /// <summary>
    /// 기준점 주변 링(min~max 거리) 안에서 NavMesh 위 스폰 지점을 찾는다 — 시도 실패가 반복되면 false.
    /// 화면 밖·너무 붙지 않게 플레이어에게서 일정 거리를 두고 스폰하기 위함.
    /// <paramref name="areaMask"/>는 <b>스폰할 프리팹이 설 수 있는 영역</b>이다 — <see cref="SpawnAreaMask"/>로
    /// 뽑아 넘길 것. 기본값을 두지 않는 이유는 빠뜨리면 그대로 버그가 되기 때문이다 (#744).
    /// <paramref name="hiddenFromPlayers"/>가 참이면 모든 현장 플레이어의 시야에서 벗어난 지점만
    /// 통과시킨다 (#332 A — 눈앞 팝인 방지). 전 시도가 시야에 걸리면 false — 호출부의 불발 폴백 유지.
    /// </summary>
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
                continue; // 누군가의 눈앞 — 팝인이 보인다, 다음 후보로

            // 기준 플레이어에게서는 링(min~max)이 거리를 보장하지만, 다른 플레이어 바로 옆일 수 있다 —
            // 시야 밖(등 뒤)이라도 코앞 스폰은 소란이 갑자기 터진 느낌이라 링 최소 거리를 전원에게 적용한다 (#332 A)
            if (hiddenFromPlayers && FindNearestFieldPlayer(hit.position, distanceMin) != null)
                continue;

            result = hit.position;
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>
    /// 스폰물을 정리한다 — 네트워크 세션이면 Despawn, 아니면 Destroy. null·미스폰 상황을 안전하게 처리한다.
    /// 대상 프리팹에 <see cref="NpcDespawnVfx"/>가 배선돼 있으면 사라지는 자리에 소멸 연출을 남긴다 —
    /// 라운드 종료 일괄 정리처럼 연출이 필요 없는 경로만 playVfx=false로 끈다. (#310)
    /// </summary>
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
