using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 남이 내 몸을 옮기는 동안의 추종 이동 — 오너 로컬 전용. (#279, #365)
/// 두 경로가 있고 <b>동시에 성립하지 않는다</b>:
/// <list type="bullet">
/// <item><b>호송(#279)</b> — 오검거 페널티로 NPC 2명이 끌고 간다. CharacterController를 끄고
/// 두 앵커 중점 살짝 뒤로 직접 이동한다(중력 없음 — 광장까지 정해진 길로 끌려간다).</item>
/// <item><b>운반(#365)</b> — 기능 정지(Die)된 몸을 동료가 밧줄로 끌고 간다. CharacterController를
/// 켠 채 Move로 밀어 벽·계단·경사를 CC가 스스로 풀게 한다.</item>
/// </list>
///
/// 둘을 한 컴포넌트에 둔 이유는 구현이 아니라 <b>규칙</b>을 공유하기 때문이다 — 상호배제·우선순위·
/// 정리 경로·진입 시 접지 보고·입력 이동 억제가 그것이다. 특히 상호배제는 실제로 대가를 치른 규칙이라
/// (Die된 몸을 호송이 접수해 광장으로 순간이동시키던 문제 — WrongfulArrestPenalty.HandlePenaltyCaught의
/// IsDead 가드) 주인이 필요했다. 예전에는 PlayerMovement.Update의 분기 <i>순서</i>가 암묵적으로
/// 그 역할을 했다.
///
/// 이동 자체는 <see cref="PlayerMovement"/>에 위임한다 — 수직 속도(중력·점프·넉백의 공용 채널)와
/// CharacterController의 소유자가 하나여야 하기 때문. 여기서 직접 만지면 같은 값을 두 컴포넌트가
/// 따로 적분하게 된다.
///
/// 실행은 <see cref="PlayerMovement"/>가 <see cref="Tick"/>으로 돌린다(자체 Update 없음) —
/// 그쪽이 비오너에서 꺼지므로 이 컴포넌트도 자동으로 오너 전용이 된다.
/// </summary>
[RequireComponent(typeof(PlayerMovement))]
public class PlayerTowedMotion : MonoBehaviour
{
    // 기능 정지(Die) 동료를 끌고 가는 연출 (#365) — 밧줄 끌기(#269)와 같은 수식·같은 감각을 쓴다.
    // 값도 PlayerEscorter.RopeDrag의 기본값에 맞춰 두었다.
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

    // 호송 추종의 뒤따르는 간격(m)과 보간 속도(1/초) — 연출 값이라 튜닝 대상이 아니다.
    private const float k_escortTrailDistance = 0.75f;
    private const float k_escortLerpSpeed = 12f;

    /// <summary>지금 이 몸을 잡고 있는 밧줄의 길이(m) — 밧줄 표시(<c>RopeDragView</c>)의 늘어짐 기준이다.
    /// <see cref="NpcRopeDrag.RopeLength"/>와 같은 갈림이고, 그쪽 주석이 근거를 갖는다. (#365/#644)</summary>
    public float RopeLength =>
        m_ragdoll != null && m_ragdoll.IsRagdollActive && m_ragdoll.RopeLength > 0f
            ? m_ragdoll.RopeLength
            : m_dragFollowDistance;

    private PlayerMovement m_movement;
    private PlayerRagdoll m_ragdoll; // 사망 래그돌 — 운반을 밧줄(물리)로 넘긴다 (#506)
    private PlayerJump m_jump; // 진입 시 접지 보고 — 공중에서 붙잡히면 낙하 상태가 고착된다 (#189)

    // 호송(#279) — 앵커가 파괴돼도 m_escorted가 참인 동안은 입력 이동으로 돌아가지 않는다
    // (서버의 종료/스냅 텔레포트가 마무리한다).
    private bool m_escorted;
    private Transform m_escortAnchorA;
    private Transform m_escortAnchorB;

    // 0이면 기존 지수 보간. 0보다 크면 그 속도(m/s)를 넘지 않는다 — UFO 흡입(#819)이 쓴다.
    private float m_escortMaxSpeed;

    // 참이면 CC를 켠 채 CC.Move로 따라간다(벽 스윕을 CC가 푼다) — 납치 지상 호송 전용 (#902).
    // 거짓이면 기존처럼 CC를 끄고 transform을 직접 옮긴다(오검거 호송·UFO·맨홀 하강).
    private bool m_escortCollide;

    // 운반(#365) — 나를 끌고 가는 플레이어. 여럿이 덧걸 수 있어(합류) 수만 센다 — 실제 견인은
    // 래그돌 경로에서 PlayerRagdoll/RagdollRope가 참가자별 가닥으로 들고 있으므로, 여기서는
    // "지금 누구 하나라도 끄는가"만 알면 된다. m_dragCarrier는 비래그돌 폴백(위치 추종)의 대표
    // 한 명일 뿐이다 — 그 경로는 운반이 항상 Die(래그돌)를 전제해 실제로는 도달하지 않는다.
    private Transform m_dragCarrier;
    private int m_dragCarrierCount;
    private Vector3 m_dragVelocity;  // SmoothDamp 관성
    private Quaternion m_dragFacing; // 흔들림을 뺀 몸 방향 — 여기에 sway를 얹어 최종 회전을 만든다
    private float m_dragTravel;      // 끌린 누적 거리(m) — 흔들림 위상의 기준

    /// <summary>지금 남에게 옮겨지는 중인가 — 참이면 입력 이동 대신 <see cref="Tick"/>이 돈다.</summary>
    public bool IsActive => m_escorted || m_dragCarrierCount > 0;

    private void Awake()
    {
        m_movement = GetComponent<PlayerMovement>();
        m_ragdoll = GetComponent<PlayerRagdoll>();
        m_jump = GetComponent<PlayerJump>();
    }

    /// <summary>
    /// 추종 한 프레임 — <see cref="PlayerMovement"/>가 입력 이동 대신 부른다.
    /// 호송이 운반보다 우선한다: 예전 Update의 분기 순서를 그대로 옮긴 것으로, 오검거 호송은
    /// 서버가 광장까지 책임지는 시퀀스라 도중에 운반으로 넘어가면 안 된다.
    /// </summary>
    public void Tick()
    {
        if (m_escorted)
        {
            UpdateEscortFollow();
            return;
        }

        // 래그돌은 밧줄이 물리로 끌고 캡슐이 그걸 따라간다 — 여기서 위치를 옮기면 둘이 싸운다.
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

    // ---- 호송 (#279, 오검거 페널티) ----

    /// <summary>
    /// 호송 추종 시작 — 오너 로컬 전용, PlayerPenaltyView가 서버 지시로 호출한다. (#279)
    /// CharacterController를 끄고 매 프레임 두 앵커(양옆 끌기 NPC — 전 피어에 NetworkTransform으로
    /// 동기화된 위치) 중점 살짝 뒤를 따라간다 — 오너가 움직여야 내 위치가 전 피어에 전파된다.
    /// </summary>
    /// <param name="maxSpeed">0보다 크면 이 속도(m/s)를 넘지 않는다 — 떠오르는 속도를 서버가
    /// 정해야 하는 UFO 흡입(#819)용. 0이면 지금까지대로 남은 거리에 비례해 따라붙는다.</param>
    /// <param name="collide">참이면 CC를 켠 채 CC.Move로 따라간다 — 벽 스윕·미끄러짐·지면 스냅을
    /// CC가 그대로 풀게 한다(운반(#365)과 같은 방식). 납치 지상 호송 전용이다 (#902): 오검거 호송과
    /// 맨홀 하강(#775)은 거짓을 써야 한다 — 하강은 CC가 켜져 있으면 지면을 통과하지 못한다.</param>
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
            // 직접 transform 이동 — 켜 두면 CC 내부 캐시가 위치를 되돌린다 (PlayerMovement.SetPose와 동일 사정).
            // collide 모드는 CC를 건드리지 않는다 — 벽 스윕이 필요해 켜 둔 채로 부른 것이다.
            m_movement.SetControllerEnabled(false);
        }

        m_movement.ClearExternalVelocity(); // 날아가던 중에 붙잡히면 그 속도가 감쇠 없이 남는다
        ReportGroundedOnEnter();
    }

    /// <summary>호송 추종 종료 — 호송 종료(광장 도착·중단) 시 PlayerPenaltyView가 호출한다. (#279)</summary>
    public void EndEscortFollow()
    {
        if (!m_escorted)
        {
            return; // CC를 괜히 다시 켜지 않는다 — 다른 사정으로 꺼 둔 것을 덮을 수 있다
        }

        bool wasCollide = m_escortCollide;

        m_escorted = false;
        m_escortAnchorA = null;
        m_escortAnchorB = null;
        m_escortMaxSpeed = 0f;
        m_escortCollide = false;

        // collide 모드는 CC를 끈 적이 없으니 다시 켤 것도 없다 — 다른 사정으로 꺼 둔 것을 덮지 않는다
        // (위 가드와 같은 이유).
        if (!wasCollide)
            m_movement.SetControllerEnabled(true);
    }

    // 끌기 NPC 추종 — 두 앵커 중점 뒤(끌리는 몸)를 부드럽게 따라간다. 한쪽이 파괴되면 남은 쪽만 따른다.
    private void UpdateEscortFollow()
    {
        Transform a = m_escortAnchorA != null ? m_escortAnchorA : m_escortAnchorB;
        if (a == null)
            return; // 앵커 전부 소실 — 그 자리에서 대기, 서버의 종료/스냅 텔레포트가 마무리한다
        Transform b = m_escortAnchorB != null ? m_escortAnchorB : a;

        Vector3 forward = a.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f)
            forward = transform.forward;
        forward.Normalize();

        Vector3 mid = (a.position + b.position) * 0.5f;

        // 간격은 '질질 끌리는' 그림을 만드는 값이라 위로 빨려 올라갈 때는 쓰지 않는다 —
        // 도는 기체를 앵커로 삼으면 그 간격만큼 옆으로 계속 흔들린다. (#819)
        Vector3 targetPos = m_escortMaxSpeed > 0f
            ? mid
            : mid - forward * k_escortTrailDistance;

        float lerp = k_escortLerpSpeed * Time.deltaTime;

        // 지수 보간은 남은 거리에 비례해 빨라진다 — 서버가 정한 속도를 지켜야 하면 쓸 수 없다
        Vector3 nextPos = m_escortMaxSpeed > 0f
            ? Vector3.MoveTowards(transform.position, targetPos, m_escortMaxSpeed * Time.deltaTime)
            : Vector3.Lerp(transform.position, targetPos, lerp);

        if (m_escortCollide)
        {
            // CC.Move에 맡긴다 — 운반(#365)의 UpdateDraggedFollow와 같은 방식. 벽에 부딪히면 스윕이
            // 걸리며, 계단·경사에서는 CC가 알아서 지면에 붙인다. 높이는 직접 만지지 않는다 — 중력은
            // MoveWithGravity가 적분한다(#902).
            Vector3 step = nextPos - transform.position;
            step.y = 0f;
            m_movement.MoveWithGravity(step);
        }
        else
        {
            transform.position = nextPos;
        }

        // 방향도 위치와 같은 이유로 갈린다 — 걷는 앵커의 forward는 진행 방향이라 몸을 그리로 돌리는
        // 것이 맞지만, UFO는 제자리 자전이라 forward가 매 프레임 도는 값일 뿐이다. 그대로 따라가면
        // 쓰러진 상태 카메라(화면이 몸을 따라간다)가 함께 빙글빙글 돈다 — 위로 끌려가는 동안은
        // 방향을 고정해 둔다. (#819)
        if (m_escortMaxSpeed <= 0f)
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(forward), lerp);
    }

    // ---- 운반 (#365, 동료가 밧줄로) ----

    /// <summary>
    /// 운반 추종 시작 — 오너 로컬 전용, <see cref="PlayerCarrier"/>가 서버 지시로 호출한다. (#365)
    /// 기능 정지(Die)된 몸을 동료가 끌고 가는 동안 오너가 스스로 따라가야 위치가 전 피어에 전파된다
    /// (NetworkTransform 오너 권한 — 오검거 호송 #279와 같은 사정).
    ///
    /// <b>참가자마다 호출된다</b> — 합류(덧걸기)가 이 함수를 다시 부르므로 <see cref="m_dragCarrierCount"/>로
    /// 인원을 센다. 래그돌 경로는 참가자 수와 무관하게 매번 <see cref="PlayerRagdoll.BeginRopePull"/>을
    /// 불러 자기 가닥을 추가한다(실제 다인 견인은 그쪽 관절이 든다).
    /// </summary>
    public void BeginDraggedFollow(Transform carrier)
    {
        if (carrier == null)
        {
            return;
        }

        bool wasActive = m_dragCarrierCount > 0;
        m_dragCarrierCount++;
        m_dragCarrier = carrier; // 비래그돌 폴백의 대표 — 항상 최신 참가자를 가리킨다(아래 근거)

        // 래그돌이면 <b>몸을 직접 밧줄로 묶는다</b> — 아래의 위치 추종을 쓰지 않는다 (#506 §9-7).
        //
        // 위치 추종은 "캡슐을 목표 위치로 옮기고 몸은 알아서 따라오게" 하는 방식인데, 몸이 래그돌이면
        // 따라올 수단이 없다(동적 리지드바디는 부모 트랜스폼을 따르지 않는다). 그래서 시체를 물리로
        // 끌고, 캡슐은 PlayerMovement.Update의 래그돌 분기가 시체를 따라가게 둔다.
        bool ropePath = m_ragdoll != null && m_ragdoll.IsRagdollActive;

        if (ropePath)
        {
            m_ragdoll.BeginRopePull(PlayerHeldItemView.ResolveRopeAnchor(carrier));
            return;
        }

        // 첫 참가자일 때만 추종 상태를 새로 잡는다 — 합류 중에 리셋하면 끌려가던 몸이 멈칫한다
        // (NpcRopeDrag.StartRopeDrag와 같은 관례). 이 경로는 비래그돌 전용이라 실제로는 도달하지 않는다.
        if (!wasActive)
        {
            m_dragVelocity = Vector3.zero;
            m_dragFacing = transform.rotation;
            m_dragTravel = 0f;

            m_movement.ClearExternalVelocity(); // 호송 진입과 같은 이유 (BeginEscortFollow 참고)
            ReportGroundedOnEnter();
        }
    }

    // 밧줄을 묶을 지점(운반자의 손)을 고르는 근거는 PlayerHeldItemView.ResolveRopeAnchor에 있다 —
    // NPC 시체 끌기(#571)가 같은 판정을 쓰게 되면서 앵커의 주인 쪽으로 옮겼다.

    /// <summary>참가자 한 명의 운반 추종만 끝낸다(그 가닥만) — 남은 참가자가 있으면 추종은 계속된다.
    /// <see cref="PlayerCarrier"/>가 호출한다. (#365, 합류)</summary>
    public void EndDraggedFollow(Transform carrier)
    {
        m_dragCarrierCount = Mathf.Max(0, m_dragCarrierCount - 1);
        if (m_dragCarrierCount == 0)
        {
            m_dragCarrier = null;
            m_dragVelocity = Vector3.zero;
        }

        m_ragdoll?.EndRopePull(carrier); // 래그돌 경로였으면 이 가닥만 푼다 (아니었으면 무동작)
    }

    /// <summary>운반 추종을 전부 끝낸다 — 디스폰·라운드 리셋처럼 서버 지시 없이 정리해야 하는 경로.
    /// <see cref="StopAll"/>이 쓴다. (#365)</summary>
    public void EndDraggedFollow()
    {
        m_dragCarrier = null;
        m_dragCarrierCount = 0;
        m_dragVelocity = Vector3.zero;
        m_ragdoll?.EndRopePull(); // 래그돌 경로였으면 밧줄을 전부 푼다 (아니었으면 무동작)
    }

    // 운반자 추종 — 밧줄 끌기(PlayerEscorter.ServerUpdateDrag)와 같은 수식이다: 간격을 넘을 때만
    // 당기고, 늦게 따라오게 해서 코너에서 몸이 바깥으로 끌려나오는 궤적을 만든다.
    // 다른 점은 적용 방식뿐 — transform 대입이 아니라 CharacterController.Move다. NPC 쪽에서 손으로
    // 짜야 했던 벽 스윕·미끄러짐·지면 스냅(ResolveDragPosition)을 CC가 그대로 해 준다.
    private void UpdateDraggedFollow()
    {
        Vector3 self = transform.position;
        Vector3 anchor = m_dragCarrier.position;

        Vector3 toSelf = self - anchor;
        toSelf.y = 0f;
        float distance = toSelf.magnitude;

        // 간격 안쪽이면 당기지 않는다 — 운반자가 제자리에서 돌기만 하면 몸은 가만히 있는다
        Vector3 target = self;
        if (distance > m_dragFollowDistance)
            target = anchor + toSelf / distance * m_dragFollowDistance;
        target.y = self.y; // 높이는 중력이 정한다

        Vector3 next = Vector3.SmoothDamp(self, target, ref m_dragVelocity, m_dragSmoothTime);
        Vector3 step = next - self;
        step.y = 0f;

        // 중력은 이동 소유자(PlayerMovement)가 든다 — 끌려가다 계단·경사를 만나면 CC가 붙여 준다
        m_movement.MoveWithGravity(step);

        // 몸 방향은 운반자 회전이 아니라 끌리는 방향 — 제자리에서 마우스만 돌려도 몸이 같이 돌지 않는다.
        // 쓰러진 몸을 돌리는 것이 여기서는 맞다(끌려가는 그림) — 시야는 카메라 로컬(m_downYaw)이 따로 든다.
        Vector3 dragDirection = anchor - transform.position;
        dragDirection.y = 0f;
        if (dragDirection.sqrMagnitude > 0.0001f)
        {
            Quaternion facing = Quaternion.LookRotation(dragDirection);
            m_dragFacing = Quaternion.Slerp(
                m_dragFacing, facing, 1f - Mathf.Exp(-m_dragTurnSharpness * Time.deltaTime));
        }

        // 끌린 거리에 비례해 좌우로 흔들린다 — 시간이 아니라 거리 기준이라 멈추면 흔들림도 멈춘다
        m_dragTravel += new Vector2(step.x, step.z).magnitude;
        float sway = Mathf.Sin(m_dragTravel * m_dragSwayFrequency) * m_dragSwayAngle;
        transform.rotation = m_dragFacing * Quaternion.Euler(0f, sway, 0f);
    }

    // ---- 공통 ----

    // 공중에서 붙잡히면 공중 상태가 고착돼 끌려가는 내내 낙하 애니메이션이 재생된다 —
    // 추종 중에는 HandleMove를 건너뛰어 접지 보고가 멈추므로 진입 시 한 번 내려준다. (#189)
    private void ReportGroundedOnEnter()
    {
        if (m_jump != null)
        {
            m_jump.ReportGrounded(true);
        }
    }
}
