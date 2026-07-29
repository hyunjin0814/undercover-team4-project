using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

public partial class NpcController
{
    // ---- 밧줄 끌기 (#269) ----

    // 밧줄을 놓은 지점에서 NavMesh를 찾을 때의 탐색 반경(m).
    private const float k_ropeReleaseSnapRadius = 2f;

    // 끌리는 동안 매 프레임 바닥을 찾을 때의 탐색 반경(m) — 계단 한 칸을 넘길 만큼만.
    private const float k_dragGroundSnapRadius = 1f;

    private bool m_roped;

    // 끌리는 중인지를 클라이언트에도 알리는 동기화 플래그 — 서버만 기록한다(PlayerEscorter의 연행 플래그와 같은 관례).
    // 표현 계층(NpcAnimationDriver)이 이 값으로 누운 모션을 고르는데, 커스터디 상태는 Escorted(수갑 찬 걷기)라
    // 이게 없으면 원격 피어에서 NPC가 서서 끌려간다. (#369)
    private readonly NetworkVariable<bool> m_ropedSynced = new(false);

    // 장력을 거는 쪽(끄는 플레이어들)의 트랜스폼 — 서버(또는 오프라인) 전용. 여러 명이 같은 대상을
    // 함께 끌 수 있어(줄다리기) 목록이다. E로 놓은 참가자는 여기서 빠지고 줄만 남는다 —
    // 장력에 기여하지 않으므로 줄이 늘어나다 거리 초과로 끊긴다.
    private readonly List<Transform> m_dragAnchors = new List<Transform>();

    // 끌기 추종 상태 (서버·오프라인 전용) — 매 프레임 이어지는 값이라 StartRopeDrag에서 초기화한다.
    // 끄는 쪽이 아니라 끌리는 쪽이 갖는다: 한 플레이어가 여러 명을 끌면 이 상태도 NPC 수만큼 필요하다.
    private Vector3 m_dragVelocity; // SmoothDamp 관성
    private Quaternion m_dragFacing; // 흔들림을 뺀 몸 방향 — 여기에 sway를 얹어 최종 회전을 만든다
    private float m_dragTravel; // 끌린 누적 거리(m) — 흔들림 위상의 기준

    /// <summary>밧줄로 묶여 <b>누군가에게</b> 끌리는 중인가 — 참가자별 판정은 <see cref="IsDraggedBy"/>.
    /// 서버·오프라인은 실제 값으로, 원격 피어는 동기화 플래그로 판정. (#269/#369)</summary>
    public bool IsRoped => IsSpawned && !IsServer ? m_ropedSynced.Value : m_roped;

    /// <summary>밧줄 길이(m) — 표시(늘어짐 정도)와 서버 장력 판정이 같은 값을 쓴다.</summary>
    public float RopeLength => m_ropeDragConfig.RopeLength;

    /// <summary>밧줄 끌기 시작 — PlayerEscorter가 서버에서 호출. 위치를 끄는 플레이어가 직접 제어하므로
    /// NavMeshAgent를 끈다(켜져 있으면 에이전트가 위치를 도로 잡아당긴다).
    /// 커스터디 상태 전이(Escorted)는 호출부가 <b>이 호출 앞에</b> 한다 — 에이전트를 끈 뒤에 전이하면
    /// 직전 상태의 Exit이 꺼진 에이전트를 건드린다 (넉백 ServerApplyKnockback과 같은 순서).
    /// 끈 플레이어를 위협으로 기억한다 — 놓아준 뒤 도주 상태가 되면 그 플레이어에게서 도망친다.</summary>
    public void StartRopeDrag(Transform dragger = null)
    {
        if (IsSpawned && !IsServer)
            return;

        // 첫 참가자일 때만 추종 상태를 새로 잡는다 — 이전 끌기의 관성·위상이 남으면 첫 프레임에 튄다.
        // 이미 끌리는 중이면(합류) 건드리지 않는다: 리셋하면 남이 붙는 순간 끌려가던 몸이 멈칫한다.
        if (!m_roped)
        {
            m_dragVelocity = Vector3.zero;
            m_dragFacing = transform.rotation;
            m_dragTravel = 0f;
        }

        if (dragger != null)
        {
            ThreatTarget = dragger;
            if (!m_dragAnchors.Contains(dragger))
                m_dragAnchors.Add(dragger);
        }

        SetRoped(true);
        if (m_agent != null && m_agent.enabled)
            m_agent.enabled = false;
    }

    /// <summary>이 플레이어가 지금 이 NPC에 장력을 걸고 있는가 — 서버(또는 오프라인) 전용.
    /// 끄는 쪽(PlayerEscorter)의 "내가 이걸 끌고 있나"가 이 값을 그대로 쓴다 — 양쪽에 따로 두면 어긋난다.</summary>
    public bool IsDraggedBy(Transform dragger) =>
        dragger != null && m_dragAnchors.Contains(dragger);

    // 부채꼴 배치용 자리 번호 — 끄는 쪽이 매 프레임 알려준다.
    private int m_dragSlot;
    private int m_dragSlotCount = 1;

    /// <summary>끌리는 자리(부채꼴 배치용) 지정 — 끄는 플레이어가 매 프레임 갱신한다. 서버(또는 오프라인) 전용.</summary>
    public void SetDragSlot(int slot, int slotCount)
    {
        m_dragSlot = slot;
        m_dragSlotCount = Mathf.Max(1, slotCount);
    }

    // 끌기 플래그와 동기화 값을 함께 갱신 — 서버(또는 오프라인)에서만 호출된다.
    private void SetRoped(bool value)
    {
        m_roped = value;
        if (IsSpawned && IsServer)
            m_ropedSynced.Value = value;
    }

    /// <summary>밧줄 끌기 해제 — releaser를 장력에서 뺀다. 아직 남은 참가자가 있으면 <b>true</b>(끌기 계속).
    /// 마지막 한 명이 놓았을 때만 에이전트를 되살려 NavMesh로 복귀(Warp)시키고 false를 돌려준다 —
    /// 안 하면 이후 이동·상태 전이가 조용히 실패한다. 커스터디 행선지는 호출부가 정한다(놓기=Captured, 판정=유치장).
    /// releaser는 장력 목록에서 뺄 키이자, 놓은 자리가 NavMesh 밖일 때 대체 기준점이다.</summary>
    public bool StopRopeDrag(Transform releaser)
    {
        if (IsSpawned && !IsServer)
            return false;

        if (releaser != null)
            m_dragAnchors.Remove(releaser);
        PruneDeadAnchors();

        // 아직 잡고 있는 사람이 남아 있으면 끌기는 이어진다 — 여기서 멈추면 줄다리기가 성립하지 않는다
        if (m_dragAnchors.Count > 0)
            return true;

        SetRoped(false);
        if (m_agent == null)
            return false;

        // 넉백 비행 중이면 에이전트는 넉백이 쥐고 있다 — 여기서 되살리면 날아가던 몸을 NavMesh로 도로
        // 끌어내린다. 착지할 때 EndKnockback이 붙이므로 그냥 넘긴다. (끌던 중 폭발에 맞은 경우)
        if (m_knockbackActive)
            return false;

        m_agent.enabled = true;

        // NavMesh에 못 붙으면 이후 상태 전이의 isStopped·SetDestination이 조용히 실패해 NPC가 그 자리에
        // 굳는다(빌드 2 이슈 E). 놓은 자리 → 실패 시 놓는 플레이어 자리(거기까지 걸어왔으니 유효한 바닥) 순으로 시도.
        if (TryWarpNear(transform.position))
            return false;
        if (releaser != null && TryWarpNear(releaser.position))
            return false;

        Debug.LogWarning(
            $"NpcController: 밧줄을 놓은 지점을 NavMesh에 붙이지 못했다 — 이후 이동·상태 전이가 조용히 실패한다: {name}",
            this
        );
        return false;
    }

    // 파괴된 참가자(접속 종료·플레이어 오브젝트 소멸)를 걷어낸다 — 남겨두면 장력 계산이 가짜 null을 만진다.
    private void PruneDeadAnchors()
    {
        for (int i = m_dragAnchors.Count - 1; i >= 0; i--)
            if (m_dragAnchors[i] == null)
                m_dragAnchors.RemoveAt(i);
    }

    // 기준점 주변에서 NavMesh 위 지점을 찾아 에이전트를 붙인다 — 붙었으면 true.
    private bool TryWarpNear(Vector3 origin)
    {
        if (
            !NavMesh.SamplePosition(
                origin,
                out NavMeshHit hit,
                k_ropeReleaseSnapRadius,
                NavMesh.AllAreas
            )
        )
            return false;

        return m_agent.Warp(hit.position) && m_agent.isOnNavMesh;
    }

    /// <summary>
    /// 밧줄 장력으로 끌리는 몸을 끌어당긴다 — 서버(또는 오프라인) 매 프레임. (#269)
    /// 위치를 직접 대입하고 NetworkTransform이 전 클라에 복제하므로 원격 피어에서도 끌리는 위치가 맞는다.
    ///
    /// 뒤 고정점에 강체로 붙이지 않는다: (1) 밧줄 길이를 넘을 때만 당기고 (2) 늦게 따라오게 해서
    /// 코너를 돌면 몸이 바깥으로 끌려나오는 궤적이 생긴다.
    ///
    /// <b>호출 위치 주의</b> — Update의 넉백·스턴 게이트보다 <b>앞</b>이다. 묶인 채 기절한 대상은 스턴
    /// 오버레이를 단 채로 끌려가야 하기 때문(TickStun이 IsRoped면 타이머를 멈추는 것과 짝) —
    /// 게이트 뒤로 내리면 테이저→밧줄 콤보로 잡은 대상이 그 자리에 멈춘다.
    /// </summary>
    private void TickRopeDrag()
    {
        if (!m_roped)
            return;

        // 앵커가 사라지는 경우(끌던 플레이어 파괴 등)는 여기서 멈추기만 한다 —
        // 끌기 상태 정리는 PlayerEscorter 쪽 참조 정리가 맡는다.
        PruneDeadAnchors();
        if (m_dragAnchors.Count == 0)
            return;

        Vector3 npcPosition = transform.position;
        float ropeLength = m_ropeDragConfig.RopeLength;

        // 여러 명을 함께 끌 때 자리마다 옆으로 벌린다 — 혼자면(slotCount 1) 0이라 궤적이 예전과 같다.
        //
        // 단 경합(줄다리기) 중에는 벌리지 않는다. 자리 번호는 끄는 쪽이 "내가 끄는 대상들" 안에서 매기는데,
        // 참가자가 여럿이면 각자 자기 기준으로 매겨 같은 대상에 다른 번호가 들어온다(A는 2자리 중 1번,
        // B는 1자리 중 0번). 매 프레임 나중에 도는 쪽이 이겨 오프셋이 좌우로 떨린다.
        // 부채꼴은 애초에 "한 사람이 여러 명"을 벌리려는 장치라 경합에는 의미가 없다.
        float lateral = m_dragAnchors.Count > 1
            ? 0f
            : (m_dragSlot - (m_dragSlotCount - 1) * 0.5f) * m_ropeDragConfig.DragSpacing;

        // 참가자마다 "자기 밧줄이 허용하는 위치"를 내고 그 평균으로 간다 — 합력.
        // 같은 방향으로 끌면 그대로 끌려가고, 서로 반대로 당기면 두 목표가 상쇄돼 가운데서 멈춘다(줄다리기).
        // 밧줄이 늘어져 있으면(길이 안쪽) 그 참가자는 당기지 않는다 — 제자리에서 돌기만 하면 NPC는 가만히 있다.
        Vector3 targetSum = Vector3.zero;
        Vector3 anchorSum = Vector3.zero;
        for (int i = 0; i < m_dragAnchors.Count; i++)
        {
            Vector3 anchorPoint = m_dragAnchors[i].position;
            anchorSum += anchorPoint;

            Vector3 toNpc = npcPosition - anchorPoint;
            toNpc.y = 0f;
            float distance = toNpc.magnitude;

            Vector3 pull = npcPosition;
            if (distance > ropeLength)
            {
                Vector3 direction = toNpc / distance;

                // 자리 오프셋을 태워도 앵커와의 거리는 밧줄 길이로 유지한다 — 벌린 만큼 늘어나면
                // 뒤로 갈수록 줄이 길어져 끊김 판정(m_ropeBreakDistance)에 먼저 걸린다.
                Vector3 right = Vector3.Cross(Vector3.up, direction);
                pull =
                    anchorPoint
                    + (direction * ropeLength + right * lateral).normalized * ropeLength;
            }

            // 높이는 끄는 플레이어 기준으로 시드만 한다 — 실제 지면 스냅·벽 판정은 ResolveDragPosition이 확정한다 (#369)
            pull.y = anchorPoint.y;
            targetSum += pull;
        }

        Vector3 target = targetSum / m_dragAnchors.Count;
        Vector3 anchor = anchorSum / m_dragAnchors.Count; // 몸 방향 기준점 — 참가자들의 중점

        Vector3 next = Vector3.SmoothDamp(
            npcPosition,
            target,
            ref m_dragVelocity,
            m_ropeDragConfig.DragSmoothTime
        );

        // 몸 방향은 플레이어 회전이 아니라 밧줄 방향 — 제자리에서 마우스만 돌려도 NPC가 같이 돌지 않는다
        Vector3 ropeDirection = anchor - next;
        ropeDirection.y = 0f;
        if (ropeDirection.sqrMagnitude > 0.0001f)
        {
            Quaternion facing = Quaternion.LookRotation(ropeDirection);
            m_dragFacing = Quaternion.Slerp(
                m_dragFacing,
                facing,
                1f - Mathf.Exp(-m_ropeDragConfig.DragTurnSharpness * Time.deltaTime)
            );
        }

        // 끌린 거리에 비례해 좌우로 흔들린다 — 시간이 아니라 거리 기준이라 멈추면 흔들림도 멈춘다
        m_dragTravel += (next - npcPosition).magnitude;
        float sway =
            Mathf.Sin(m_dragTravel * m_ropeDragConfig.DragSwayFrequency)
            * m_ropeDragConfig.DragSwayAngle;

        transform.SetPositionAndRotation(
            ResolveDragPosition(next),
            m_dragFacing * Quaternion.Euler(0f, sway, 0f)
        );
    }

    /// <summary>
    /// 끌리는 몸의 다음 위치를 지형에 맞춘다. (#369)
    /// 끌기는 에이전트를 끄고 위치를 직접 대입하므로 NavMesh가 대신 풀어 주던 벽·바닥을 스스로 처리해야 한다 —
    /// 밧줄이 기본 검거가 되면 본부 실내에서 유치장까지 문틀·좁은 통로·계단을 상시로 지난다.
    /// </summary>
    private Vector3 ResolveDragPosition(Vector3 desired)
    {
        Vector3 delta = desired - transform.position;
        delta.y = 0f;
        float distance = delta.magnitude;

        // 벽 스윕(넉백 판정 재사용, 사람은 안 침 — #313/#339). 넉백처럼 '전부 멈춤'을 쓰면 끌기는 상태가
        // 이어져서 벽을 따라 빠져나가는 방향까지 막혀 영구히 낀다 — 파고드는 성분만 버리고 미끄러뜨린다.
        if (distance > 0.001f && SweepHitsObstacle(delta / distance, distance, out RaycastHit wall))
        {
            Vector3 normal = wall.normal;
            normal.y = 0f;
            if (normal.sqrMagnitude > 0.0001f)
            {
                normal.Normalize();
                float into = Vector3.Dot(delta, normal);
                if (into < 0f)
                {
                    Vector3 slide = delta - normal * into;
                    desired.x = transform.position.x + slide.x;
                    desired.z = transform.position.z + slide.z;
                }
            }
        }

        // 지면 스냅 — 끄는 플레이어의 발밑 높이를 그대로 쓰면 계단·경사에서 뜨거나 바닥에 박힌다.
        // 찾지 못하면(NavMesh 밖으로 끌려나간 순간 등) 넘겨받은 높이를 그대로 둔다.
        if (
            NavMesh.SamplePosition(
                desired,
                out NavMeshHit ground,
                k_dragGroundSnapRadius,
                NavMesh.AllAreas
            )
        )
            desired.y = ground.position.y;

        return desired;
    }
}
