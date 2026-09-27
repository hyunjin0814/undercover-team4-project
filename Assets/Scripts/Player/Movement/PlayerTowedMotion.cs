using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 남이 내 몸을 옮기는 동안의 오너 로컬 추종 이동 — 호송(NPC 2명)과 운반(동료 밧줄) 두 경로를 다룬다.
/// 두 경로의 상호배제·우선순위를 관리하며, 실제 이동은 PlayerMovement에 위임한다.
/// </summary>
[RequireComponent(typeof(PlayerMovement))]
public class PlayerTowedMotion : MonoBehaviour
{
    [Header("운반되는 쪽 — 끌려가기 (#365)")]
    [Tooltip("끌기 간격(m) — 운반자와 이 거리 안쪽이면 끌려가지 않는다(줄이 늘어진 상태)")]
    [SerializeField] private float m_dragFollowDistance = 1.6f;

    [Tooltip("끌리는 몸이 목표 위치를 따라잡는 데 걸리는 시간(초) — 클수록 늦게, 크게 휘며 따라온다")]
    [SerializeField] private float m_dragSmoothTime = 0.14f;

    [Tooltip("몸이 끌리는 방향으로 도는 민감도(1/초)")]
    [SerializeField] private float m_dragTurnSharpness = 6f;

    [Tooltip("끌리며 좌우로 흔들리는 최대 각(도) — 0이면 흔들리지 않는다")]
    [SerializeField] private float m_dragSwayAngle = 7f;

    [Tooltip("흔들림 주기 — 끌린 거리 1m당 위상(라디안)")]
    [SerializeField] private float m_dragSwayFrequency = 1.6f;

    private const float k_escortTrailDistance = 0.75f;
    private const float k_escortLerpSpeed = 12f;

    public float RopeLength =>
        m_ragdoll != null && m_ragdoll.IsRagdollActive && m_ragdoll.RopeLength > 0f
            ? m_ragdoll.RopeLength
            : m_dragFollowDistance;

    private PlayerMovement m_movement;
    private PlayerRagdoll m_ragdoll;
    private PlayerJump m_jump;

    private bool m_escorted;
    private Transform m_escortAnchorA;
    private Transform m_escortAnchorB;

    private float m_escortMaxSpeed;

    private bool m_escortCollide;

    private Transform m_dragCarrier;
    private int m_dragCarrierCount;
    private Vector3 m_dragVelocity;
    private Quaternion m_dragFacing;
    private float m_dragTravel;

    public bool IsActive => m_escorted || m_dragCarrierCount > 0;

    private void Awake()
    {
        m_movement = GetComponent<PlayerMovement>();
        m_ragdoll = GetComponent<PlayerRagdoll>();
        m_jump = GetComponent<PlayerJump>();
    }

    /// <summary>추종 한 프레임을 진행한다. 호송이 운반보다 우선한다.</summary>
    public void Tick()
    {
        if (m_escorted)
        {
            UpdateEscortFollow();
            return;
        }

        if (m_dragCarrierCount > 0 && (m_ragdoll == null || !m_ragdoll.IsRagdollActive))
        {
            UpdateDraggedFollow();
        }
    }

    /// <summary>두 추종을 모두 푼다 — 디스폰·라운드 리셋처럼 서버 종료 지시가 못 올 수 있는 지점에서 부른다.</summary>
    public void StopAll()
    {
        EndEscortFollow();
        EndDraggedFollow();
    }

    /// <summary>CharacterController를 끄고 두 앵커 중점 뒤를 따라가는 호송 추종을 시작한다.</summary>
    public void BeginEscortFollow(
        Transform anchorA, Transform anchorB, float maxSpeed = 0f, bool collide = false)
    {
        m_escorted = true;
        m_escortAnchorA = anchorA;
        m_escortAnchorB = anchorB;
        m_escortMaxSpeed = maxSpeed;
        m_escortCollide = collide;

        if (!collide)
        {
            m_movement.SetControllerEnabled(false);
        }

        m_movement.ClearExternalVelocity();
        ReportGroundedOnEnter();
    }

    /// <summary>호송 추종 종료 — 호송 종료(광장 도착·중단) 시 PlayerPenaltyView가 호출한다.</summary>
    public void EndEscortFollow()
    {
        if (!m_escorted)
        {
            return;
        }

        bool wasCollide = m_escortCollide;

        m_escorted = false;
        m_escortAnchorA = null;
        m_escortAnchorB = null;
        m_escortMaxSpeed = 0f;
        m_escortCollide = false;

        if (!wasCollide)
            m_movement.SetControllerEnabled(true);
    }

    private void UpdateEscortFollow()
    {
        Transform a = m_escortAnchorA != null ? m_escortAnchorA : m_escortAnchorB;
        if (a == null)
            return;
        Transform b = m_escortAnchorB != null ? m_escortAnchorB : a;

        Vector3 forward = a.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f)
            forward = transform.forward;
        forward.Normalize();

        Vector3 mid = (a.position + b.position) * 0.5f;

        Vector3 targetPos = m_escortMaxSpeed > 0f
            ? mid
            : mid - forward * k_escortTrailDistance;

        float lerp = k_escortLerpSpeed * Time.deltaTime;

        Vector3 nextPos = m_escortMaxSpeed > 0f
            ? Vector3.MoveTowards(transform.position, targetPos, m_escortMaxSpeed * Time.deltaTime)
            : Vector3.Lerp(transform.position, targetPos, lerp);

        if (m_escortCollide)
        {
            Vector3 step = nextPos - transform.position;
            step.y = 0f;
            m_movement.MoveWithGravity(step);
        }
        else
        {
            transform.position = nextPos;
        }

        if (m_escortMaxSpeed <= 0f)
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(forward), lerp);
    }

    /// <summary>운반자를 따라가는 운반 추종을 시작한다. 참가자가 덧걸 때마다 호출된다.</summary>
    public void BeginDraggedFollow(Transform carrier)
    {
        if (carrier == null)
        {
            return;
        }

        bool wasActive = m_dragCarrierCount > 0;
        m_dragCarrierCount++;
        m_dragCarrier = carrier;

        bool ropePath = m_ragdoll != null && m_ragdoll.IsRagdollActive;

        if (ropePath)
        {
            m_ragdoll.BeginRopePull(PlayerHeldItemView.ResolveRopeAnchor(carrier));
            return;
        }

        if (!wasActive)
        {
            m_dragVelocity = Vector3.zero;
            m_dragFacing = transform.rotation;
            m_dragTravel = 0f;

            m_movement.ClearExternalVelocity();
            ReportGroundedOnEnter();
        }
    }

    /// <summary>참가자 한 명의 운반 추종만 끝낸다.</summary>
    public void EndDraggedFollow(Transform carrier)
    {
        m_dragCarrierCount = Mathf.Max(0, m_dragCarrierCount - 1);
        if (m_dragCarrierCount == 0)
        {
            m_dragCarrier = null;
            m_dragVelocity = Vector3.zero;
        }

        m_ragdoll?.EndRopePull(carrier);
    }

    /// <summary>운반 추종을 전부 끝낸다.</summary>
    public void EndDraggedFollow()
    {
        m_dragCarrier = null;
        m_dragCarrierCount = 0;
        m_dragVelocity = Vector3.zero;
        m_ragdoll?.EndRopePull();
    }

    private void UpdateDraggedFollow()
    {
        Vector3 self = transform.position;
        Vector3 anchor = m_dragCarrier.position;

        Vector3 toSelf = self - anchor;
        toSelf.y = 0f;
        float distance = toSelf.magnitude;

        Vector3 target = self;
        if (distance > m_dragFollowDistance)
            target = anchor + toSelf / distance * m_dragFollowDistance;
        target.y = self.y;

        Vector3 next = Vector3.SmoothDamp(self, target, ref m_dragVelocity, m_dragSmoothTime);
        Vector3 step = next - self;
        step.y = 0f;

        m_movement.MoveWithGravity(step);

        Vector3 dragDirection = anchor - transform.position;
        dragDirection.y = 0f;
        if (dragDirection.sqrMagnitude > 0.0001f)
        {
            Quaternion facing = Quaternion.LookRotation(dragDirection);
            m_dragFacing = Quaternion.Slerp(
                m_dragFacing, facing, 1f - Mathf.Exp(-m_dragTurnSharpness * Time.deltaTime));
        }

        m_dragTravel += new Vector2(step.x, step.z).magnitude;
        float sway = Mathf.Sin(m_dragTravel * m_dragSwayFrequency) * m_dragSwayAngle;
        transform.rotation = m_dragFacing * Quaternion.Euler(0f, sway, 0f);
    }

    private void ReportGroundedOnEnter()
    {
        if (m_jump != null)
        {
            m_jump.ReportGrounded(true);
        }
    }
}
