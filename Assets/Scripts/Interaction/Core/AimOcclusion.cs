using UnityEngine;

/// <summary>
/// 조준·가시선 판정에서 무엇이 앞을 막는지 정하는 공용 로직 — 테이저·진압봉·상호작용 가시선이 공유한다.
/// </summary>
public static class AimOcclusion
{
    private static readonly RaycastHit[] s_blockHits = new RaycastHit[32];

    /// <summary>후보 히트 중 가장 가까운 것의 인덱스를 돌려준다(excludedRoot 제외). 없으면 -1.</summary>
    public static int FindNearest(
        Vector3 origin,
        RaycastHit[] hits,
        int count,
        Transform excludedRoot
    )
    {
        if (hits == null)
        {
            return -1;
        }

        int limit = Mathf.Min(count, hits.Length);
        int nearestIndex = -1;
        float nearestDistance = float.PositiveInfinity;
        int firstZeroIndex = -1;

        for (int i = 0; i < limit; i++)
        {
            Collider collider = hits[i].collider;
            if (collider == null)
            {
                continue;
            }

            if (excludedRoot != null && collider.transform.IsChildOf(excludedRoot))
            {
                continue;
            }

            float distance = hits[i].distance;
            if (distance <= 0f)
            {
                if (firstZeroIndex < 0)
                {
                    firstZeroIndex = i;
                }
                continue;
            }

            if (distance >= nearestDistance)
            {
                continue;
            }

            nearestDistance = distance;
            nearestIndex = i;
        }

        return nearestIndex >= 0 ? nearestIndex : firstZeroIndex;
    }

    /// <summary>origin과 targetPoint 사이를 환경이 가로막는지 구 스윕으로 확인한다(사람은 제외).</summary>
    public static bool IsEnvironmentBlocked(
        Vector3 origin,
        Vector3 targetPoint,
        LayerMask blockMask,
        float probeRadius,
        Transform ignoredRoot
    )
    {
        Vector3 delta = targetPoint - origin;
        float distance = delta.magnitude - probeRadius;
        if (distance <= 0f)
        {
            return false;
        }

        int count = Physics.SphereCastNonAlloc(
            origin,
            probeRadius,
            delta.normalized,
            s_blockHits,
            distance,
            blockMask,
            QueryTriggerInteraction.Ignore
        );

        for (int i = 0; i < count; i++)
        {
            Collider collider = s_blockHits[i].collider;
            if (collider == null)
            {
                continue;
            }

            if (s_blockHits[i].distance <= 0f)
            {
                continue;
            }

            if (ignoredRoot != null && collider.transform.IsChildOf(ignoredRoot))
            {
                continue;
            }

            if (IsCharacter(collider))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsCharacter(Collider collider) =>
        collider.GetComponentInParent<CharacterController>() != null
        || collider.GetComponentInParent<NpcController>() != null;

    /// <summary>origin과 targetPoint 사이를 무언가 가로막는지 확인한다(targetRoot 제외).</summary>
    public static bool IsBlocked(
        Vector3 origin,
        Vector3 targetPoint,
        Transform targetRoot,
        LayerMask blockMask
    )
    {
        if (
            !Physics.Linecast(
                origin, targetPoint, out RaycastHit hit, blockMask, QueryTriggerInteraction.Ignore)
        )
        {
            return false;
        }

        return targetRoot == null || !hit.collider.transform.IsChildOf(targetRoot);
    }
}
