using UnityEngine;

/// <summary>
/// 골반에서 뼈로 레이를 쏴 래그돌 뼈가 벽에 박혔는지 판정하고 빠져나갈 방향을 준다.
/// NPC·플레이어가 함께 쓰는 정적 헬퍼다.
/// </summary>
public static class RagdollWallProbe
{
    private const float k_maxNormalY = 0.5f;

    private const float k_minSurfaceDistance = 0.02f;

    private static readonly RaycastHit[] s_hits = new RaycastHit[16];

    private static int s_queryMask;
    private static bool s_maskReady;

    public readonly struct Pin
    {
        public readonly Collider Wall;

        public readonly Vector3 Normal;

        public readonly Vector3 Point;

        public readonly float SurfaceDistance;

        public readonly float BoneDistance;

        public Pin(Collider wall, Vector3 normal, Vector3 point, float surfaceDistance, float boneDistance)
        {
            Wall = wall;
            Normal = normal;
            Point = point;
            SurfaceDistance = surfaceDistance;
            BoneDistance = boneDistance;
        }

        public float PastSurface => BoneDistance - SurfaceDistance;

        public bool IsValid => Wall != null;
    }

    /// <summary>골반에서 뼈 콜라이더 사이를 막는 가장 가까운 벽면(사람 제외)을 찾는다.</summary>
    public static bool TryFindPinningWall(
        Vector3 from,
        Collider boneCollider,
        float clearance,
        System.Func<Collider, bool> isCharacter,
        out Pin pin
    )
    {
        pin = default;

        if (boneCollider == null)
            return false;

        Vector3 target = boneCollider.bounds.center;
        Vector3 segment = target - from;
        float distance = segment.magnitude;
        if (distance <= clearance + k_minSurfaceDistance)
            return false;

        int count = Physics.RaycastNonAlloc(
            from,
            segment / distance,
            s_hits,
            distance - clearance,
            QueryMask(),
            QueryTriggerInteraction.Ignore
        );

        int best = -1;
        for (int i = 0; i < count; i++)
        {
            if (s_hits[i].collider == null || s_hits[i].distance <= k_minSurfaceDistance)
                continue;

            if (Mathf.Abs(s_hits[i].normal.y) > k_maxNormalY)
                continue;

            if (isCharacter != null && isCharacter(s_hits[i].collider))
                continue;

            if (best < 0 || s_hits[i].distance < s_hits[best].distance)
                best = i;
        }

        if (best < 0)
            return false;

        pin = new Pin(
            s_hits[best].collider,
            s_hits[best].normal,
            s_hits[best].point,
            s_hits[best].distance,
            distance
        );
        return true;
    }

    private static int QueryMask()
    {
        if (s_maskReady)
            return s_queryMask;

        int layer = LayerMask.NameToLayer(RagdollRig.k_layerName);
        s_queryMask = layer >= 0 ? ~(1 << layer) : ~0;
        s_maskReady = true;
        return s_queryMask;
    }
}
