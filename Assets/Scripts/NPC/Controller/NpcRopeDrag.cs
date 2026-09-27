using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// NPC 밧줄 도메인 부품 — 묶임·끌기·무게를 관리한다.
/// 끌리는 동안 NavMeshAgent를 끄고 위치를 직접 대입한다.
/// </summary>
public class NpcRopeDrag : NetworkBehaviour
{
    private NpcController m_owner;
    private NpcRagdoll m_ragdoll;

    private const float k_dragGroundSnapRadius = 1f;

    private bool m_roped;

    private readonly NetworkVariable<bool> m_ropedSynced = new(false);

    private readonly List<Transform> m_dragAnchors = new List<Transform>();

    private Vector3 m_dragVelocity;
    private Quaternion m_dragFacing;
    private float m_dragTravel;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
        m_ragdoll = GetComponent<NpcRagdoll>();
    }

    public bool IsRoped => IsSpawned && !IsServer ? m_ropedSynced.Value : m_roped;

    private int m_tetherCount;

    private readonly NetworkVariable<bool> m_tetheredSynced = new(false);

    public bool IsTethered => IsSpawned && !IsServer ? m_tetheredSynced.Value : m_tetherCount > 0;

    /// <summary>밧줄 연결이 하나 늘었음을 기록한다. 서버(또는 오프라인) 전용.</summary>
    internal void AddTether()
    {
        if (IsSpawned && !IsServer)
            return;

        m_tetherCount++;
        SyncTethered();
    }

    /// <summary>줄 하나가 풀렸다 — 연결 목록에서 실제로 빠질 때만 호출한다. 서버(또는 오프라인).</summary>
    internal void RemoveTether()
    {
        if (IsSpawned && !IsServer)
            return;

        m_tetherCount = Mathf.Max(0, m_tetherCount - 1);
        SyncTethered();
    }

    private void SyncTethered()
    {
        if (IsSpawned && IsServer)
            m_tetheredSynced.Value = m_tetherCount > 0;
    }

    /// <summary>묶임 표시를 모두 내린다(커스터디 이탈 시).</summary>
    internal void ClearTethers()
    {
        if (m_tetherCount == 0)
            return;

        m_tetherCount = 0;
        SyncTethered();
    }

    public float RopeLength =>
        UsesRagdollRope && m_ragdoll.RopeLength > 0f
            ? m_ragdoll.RopeLength
            : m_owner.RopeDragConfig.RopeLength;

    private float m_dragWeight = 1f;

    public float DragWeight => m_dragWeight;

    private readonly NetworkVariable<byte> m_draggerCountSynced = new(0);

    public int DraggerCount =>
        IsSpawned && !IsServer ? m_draggerCountSynced.Value : m_dragAnchors.Count;

    /// <summary>무게 배정 — 코어의 InitBehavior에서 서버(또는 오프라인) 1회 호출된다.</summary>
    internal void InitDragWeight() => m_dragWeight = m_owner.CommonConfig.PickWeight();

    private bool m_ignoresDragSpeedFloor;

    public bool IgnoresDragSpeedFloor => m_ignoresDragSpeedFloor;

    /// <summary>끌기 무게를 추첨 대신 고정값으로 설정한다. InitDragWeight 이후에 호출할 것.</summary>
    public void ServerSetDragWeight(float weight, bool ignoreSpeedFloor)
    {
        if (IsSpawned && !IsServer)
            return;

        m_dragWeight = Mathf.Max(0f, weight);
        m_ignoresDragSpeedFloor = ignoreSpeedFloor;
    }

    /// <summary>밧줄 끌기를 시작한다 — NavMeshAgent를 끄고 끄는 플레이어를 위협으로 기억한다.</summary>
    public void StartRopeDrag(Transform dragger = null)
    {
        if (IsSpawned && !IsServer)
            return;

        if (!m_roped)
        {
            m_dragVelocity = Vector3.zero;
            m_dragFacing = transform.rotation;
            m_dragTravel = 0f;
        }

        if (dragger != null)
        {
            m_owner.Reaction.ThreatTarget = dragger;
            if (!m_dragAnchors.Contains(dragger))
                m_dragAnchors.Add(dragger);
        }

        m_owner.StandUp.CancelStandUp();

        m_owner.Custody.SetSecuredByPlayer(true);

        SetRoped(true);
        SyncDraggerCount();

        NavMeshAgent agent = m_owner.Agent;
        if (agent != null && agent.enabled)
            agent.enabled = false;

        if (UsesRagdollRope)
            ServerAttachCorpseRope(dragger);
    }

    private bool UsesRagdollRope => m_ragdoll != null && m_ragdoll.IsRagdollActive;

    private bool m_corpseRopeAttached;

    /// <summary>이 플레이어가 지금 이 NPC에 장력을 걸고 있는지 돌려준다. 서버(또는 오프라인) 전용.</summary>
    public bool IsDraggedBy(Transform dragger) =>
        dragger != null && m_dragAnchors.Contains(dragger);

    internal Transform AnyDragger => m_dragAnchors.Count > 0 ? m_dragAnchors[0] : null;

    private int m_dragSlot;
    private int m_dragSlotCount = 1;

    /// <summary>끌리는 자리(부채꼴 배치용) 지정 — 끄는 플레이어가 매 프레임 갱신한다. 서버(또는 오프라인) 전용.</summary>
    public void SetDragSlot(int slot, int slotCount)
    {
        m_dragSlot = slot;
        m_dragSlotCount = Mathf.Max(1, slotCount);
    }

    private void SetRoped(bool value)
    {
        m_roped = value;
        if (IsSpawned && IsServer)
            m_ropedSynced.Value = value;
    }

    /// <summary>releaser를 장력에서 뺀다. 남은 참가자가 있으면 true, 마지막이면 NavMesh로 복귀시키고 false.</summary>
    public bool StopRopeDrag(Transform releaser)
    {
        if (IsSpawned && !IsServer)
            return false;

        if (releaser != null)
            m_dragAnchors.Remove(releaser);
        PruneDeadAnchors();

        if (m_corpseRopeAttached && releaser != null)
            ServerDetachCorpseRope(releaser);

        if (m_dragAnchors.Count > 0)
            return true;

        SetRoped(false);
        ServerDetachAllCorpseRopes();

        NavMeshAgent agent = m_owner.Agent;
        if (agent == null)
            return false;

        if (m_owner.Knockback.IsKnockedBack)
            return false;

        if (m_owner.Death.IsDead)
            return false;

        if (m_ragdoll != null && m_ragdoll.IsRagdollActive)
            return false;

        agent.enabled = true;

        if (m_owner.TryWarpNear(transform.position))
            return false;
        if (releaser != null && m_owner.TryWarpNear(releaser.position))
            return false;

        agent.Warp(transform.position);

        Debug.LogWarning(
            "NpcRopeDrag: 밧줄을 놓은 지점을 NavMesh에 붙이지 못했다 — 행방불명 처리 대기: "
                + $"{name} @{transform.position.ToString("F1")}",
            this
        );
        return false;
    }

    /// <summary>에이전트를 되살리지 않고 장력·묶임을 모두 끊는다(사망 전용, 사망 전이 전에 호출).</summary>
    internal void ServerClearDrag()
    {
        if (IsSpawned && !IsServer)
            return;

        m_dragAnchors.Clear();
        SetRoped(false);
        SyncDraggerCount();
        ClearTethers();

        ServerDetachAllCorpseRopes();
    }

    /// <summary>시체에 밧줄을 묶는다 — 전 피어가 각자 로컬 시체에 관절을 건다.</summary>
    private void ServerAttachCorpseRope(Transform dragger)
    {
        if (dragger == null)
            return;

        m_corpseRopeAttached = true;

        if (!IsSpawned)
        {
            AttachCorpseRope(dragger);
            return;
        }

        NetworkObject carrier = dragger.GetComponentInParent<NetworkObject>();
        if (carrier == null || !carrier.IsSpawned)
            return;

        AttachCorpseRopeRpc(new NetworkObjectReference(carrier));
    }

    /// <summary>이 참가자의 시체 밧줄 한 가닥만 푼다(멱등). 서버(또는 오프라인) 전용.</summary>
    private void ServerDetachCorpseRope(Transform dragger)
    {
        if (dragger == null)
            return;

        if (!IsSpawned)
        {
            DetachCorpseRope(dragger);
            return;
        }

        NetworkObject carrier = dragger.GetComponentInParent<NetworkObject>();
        if (carrier == null || !carrier.IsSpawned)
            return;

        DetachCorpseRopeRpc(new NetworkObjectReference(carrier));
    }

    /// <summary>걸린 시체 밧줄을 전부 푼다 — 서버(또는 오프라인) 진입점. 시체가 아니면 무동작. 멱등.</summary>
    private void ServerDetachAllCorpseRopes()
    {
        if (!m_corpseRopeAttached)
            return;

        m_corpseRopeAttached = false;

        if (!IsSpawned)
        {
            DetachAllCorpseRopes();
            return;
        }

        DetachAllCorpseRopesRpc();
    }

    /// <summary>시체 순간이동 후 원장 기준으로 끊긴 관절 밧줄을 다시 건다(멱등). 서버(또는 오프라인) 전용.</summary>
    internal void ServerReattachCorpseRopes()
    {
        if (IsSpawned && !IsServer)
            return;

        if (!m_roped || !UsesRagdollRope)
            return;

        PruneDeadAnchors();

        for (int i = 0; i < m_dragAnchors.Count; i++)
            ServerAttachCorpseRope(m_dragAnchors[i]);
    }

    [Rpc(SendTo.Everyone)]
    private void AttachCorpseRopeRpc(NetworkObjectReference carrierRef)
    {
        if (carrierRef.TryGet(out NetworkObject carrier))
            AttachCorpseRope(carrier.transform);
    }

    [Rpc(SendTo.Everyone)]
    private void DetachCorpseRopeRpc(NetworkObjectReference carrierRef)
    {
        if (carrierRef.TryGet(out NetworkObject carrier))
            DetachCorpseRope(carrier.transform);
    }

    [Rpc(SendTo.Everyone)]
    private void DetachAllCorpseRopesRpc() => DetachAllCorpseRopes();

    private void AttachCorpseRope(Transform carrier) =>
        m_ragdoll?.BeginRopePull(PlayerHeldItemView.ResolveRopeAnchor(carrier));

    private void DetachCorpseRope(Transform carrier) =>
        m_ragdoll?.EndRopePull(PlayerHeldItemView.ResolveRopeAnchor(carrier));

    private void DetachAllCorpseRopes() => m_ragdoll?.EndRopePull();

    private void PruneDeadAnchors()
    {
        for (int i = m_dragAnchors.Count - 1; i >= 0; i--)
            if (m_dragAnchors[i] == null)
                m_dragAnchors.RemoveAt(i);

        SyncDraggerCount();
    }

    private void SyncDraggerCount()
    {
        if (IsSpawned && IsServer)
            m_draggerCountSynced.Value = (byte)m_dragAnchors.Count;
    }

    /// <summary>밧줄 길이를 넘으면 끌리는 몸을 당긴다. 코어 Update에서 매 프레임 호출된다.</summary>
    internal void Tick()
    {
        if (!m_roped)
            return;

        PruneDeadAnchors();
        if (m_dragAnchors.Count == 0)
            return;

        NpcRopeDragConfig config = m_owner.RopeDragConfig;
        Vector3 npcPosition = transform.position;
        float ropeLength = config.RopeLength;

        float lateral =
            m_dragAnchors.Count > 1
                ? 0f
                : (m_dragSlot - (m_dragSlotCount - 1) * 0.5f) * config.DragSpacing;

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

                Vector3 right = Vector3.Cross(Vector3.up, direction);
                pull =
                    anchorPoint
                    + (direction * ropeLength + right * lateral).normalized * ropeLength;
            }

            pull.y = anchorPoint.y;
            targetSum += pull;
        }

        Vector3 target = targetSum / m_dragAnchors.Count;
        Vector3 anchor = anchorSum / m_dragAnchors.Count;

        Vector3 next = Vector3.SmoothDamp(
            npcPosition,
            target,
            ref m_dragVelocity,
            config.DragSmoothTime
        );

        Vector3 ropeDirection = anchor - next;
        ropeDirection.y = 0f;
        if (ropeDirection.sqrMagnitude > 0.0001f)
        {
            Quaternion facing = Quaternion.LookRotation(ropeDirection);
            m_dragFacing = Quaternion.Slerp(
                m_dragFacing,
                facing,
                1f - Mathf.Exp(-config.DragTurnSharpness * Time.deltaTime)
            );
        }

        m_dragTravel += (next - npcPosition).magnitude;
        float sway = Mathf.Sin(m_dragTravel * config.DragSwayFrequency) * config.DragSwayAngle;

        transform.SetPositionAndRotation(
            ResolveDragPosition(next),
            m_dragFacing * Quaternion.Euler(0f, sway, 0f)
        );
    }

    /// <summary>끌리는 몸의 다음 위치를 벽·바닥 지형에 맞춰 보정한다.</summary>
    private Vector3 ResolveDragPosition(Vector3 desired)
    {
        Vector3 delta = desired - transform.position;
        delta.y = 0f;
        float distance = delta.magnitude;

        if (
            distance > 0.001f
            && m_owner.SweepHitsObstacle(delta / distance, distance, out RaycastHit wall)
        )
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
