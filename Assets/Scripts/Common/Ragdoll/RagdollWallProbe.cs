using UnityEngine;

/// <summary>
/// 래그돌 뼈가 벽에 박혔는지 <b>레이로</b> 판정하고, 빠져나갈 방향을 준다. (#980)
///
/// <b>방향의 정당성이 기하 계산이 아니라 "어디서 쐈는가"에서 나온다.</b> 골반은 확실히 벽 밖이므로
/// 골반에서 쏜 레이가 처음 맞는 면은 반드시 <b>몸 쪽 면</b>이고, 그 법선은 골반을 향한다.
/// <c>ComputePenetration</c>의 MTV는 뼈가 벽 중앙면 근처면 부호가 뒤집혀, 그것을 믿고 몸을 옮겼다가
/// <b>몸이 벽 속으로 40cm 들어갔다</b> — 근거는 <c>docs/980-ragdoll-wall-stuck.md</c> §2-6·§2-7.
///
/// 겹침 깊이로 판정하지 않는 이유도 그 문서 §2-7이다: 겹침 0에는 "안 닿음"과 <b>"반대편으로 완전히
/// 관통함"</b> 두 뜻이 있어 관통한 팔을 놓친다. 레이는 그 경우도 잡는다.
///
/// <see cref="RagdollGround"/>와 같은 자리 — 리그를 소유한 쪽(NPC·플레이어)이 함께 쓰는 정적 헬퍼다.
/// </summary>
public static class RagdollWallProbe
{
    // 바닥·천장으로 보는 법선 기울기 — 이보다 세로면 벽이 아니다(계단·경사에 누운 다리가 걸린다).
    private const float k_maxNormalY = 0.5f;

    // 출발점이 이미 그 콜라이더 안이면 버린다(m) — 골반이 벽 밖이라는 전제가 깨진 히트다.
    private const float k_minSurfaceDistance = 0.02f;

    private static readonly RaycastHit[] s_hits = new RaycastHit[16];

    private static int s_queryMask; // 뼈 레이어만 뺀 마스크 — 벽·바닥·사람은 남기고 코드로 거른다
    private static bool s_maskReady;

    /// <summary>골반과 뼈 사이를 막고 있는 표면 한 벌.</summary>
    public readonly struct Pin
    {
        /// <summary>막고 있는 콜라이더 — 사람이 아님이 보장된다.</summary>
        public readonly Collider Wall;

        /// <summary>그 면의 법선 — <b>골반 쪽(=몸이 있는 쪽)</b>을 향한다. 이것이 탈출 방향이다.</summary>
        public readonly Vector3 Normal;

        public readonly Vector3 Point;

        /// <summary>골반 → 벽면(m).</summary>
        public readonly float SurfaceDistance;

        /// <summary>골반 → 뼈(m).</summary>
        public readonly float BoneDistance;

        public Pin(Collider wall, Vector3 normal, Vector3 point, float surfaceDistance, float boneDistance)
        {
            Wall = wall;
            Normal = normal;
            Point = point;
            SurfaceDistance = surfaceDistance;
            BoneDistance = boneDistance;
        }

        /// <summary>뼈가 벽면 너머로 들어간 깊이(m) — <b>겹침 깊이가 아니다.</b> 관통해도 커진다.</summary>
        public float PastSurface => BoneDistance - SurfaceDistance;

        public bool IsValid => Wall != null;
    }

    /// <summary>
    /// <paramref name="from"/>(골반)에서 <paramref name="boneCollider"/>로 쏴 사이를 막는
    /// <b>사람이 아닌 벽면</b>을 찾는다. 가장 가까운 것을 고른다 — 첫 히트만 보면 벽 앞에 사람이
    /// 서 있을 때 판정이 통째로 죽는다.
    /// </summary>
    /// <param name="clearance">뼈 표면을 스치는 히트를 버릴 여유(m).</param>
    /// <param name="isCharacter">
    /// 이 콜라이더가 <b>사람</b>인가 — 참이면 벽으로 세지 않는다.
    ///
    /// ⚠ <b>레이어 마스크로는 못 거른다.</b> 이 프로젝트에는 벽 전용 레이어가 없어 벽·바닥·플레이어
    /// 캡슐·NPC 캡슐이 전부 <c>Default</c>에 산다. <b>자기 자신의 루트 캡슐도 여기서 걸린다</b> —
    /// 안 거르면 모든 뼈가 "벽 뒤"로 판정된다(<c>NpcProneCollider</c>의 캡슐은 래그돌 중에도 켜져 있다).
    ///
    /// ⚠ <b>판정을 여기서 하지 않고 받는 이유</b>: 사람인지 아는 것은 도메인 타입
    /// (<c>NpcController</c>·<c>PlayerHealth</c>)인데 이 파일은 <c>Common/</c>에 있다 —
    /// "도메인에 속하지 않는 공유 부품"(<c>docs/architecture.md</c> §1)이 도메인을 알면 안 된다.
    /// 넘기는 쪽은 <c>NpcRagdoll</c>이다.
    /// </param>
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

        // 관절점이 아니라 콜라이더 중심을 노린다 — 벽에 끼는 것은 관절이 아니라 캡슐이다.
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
                continue; // 바닥·천장 — 이 판정이 다룰 것이 아니다

            if (isCharacter != null && isCharacter(s_hits[i].collider))
                continue; // 사람은 벽이 아니다 — 판정은 넘겨받는다(인자 주석)

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

    // 자기 뼈만 뺀다 — 같은 리그의 다른 뼈가 벽으로 잡히면 몸이 스스로를 막은 것이 된다.
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
