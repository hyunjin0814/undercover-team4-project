using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 래그돌 — 기능 정지(<see cref="IncapacitationCause.Die"/>) 또는 홈런 진압봉 비행
/// (<see cref="IncapacitationCause.Launched"/>, #815) 동안 애니메이터를 끄고 뼈를 물리에 넘긴 뒤,
/// 착지·정착하면 다시 애니메이터로 되돌린다. (#506) 사망은 부활 키트가 풀고, 비행은 정착 자체가
/// 복구 신호다 — <see cref="Settle"/>이 그 통보를 보낸다.
///
/// <b>이 클래스가 쥔 것은 "누가 위치를 쥐나"다.</b> 뼈를 물리에 넘기고 되돌리는 일 자체는
/// <see cref="RagdollRig"/>가, 밧줄 견인은 <see cref="RagdollRope"/>가 한다 — 둘 다 네트워크·권위를
/// 모르는 순수 물리라 NPC가 그대로 재사용한다. 여기 남은 것은 전부 <b>플레이어 고유</b>다:
/// CharacterController 캡슐을 대리값으로 쓰는 것, 오너 권한 NetworkTransform, 사망 폴링, 기상 블렌드.
///
/// <b>표현 계층 전용이다</b> — 뼈를 동기화하지 않고 모든 피어에서 로컬로 같은 규칙으로 돈다.
/// 그래서 NetworkBehaviour가 아니고, 매니저도 아니라 App 파사드와 무관하다.
///
/// <b>설계 근거·실측·되살리면 안 되는 것들은 <c>docs/player-ragdoll.md</c>에 있다.</b>
/// 이 파일을 고치기 전에 그쪽의 해당 항목을 먼저 볼 것 — 특히 §2(리그가 한 벌로 돌아왔다)와
/// §5(캡슐 추종이 FixedUpdate인 이유).
///
/// 붙이는 곳: <b>프리팹 루트</b>(CharacterController·PlayerIncapacitation과 같은 오브젝트).
/// ⚠ <see cref="RagdollRig"/>는 <b>자식</b>에, <see cref="RagdollPoseStreamer"/>는 <b>루트</b>에 있다.
/// </summary>
public partial class PlayerRagdoll : MonoBehaviour
{
    // 지면을 못 찾아도 결국은 정착시키는 최후 배수 — 맵 밖으로 떨어진 시체가 Ragdoll에 갇히지 않게.
    private const float k_lostBodyTimeoutFactor = 4f;


    // 사망·비행 원인 동기화를 기다려 주는 시간(초) — <b>안전망뿐</b>이다. 정상 경로에서는 걸리지 않는다 (docs §9).
    private const float k_causeSyncGraceSeconds = 1f;

    // 순간이동 뒤 줄을 다시 맬 운반자 거리(m) — 밧줄 길이(약 2m)보다 넉넉히 두되 운반 끊김 거리(8m)
    // 보다는 짧게. 넓게 잡아도 안전하다: 더 멀면 서버가 운반 자체를 정리한다. (#614)
    private const float k_ropeReattachRange = 5f;

    // 부활 블렌드가 물려 들어가는 상태 — PlayerAnimatorControllerBuilder의 k_groundState와 같아야 한다.
    private static readonly int s_groundStateHash = Animator.StringToHash("Knockdown_Ground");

    private enum RagdollState
    {
        Animated, // 평시 — 전 Rigidbody 키네마틱, 애니메이터가 포즈를 쥔다
        // 물리 중. <b>시체도 끝까지 이 상태다</b> — 정착은 상태가 아니라 m_settled 깃발이다 (docs §3).
        Ragdoll,
        BlendingToAnimator, // 정착 포즈 → 애니메이터 포즈 보간 (부활)
    }

    [Header("정착 판정")]
    [Tooltip("정착 판정 타임아웃(초) — 지형에 껴서 영원히 떨리는 경우의 안전장치")]
    [SerializeField] private float m_settleTimeoutSeconds = 5f;

    [Header("정착 후 정렬")]
    [Tooltip("시체 밑 지면을 찾는 레이캐스트 마스크 — 지형(Default). 래그돌 뼈는 다른 레이어라 걸리지 않는다")]
    [SerializeField] private LayerMask m_groundMask = 1;

    [Tooltip("골반 아래로 지면을 찾는 거리(m). 짧게 잡을 것 — 길면 얇은 실내 바닥을 뚫고 아래층 지면을 " +
             "찾아내 시체가 한 층 밑으로 순간이동한다. 못 찾으면 골반 높이를 쓴다")]
    [SerializeField] private float m_groundProbeDistance = 1.5f;

    [Tooltip("루트 yaw를 몸이 누운 방향에 맞춘다 — 기상 모션이 '루트 전방을 향해 누워 있다'를 " +
             "전제하므로. 비행 중에도 매 프레임 맞춘다 — 근거는 docs/player-ragdoll.md §11")]
    [SerializeField] private bool m_alignRootYawToBody = true;

    [Tooltip("루트 yaw 추종 감쇠율(1/초) — 0이면 즉시 대입.\n\n" +
             "⚠ 즉시 대입은 위험하다: 몸이 막 기우는 동안 방향값이 흔들려 루트에 매달린 것들" +
             "(이름표·아이템)이 한 프레임에 통째로 돈다 — docs/player-ragdoll.md §11")]
    [SerializeField] private float m_rootYawFollowSpeed = 8f;

    [Tooltip("몸 방향 대비 루트 yaw 보정(도) — Knockdown_StandUp 클립이 어느 쪽을 머리로 보는지에 " +
             "맞춘다. 아래 m_logRevivalYaw로 실측해 넣는 값이다")]
    [SerializeField] private float m_rootYawOffset;

    [Header("애니메이터 복귀")]
    [Tooltip("정착 포즈 → 애니메이터 포즈 보간 시간(초)")]
    [SerializeField] private float m_blendSeconds = 0.4f;

    private RagdollRig m_rig; // 뼈 한 벌 — 물리 조작 전부를 여기 위임한다
    private bool m_lostBodyHidden; // 회수 불가 몸을 이미 감췄는가 (#819) — 매 프레임 렌더러를 훑지 않으려고
    // HideLostBody가 실제로 끈 렌더러만 담는다 — 되살릴 때 이 목록만 켜야, 평소 꺼져 있는 렌더러
    // (1인칭 팔 리그처럼 뼈 이름이 겹치는 여분 리그)까지 함께 켜는 사고를 피한다.
    private readonly List<Renderer> m_hiddenLostBodyRenderers = new List<Renderer>();
    private RagdollRope m_rope; // 밧줄 견인 (선택 — 없으면 운반이 물리로 안 끌린다)

    // 다시 맬 상대들 — 순간이동이 관절을 끊어도 남는다. 참가자별로 실제 운반이 끝날 때만 빠진다.
    // 여럿이 덧걸 수 있어(합류) 목록이다 (#614·다인 확장).
    private readonly List<Transform> m_ropeCarriers = new List<Transform>();
    private RagdollPoseBlend m_blend; // 부활 블렌드 — 리그가 한 벌이므로 그 리그를 섞는다

    // 전 뼈 자세 스트림 — 이 컴포넌트가 피어로 내보내는 유일한 통로다.
    // NPC와 같은 부품을 그대로 쓰고, 갈리는 것은 <b>권위뿐</b>이다(프리팹에서 Owner로 박는다).
    private RagdollPoseStreamer m_streamer;

    // 첫 패킷이 오기 전까지 붙들 자세를 잡았는가 — <see cref="TickHoldPoseUntilStream"/>.
    private bool m_holdPoseUntilStream;

    private Animator m_animator;
    private CharacterController m_controller;

    // 빔 흡입 중 뼈를 얼려 루트에 붙여 두는 구간인가 + 그 직전 루트 위치 (TickBeamedBodyFollow)
    private bool m_beamedHold;
    private Vector3 m_beamedLastRoot;
    private PlayerIncapacitation m_incapacitation;
    private PlayerMovement m_movement;
    private NetworkObject m_netObject;

    private Transform m_root; // CharacterController가 붙은 트랜스폼 = 판정·동기화의 주체

    private RagdollState m_state = RagdollState.Animated;

    // <b>정착은 상태가 아니라 국면을 적어 둔 깃발이다</b> — 뜻은 "물리가 잠들어 스트림을 끊었다"뿐이고
    // 뼈는 동적으로 남는다. 밟히거나 밀리면 Update의 깨어남 폴링이 스트림을 되살린다 (docs §3·§10).
    private bool m_settled;
    private float m_elapsedInRagdoll;

    // 늦게 접속했는데 대상이 이미 쓰러져(다운·사망) 있거나 비행 중이던 경우 — 이번 래그돌 원인은
    // 건너뛴다 (docs §9). ⚠ 다운→사망을 지나도 계속 참이다(!wantsRagdoll일 때만 내려간다) —
    // 그 피어에서는 유예가 끝나도 몸이 Knockdown_Ground 애니로 남는다. 이미 끝난 낙하를 뒤늦게
    // 재생하지 않는 것이 이 플래그의 뜻이라 의도된 동작이고, 위치·yaw는 루트 NT가 맞춘다.
    private bool m_skipThisEpisode;
    private bool m_polledOnce;

    // 이번 에피소드에서 <b>래그돌 원인(다운·사망·비행)을 한 번이라도 관측했는가</b> — 부활 판정의 전제다 (docs §9).
    // #815로 비행, #865로 다운이 늘어왔다 — 이름을 "원인"으로 고친 것이 그 셋을 함께 담기 위해서다.
    private bool m_sawCauseThisEpisode;
    private float m_awaitingCauseSeconds;

    // 직전 프레임의 물리 권위 — 래그돌 도중 권위가 뒤집히는 경우를 잡는 안전망용. (#865)
    private bool m_hadMoveAuthority;

    // 이번 에피소드에서 <b>한 번이라도 정착했는가</b> — 루트 yaw 추종을 끄는 일방향 래치다. (#865)
    // m_settled로 물으면 안 된다: 잠든 몸이 밟혀 깨어나면 ResumeFromSleep이 그것을 되돌려 추종이
    // 다시 켜지고, 다운은 1인칭이라 <b>그 yaw가 곧 쓰러진 본인의 시야</b>다 — 동료가 몸을 발로 차서
    // 남의 화면을 돌릴 수 있게 된다. 사망은 3인칭 관전이라 없던 문제고 비행은 정착 전이라 의도였다.
    private bool m_yawFollowDone;

    /// <summary>
    /// 래그돌이 애니메이터로부터 포즈를 빼앗고 있는가 — <see cref="PlayerMovement.AddKnockback"/>·
    /// <see cref="PlayerAnimationDriver"/>·<see cref="PlayerHeadLook"/>이 각자 물러나는 판정에 쓴다.
    /// 정착 후에도, 부활 블렌드 중에도 참이다.
    /// </summary>
    public bool IsRagdollActive => m_state != RagdollState.Animated;

    /// <summary>
    /// 캡슐이 시체를 따라가야 하는 구간인가 — <see cref="PlayerMovement.Update"/>가 입력 이동을 접는 판정.
    /// </summary>
    internal bool IsCapsuleFollowingBody =>
        m_state == RagdollState.Ragdoll && (m_incapacitation == null || !m_incapacitation.IsBeamed);

    /// <summary>
    /// 물리가 정착했는가 — <see cref="PlayerIncapacitation.RequestLaunchSettled"/>가 비행(#815) 복구
    /// 판정에 쓴다. <see cref="m_settled"/>는 권위 게이트 뒤에서만 세워지므로 이 값이 참인 피어가
    /// 곧 그 통보를 보낼 오너다.
    /// </summary>
    internal bool IsSettled => m_settled;

    // 이동 권한 — 오너(또는 세션 없는 오프라인 Play)만 루트를 옮길 수 있다.
    private bool HasMoveAuthority =>
        m_netObject == null || !m_netObject.IsSpawned || m_netObject.IsOwner;

    private void Awake()
    {
        // ⚠ 리그는 <b>자식</b>에 있다 — 비활성일 수 있으므로 includeInactive를 반드시 켠다.
        m_rig = GetComponentInChildren<RagdollRig>(true);
        if (m_rig == null)
        {
            Debug.LogWarning(
                $"PlayerRagdoll: RagdollRig를 찾지 못해 사망 래그돌을 끈다 — {name}. "
                    + "Corpse 오브젝트에 RagdollRig가 붙어 있는지 확인할 것",
                this
            );
            enabled = false;
            return;
        }

        // 소유자보다 먼저 돌 수 있다 — 리그 수집은 멱등이라 여기서 보장해도 된다.
        m_rig.EnsureCollected();

        // 밧줄은 리그와 <b>같은 오브젝트</b>에 있다 — RagdollRope가 RagdollRig를 RequireComponent한다.
        m_rope = m_rig.GetComponent<RagdollRope>();

        // 리그가 한 벌이므로 섞는 대상도 그 리그다 — 애니메이터와 물리가 같은 뼈를 번갈아 쥔다.
        m_blend = new RagdollPoseBlend(m_rig.BoneRoot);

        m_animator = GetComponentInChildren<Animator>();
        m_controller = GetComponentInParent<CharacterController>();
        m_incapacitation = GetComponentInParent<PlayerIncapacitation>();
        m_movement = GetComponentInParent<PlayerMovement>();
        m_netObject = GetComponentInParent<NetworkObject>();

        // 판정의 주체는 CharacterController가 붙은 트랜스폼이다 — 이 컴포넌트가 프리팹 어디에 붙어도
        // 같은 것을 가리키게 한다.
        m_root = m_controller != null ? m_controller.transform : transform;

        // 스트리머는 <b>루트</b>에 있다 — NGO가 비활성 GameObject의 NetworkBehaviour를 스폰에서
        // 제외하므로 NetworkObject와 같은 오브젝트여야 한다.
        m_streamer = m_root.GetComponent<RagdollPoseStreamer>();
        if (m_streamer == null)
        {
            Debug.LogWarning(
                $"PlayerRagdoll: RagdollPoseStreamer가 없다 — {name}. 원격 피어에 자세가 가지 않아 "
                    + "시체가 진입 자세로 굳는다. Player 프리팹 루트에 붙일 것",
                this
            );
        }
        else
        {
            // 정착 자세가 도착하면 원격도 그 자리에서 정착으로 넘긴다 — <b>원격의 종착 상태다.</b>
            m_streamer.OnSettledPoseReceived += HandleSettledPoseReceived;
        }

        // 뼈 콜라이더는 평시에도 켜져 있으므로(격리는 Ragdoll 레이어와 충돌 매트릭스가 맡는다)
        // 캡슐 무시를 여기서 바로 건다.
        ReapplyCapsuleIgnore();
    }

    // ---- 래그돌 표현 전환 (리그 한 벌 — NPC와 같은 모양, #763 2단계) ----

    /// <summary>
    /// 리그를 물리에 넘긴다 — <b>사망 시 표현 전환의 전부.</b> 모델을 갈아 끼우지 않는다.
    /// ⚠ 애니메이터를 반드시 먼저 끈다 — 리그가 한 벌이라 켜 둔 채 넘기면 물리 결과를 매 프레임 덮는다.
    /// </summary>
    private void EnterRagdollPose()
    {
        if (m_animator != null)
            m_animator.enabled = false;

        // 컬링으로 사라지지 않게 — 무너진 뼈가 루트에서 멀어져도 그린다(NPC와 같다).
        m_rig.Skins.SetAlwaysVisible(true);

        // ⚠ 물리로 넘기기 <b>전에</b> 열어야 진입 프레임의 자세가 계측에 남는다.
        BeginEntryTrace();

        ReleaseBonesToPhysics();
    }

    /// <summary>
    /// 리그를 애니메이터에게 돌려줄 준비 — 부활·라운드 리셋. <b>애니메이터를 켜는 것은 호출부가 한다</b>
    /// (블렌드 출발점을 잡는 순서 때문 — <see cref="ExitToAnimator"/>).
    /// <c>BindPose.RestoreAll</c>은 자세가 아니라 <b>뼈 길이</b>를 되돌린다 — 유일한 누적 방어다 (docs §8).
    /// </summary>
    private void ExitRagdollPose()
    {
        m_rig.Skins.SetAlwaysVisible(false);
        m_rig.BindPose.RestoreAll();
    }

    /// <summary>
    /// 뼈를 물리로 놓아준다. <b>자세를 스트림으로 받는 피어는 전 뼈 키네마틱</b>이라 물리를 아예
    /// 돌리지 않고, 첫 패킷 전까지 붙들 자세만 잡아 둔다.
    /// 권위 피어는 정착해도 얼리지 않는다 — 그 근거와 앞서 버린 세 방식은 docs §7.
    /// </summary>
    private void ReleaseBonesToPhysics()
    {
        // ⚠ <b>여기가 이관 래치의 갱신 지점이다</b> — "뼈를 지금 권위 기준으로 배선했다"는 사실을
        // 진입·이관 양쪽에서 한 곳에 기록한다. 안 세우면 소유권이 옮겨간 뒤 처음 진입한 몸이
        // 다음 Update의 TickAuthorityHandover에 뒤늦게 걸려 <b>임펄스를 통째로 잃는다</b>
        // (SetKinematic(false)가 속도를 0으로 대입한다). 근거는 docs/506-explosion-ragdoll.md.
        m_hadMoveAuthority = HasMoveAuthority;

        if (m_streamer != null && !HasMoveAuthority)
        {
            m_rig.SetKinematic(true);

            // 첫 패킷이 오기 전까지 붙들 자세를 잡아 둔다 — 그 사이에도 루트는 이미 움직인다.
            m_rig.CapturePose();
            m_holdPoseUntilStream = true;
            return;
        }

        m_rig.SetKinematic(false);

        // ⚠ <b>이미 정착한 몸의 권위를 얻었다면 곧바로 다시 재운다</b> (#957). 이관을 정착까지 미루면서
        // 이 조합이 처음으로 정상 경로가 됐다. 안 재우면 위 SetKinematic(false)가 뼈를 <b>깨어난 채</b>
        // 동적으로 만들고, Update의 깨어남 폴링이 그것을 "밟혀서 깨어났다"로 읽어 ResumeFromSleep이
        // m_settled를 되돌린다 — 멈춰 있던 시체가 다시 흔들렸다 재정착한다.
        //
        // 잃는 것은 없다: 정착했다는 것이 곧 속도가 0이라는 뜻이라, 여기서 재워도 물리 상태가 사라지지
        // 않는다(그것이 이관을 이 시점으로 옮긴 근거이기도 하다 — docs/865-down-ragdoll.md §2-1).
        //
        // ⚠ <b>여기서 m_settled가 이미 참인 근거는 전송 순서다.</b> 옛 오너가 Settle()에서
        // EndStreaming()의 FinalPoseRpc를 먼저 쏘고 그다음 RequestDeathSettled()를 쏘는데, <b>둘 다
        // 신뢰 RPC라 같은 연결에서 순서가 보장된다</b> — 서버는 종착 자세를 받아
        // HandleSettledPoseReceived로 m_settled를 세운 <b>뒤에</b> 이관을 받는다. 스트림 패킷
        // (StreamPoseRpc)만 Unreliable이고 이 마지막 패킷은 아니다.
        if (m_settled)
            m_rig.SleepAll();
    }

    /// <summary>
    /// 원격이 정착 자세를 받았다 — 깃발만 세운다. 자세는 스트리머가 이미 입혔고, 권위 쪽
    /// <see cref="Settle"/>의 나머지는 자기 물리를 정리하는 일이라 여기서 하면 안 된다.
    /// </summary>
    private void HandleSettledPoseReceived()
    {
        if (m_state == RagdollState.Animated)
            return; // 이번 사망을 건너뛴 피어 — 살아있는 몸을 시체 상태로 밀지 않는다

        m_holdPoseUntilStream = false;
        m_settled = true;
        DumpSettleTrace();
    }

    private void OnDestroy()
    {
        if (m_streamer != null)
            m_streamer.OnSettledPoseReceived -= HandleSettledPoseReceived;
    }

    // ---- 캡슐(대리값) 다루기 — 여기부터가 플레이어 고유다 ----

    /// <summary>
    /// 이 몸의 뼈가 <paramref name="others"/>와 <b>충돌하지 않게</b> 한다 — 전 피어가 각자 부른다.
    ///
    /// 쓰는 곳은 <b>치인 차</b>다(<c>TrafficVehicle</c>). 차 몸통 MeshCollider는 Default 레이어이고
    /// Ragdoll×Default 충돌이 켜져 있어서, <b>정면으로 치이면 몸이 범퍼 앞에 갇힌다</b> — 임펄스로
    /// 14~25 m/s를 줘도 차가 22 m/s로 따라붙어 매 스텝 다시 부딪히므로 날아가는 대신 끌려간다
    /// (옆면에 맞으면 메시를 비껴가 잘 날아가던 것이 이 차이였다). 근거는 docs/903-instant-death.md.
    ///
    /// ⚠ <b>되돌릴 필요가 없다</b> — 차는 풀로 반납될 때 <c>SetActive(false)</c>되고, 콜라이더를
    /// 껐다 켜면 이 상태가 초기화된다(Unity 사양 — <see cref="ReapplyCapsuleIgnore"/>가 존재하는
    /// 이유이기도 하다). 그 관례가 깨지면 여기도 함께 새므로, 풀 반납이 비활성화를 그만두면
    /// 이 주석을 다시 볼 것.
    /// </summary>
    public void IgnoreCollisionWith(Collider[] others, bool ignore)
    {
        if (others == null || m_rig == null || !m_rig.IsValid)
            return;

        for (int i = 0; i < others.Length; i++)
            m_rig.IgnoreCollisionWith(others[i], ignore);
    }

    // 자기 CharacterController 캡슐과의 충돌을 끈다 — 죽는 순간 래그돌은 자기 캡슐 <b>안에서</b>
    // 출발하므로 그대로 두면 겹침 탈출에 몸이 튄다.
    //
    // ⚠ 콜라이더를 껐다 켜면 이 상태가 초기화된다(Unity 사양). 그래서 재적용을 경로마다 흩지 않고
    // <b>캡슐을 켜는 통로 하나</b>에 걸었다 — PlayerMovement.SetCapsuleEnabled가 켜는 순간 부른다.
    // 근거와 옛 폴러가 놓친 구멍은 docs/player-ragdoll.md §6.
    internal void ReapplyCapsuleIgnore()
    {
        if (m_controller == null)
            return;

        m_rig.IgnoreCollisionWith(m_controller, true);
    }

    // 캡슐을 켜고 끈다 — <b>쓰기는 PlayerMovement에 맡긴다</b>(캡슐의 주인이 저쪽이고, 켜는 순간의
    // 무시 재적용도 저쪽 통로가 책임진다). 직접 쓰는 것은 배선이 없는 구성뿐이다.
    private void SetControllerEnabled(bool value)
    {
        if (m_movement != null)
        {
            m_movement.SetControllerEnabled(value);
            return;
        }

        if (m_controller == null)
            return;

        m_controller.enabled = value;
        if (value)
            ReapplyCapsuleIgnore();
    }

    // ---- 밧줄 파사드 (#365 운반 / #398 드래그) ----
    //
    // 실물은 RagdollRope가 쥔다. 파사드를 두는 이유는 호출부(PlayerTowedMotion)가 "래그돌인 대상에게
    // 밧줄을 묶는다"를 표현하기 때문이다 — 직접 찾게 하면 조건이 호출부로 새어 나간다. (docs §12)

    /// <summary>관절 밧줄의 길이(m) — 리그가 아직 안 잡혔으면 0. 묻는 쪽이 대신 쓸 값을 정한다. (#644)</summary>
    public float RopeLength => m_rope != null ? m_rope.Length : 0f;

    /// <summary>
    /// 밧줄을 시체에 <b>한 가닥</b> 묶는다 — <see cref="PlayerTowedMotion.BeginDraggedFollow"/>가
    /// 래그돌인 대상에게, 참가자(합류)마다 부른다. <b>권위 피어만 묶는다</b> — 전 피어가 각자 묶던
    /// 옛 배선이 견인 발산의 근원이었다 (docs §12).
    /// </summary>
    /// <param name="carrier">운반자(밧줄을 쥔 쪽) — 이미 목록에 있으면 멱등.</param>
    public void BeginRopePull(Transform carrier)
    {
        // ⚠ 권위 가드보다 <b>앞</b>에 기억한다 — 이 호출은 전 피어에 오지만(운반 RPC가 SendTo.Everyone),
        // 사망 중 소유권이 넘어가면 지금 권위가 아닌 피어가 나중에 권위가 될 수 있다. (#614)
        if (carrier != null && !m_ropeCarriers.Contains(carrier))
            m_ropeCarriers.Add(carrier);

        if (!HasMoveAuthority)
            return;

        // ⚠ 구조 채널링 중 <b>붙들기는 여기가 아니다</b> — 몸을 깨우는 것은 밟은 PhysX이고 이 함수는
        // 그 경로에 없다. 붙들기는 <see cref="Update"/>의 깨어남 폴링에 있다. 근거는
        // docs/865-down-ragdoll.md §3-2. (Down도 끌 수 있게 되면 끌기가 구조를 취소한다 — 같은 절)

        // 깨우기만 한다 — 스트림 재개는 <see cref="Update"/>의 깨어남 폴링이 받는다.
        // (NpcRagdoll.WakeCorpse와 같은 모양. 재개 경로를 하나로 모으는 것이 요점이다)
        m_rig.WakeAll();
        if (m_settled)
            ResumeFromSleep();

        m_rope?.Attach(carrier);
    }

    /// <summary>이 참가자의 가닥만 푼다 — 줄다리기에서 한 명이 손을 뗄 때. 없으면 무동작(멱등).
    /// <b>다시 맬 상대에서도 뺀다</b> — <see cref="PlaceBodyBy"/>가 남기는 것과 갈리는 지점이 여기다. (#614)</summary>
    public void EndRopePull(Transform carrier)
    {
        m_ropeCarriers.Remove(carrier);
        m_rope?.Detach(carrier);
    }

    // 회수 불가로 확정된 몸을 화면에서 지운다 (#819). 전 피어가 각자 부르는 자리다 —
    // IsBodyLost가 복제되므로 서버 지시 없이도 같은 결과가 난다.
    // <see cref="ShowLostBody"/>와 대칭 — 같은 오브젝트가 라운드를 넘어 재사용되므로
    // (PlayerHealth.ServerResetState가 HP만 초기화하고 despawn하지 않는다) 되돌리지 않으면
    // 다음 라운드부터 이 플레이어가 영구히 투명해진다.
    private void HideLostBody()
    {
        if (m_lostBodyHidden)
            return;

        m_lostBodyHidden = true;
        foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer.enabled)
                continue;

            renderer.enabled = false;
            m_hiddenLostBodyRenderers.Add(renderer);
        }
    }

    // HideLostBody가 끈 렌더러만 되살린다 — IsBodyLost가 풀리는 유일한 경로(PlayerIncapacitation
    // .SetBodyLost(false))는 부활 RPC 안에서 돌고, 그 직후 PollRagdollCause가 ExitToAnimator를 부른다.
    private void ShowLostBody()
    {
        if (!m_lostBodyHidden)
            return;

        m_lostBodyHidden = false;
        foreach (Renderer renderer in m_hiddenLostBodyRenderers)
        {
            if (renderer != null)
                renderer.enabled = true;
        }
        m_hiddenLostBodyRenderers.Clear();
    }

    /// <summary>밧줄을 전부 푼다 — 내려놓기·부활·운반자 전원 소실. <b>다시 맬 상대도 전부 잊는다</b>
    /// — 순간이동이 잠깐 끊는 것(<see cref="PlaceBodyBy"/>)과 갈리는 지점이 여기다. (#614)</summary>
    public void EndRopePull()
    {
        m_ropeCarriers.Clear();
        m_rope?.Detach();
    }

    /// <summary>
    /// 순간이동이 끊어 둔 줄을 <b>참가자가 실제로 가까워지면</b> 각자 다시 맨다 — 권위 피어 전용. (#614)
    ///
    /// <b>왜 바로 못 매는가.</b> 운반자와 이 몸은 <b>오너가 서로 다른 피어</b>라 두 순간이동이 각자
    /// 도착한다 — 이 피어가 옮겨진 직후에는 운반자가 아직 <b>옛 자리</b>로 보인다. 그때 매면 앵커가
    /// 거기 생기고 다음 물리 스텝에 그 거리만큼 위반이 터진다(<see cref="PlaceBodyBy"/>가 줄을 끊고
    /// 가는 이유와 같은 사고).
    ///
    /// <b>참가자마다 따로 판정한다</b> — <c>m_rope.IsAttached</c>("한 가닥이라도")로 한 번만 보면
    /// 다인 운반에서 A가 먼저 붙는 순간 B의 재부착이 영영 막힌다. 각자 자기 가닥이 이미 붙었는지
    /// (<see cref="RagdollRope.IsAttachedTo"/>)와 자기 거리만 본다.
    ///
    /// 영영 안 매인 채 남지 않는 근거는 서버에 있다 — 그만큼 멀면 <see cref="PlayerCarrier"/>의 거리
    /// 검사(또는 목줄 완화식)가 유예가 끝난 뒤 그 참가자의 운반을 정리한다.
    /// </summary>
    private void TickRopeReattach()
    {
        if (m_rope == null || m_ropeCarriers.Count == 0)
            return;

        // 이 프레임에 다시 매는 참가자가 있을 수 있어 스냅샷을 돈다 — BeginRopePull이 같은 목록을
        // 다시 건드리지는 않지만(이미 들어 있으면 멱등), 방어적으로 인덱스 역순 순회를 쓴다.
        for (int i = m_ropeCarriers.Count - 1; i >= 0; i--)
        {
            Transform carrier = m_ropeCarriers[i];
            if (carrier == null || m_rope.IsAttachedTo(carrier))
                continue;

            Vector3 delta = carrier.position - m_root.position;
            if (delta.sqrMagnitude > k_ropeReattachRange * k_ropeReattachRange)
                continue;

            BeginRopePull(carrier);
        }
    }

    // ---- 순간이동 (#614) ----

    /// <summary>
    /// 순간이동한 루트에 뼈를 <b>같은 델타로</b> 따라 옮긴다 — 오너 전용. 래그돌이 아니면 무동작.
    /// 부르는 곳은 <see cref="PlayerMovement.SetPose"/> 하나다.
    ///
    /// <b>루트는 건드리지 않는다</b> — 저쪽이 이미 옮겼다. 여기서 또 옮기면 델타가 두 번 실린다.
    /// 뼈는 동적 리지드바디라 루트를 따라오지 않으므로(계층이 아니라 물리가 자리를 쥔다) 이 보정이
    /// 없으면 <see cref="TickCapsuleFollow"/>가 다음 물리 스텝에 루트를 <b>도로 시체 자리로</b> 끌어간다.
    ///
    /// NPC의 <c>NpcRagdoll.ServerPlaceCorpse</c>와 같은 일을 하지만 <b>도는 피어가 반대다</b> —
    /// 저쪽은 서버, 이쪽은 그 몸의 오너다(루트 NetworkTransform·자세 스트림이 둘 다 오너 권한).
    /// </summary>
    /// <param name="delta">루트가 옮겨 간 거리 — 옮기기 <b>전에</b> 재야 한다.</param>
    public void PlaceBodyBy(Vector3 delta)
    {
        if (!HasMoveAuthority || m_state != RagdollState.Ragdoll)
            return;
        if (m_rig == null || !m_rig.IsValid)
            return;

        // ⚠ <b>줄을 먼저 끊는다.</b> 관절의 앵커는 운반자를 따라가는 별개 오브젝트라 이 델타로 같이
        // 움직이지 않는다 — 매인 채 옮기면 위반이 그 거리만큼 생기고 솔버가 그것을 메우며 몸을
        // <b>발사한다</b>(NPC 실측 237 m/s). 다시 매는 것은 운반자가 가까워진 뒤다
        // (<see cref="TickRopeReattach"/>) — 여기서 바로 매면 아직 옛 자리에 있는 운반자에게 걸린다.
        //
        // ⚠ <see cref="EndRopePull()"/>이 아니라 관절만 끊는다 — 저쪽은 "운반이 끝났다"라 다시 맬
        // 상대까지 잊는다. 여기서는 운반이 계속되는 중이므로 <see cref="m_ropeCarriers"/>를 남긴다.
        m_rope?.Detach();

        // 전 뼈를 한 델타로 — 상대 자세·속도·관절이 보존돼 도착지에서 솔버가 메울 것이 없다.
        m_rig.TranslateBy(delta);

        // ⚠ <b>보간 없이 쏜다 — 안 쏘면 원격의 몸이 출발지에 남는다.</b> 평범한 스냅샷으로 보내면
        // 원격이 두 지점 사이를 보간하며 몸이 맵을 가로질러 날아간다. 이 패킷에는 뼈 길이도 실려
        // 원격이 바인드 골격으로 그리는 것도 함께 막는다.
        m_streamer?.SendTeleportPose();

        // 옮긴 몸은 깨어난 것으로 본다 — 도착지에서 다시 무너져 잠드는 과정이 원격에도 흘러야 한다.
        m_rig.WakeAll();
        if (m_settled)
            ResumeFromSleep();
    }

    // ---- 진입 / 이탈 ----

    /// <summary>
    /// 래그돌 진입 — <b>멱등이다.</b> 이미 물리 중이면 임펄스만 누적하고, 기상 블렌드 중이면
    /// <b>지금 사유가 래그돌 사유일 때만</b> 새로 진입한다(늦게 도착한 옛 RPC를 거르기 위함).
    /// 원격의 도착 순서가 보장되지 않기 때문이다 — 반대 순서는 <see cref="PollRagdollCause"/>가 막는다 (docs §8·§9).
    /// </summary>
    /// <param name="impulse">폭심에서 밀려나는 속도(m/s). 힘없이 무너지는 사망은 <see cref="Vector3.zero"/>.</param>
    public void EnterRagdoll(Vector3 impulse)
    {
        if (m_rig == null || !m_rig.IsValid)
            return;

        if (m_state == RagdollState.Ragdoll)
        {
            // ⚠ <b>뼈가 키네마틱이면 임펄스가 통째로 버려진다</b> — RagdollRig.ApplyImpulse가 키네마틱
            // 바디를 건너뛴다(#768이 정착한 시체에서 겪은 것과 같다). <b>비행 중 차·폭발에 맞으면
            // 정확히 그 상태다</b>: 직전에 ApplyDeathOwnership이 소유권을 이 피어로 옮겨 놓았는데
            // 뼈는 아직 원격 시절의 키네마틱이고, 그것을 푸는 TickAuthorityHandover는 <b>다음
            // Update</b>다. 그때는 이미 늦다 — SetKinematic 전이가 속도까지 지운다.
            //
            // ⚠ <b>조건 없이 부르면 안 된다.</b> 이미 물리로 날고 있는 몸에 부르면 SetKinematic(false)의
            // 속도 0 대입이 먼저 걸려, "누적"이어야 할 늦은 임펄스가 "대체"가 된다.
            // 근거는 docs/506-explosion-ragdoll.md §15.
            if (HasMoveAuthority && m_rig.AnyKinematic)
                ReleaseBonesToPhysics();

            m_rig.ApplyImpulse(impulse); // 늦게 도착한 폭발 정보 — 누적한다
            return;
        }

        // 여기 오는 것은 <b>기상 블렌드 중</b>(BlendingToAnimator)뿐이다 — 정착한 시체는 상태가
        // 여전히 Ragdoll이라 위 갈래가 받는다(정착은 상태가 아니라 깃발이다).
        //
        // ⚠ <b>블렌드 중이어도 "새로 쓰러진 것"이면 받는다.</b> 예전에는 무조건 물러났는데, 그러면
        // 폭발에 날아갔다 <b>일어나는 중에</b> 차에 치인 사람이 임펄스를 통째로 잃고 제자리에서
        // 죽는다(실측 — 그 자리엔 로그도 안 남아 원인이 안 보였다). 그 가드가 막으려던 것은
        // "도로에 누운 시체를 지나가는 차마다 다시 날리는 것"인데, 그쪽은 피해원의 시체 가드가
        // 이미 막는다(TrafficVehicle의 CurrentHp<=0 · ServerHitNpc의 wasAlive).
        //
        // 판정을 <b>동기화된 사유</b>로 하는 것이 요점이다 — 늦게 도착한 옛 임펄스 RPC는 그 사이
        // 부활해 사유가 None이 되어 있으므로 그대로 걸러진다(이 가드의 원래 목적인 도착 순서 방어).
        if (m_state != RagdollState.Animated
            && (m_incapacitation == null || !m_incapacitation.IsRagdollCause))
        {
            return;
        }

        m_state = RagdollState.Ragdoll;
        m_settled = false;
        m_yawFollowDone = false; // 새 에피소드 — 몸이 기울면 다시 따라간다
        m_elapsedInRagdoll = 0f;

        // 슬라이드 넉백과 이중으로 밀리지 않게 CharacterController 쪽 외력을 지운다.
        m_movement?.ClearExternalVelocity();

        // <b>사망 중에는 캡슐을 끈다</b> — 뼈를 물리에 넘기기 <b>전에</b>. 순서를 뒤집으면 뼈가 캡슐
        // 안에서 겹친 채 한 프레임을 보내고 그 탈출 임펄스에 몸이 튄다. (docs §8)
        SetControllerEnabled(false);

        EnterRagdollPose();
        m_rig.ApplyImpulse(impulse);

        // 자세를 흘려보내기 시작한다 — 권위가 아니면 스스로 무동작이다(그쪽 주석).
        m_streamer?.BeginStreaming();
    }

    /// <summary>
    /// 애니메이터로 되돌린다.
    /// <paramref name="blend"/>가 참이면 정착 포즈에서 기상 자세로 보간하고(본부 부활),
    /// 거짓이면 즉시 되돌린다(라운드 리셋·씬 전환·despawn).
    ///
    /// ⚠ <b>순서가 그대로 결과를 바꾼다</b> — 특히 블렌드 출발점은 뼈 길이 복원보다 먼저다. docs §8.
    ///
    /// ⚠ <b>바깥에서 부르지 않는다.</b> 부활은 <see cref="PollRagdollCause"/>가 동기화된 사유를 보고
    /// 결정한다 — "죽음을 본 뒤에만 부활이 성립한다"(<c>ragdoll.md</c> 불변식 5)를 지키는 유일한 문이다.
    /// </summary>
    private void ExitToAnimator(bool blend)
    {
        if (m_state == RagdollState.Animated || m_rig == null || !m_rig.IsValid)
            return;

        // 회수 불가로 감춰졌던 몸이라면 애니메이터로 돌아가는 이 시점에 되살린다 — HideLostBody 참고.
        ShowLostBody();

        // 에피소드가 여기서 끝난다 — 다음 래그돌은 자기 원인을 다시 관측해야 부활할 수 있다 (PollRagdollCause).
        m_settled = false;
        m_yawFollowDone = false;
        m_sawCauseThisEpisode = false;
        m_awaitingCauseSeconds = 0f;

        // 스트림을 <b>아무것도 보내지 않고</b> 끊는다 — 기상에는 종착 자세가 없다. 전 피어가 각자
        // 부르므로 RPC가 필요 없다. 안 끊으면 스트림이 기상 블렌드를 매 프레임 덮어쓴다.
        m_streamer?.StopStreaming();
        m_holdPoseUntilStream = false;

        // 뼈는 전부 물리에 있다 — 애니메이터로 돌아가려면 전부 멈춰야 한다.
        // 밧줄을 먼저 끊는다: 순서를 뒤집으면 한 스텝 동안 스프링이 블렌드 시작 포즈를 당긴다.
        EndRopePull();

        // 시체가 실제로 누운 방향 — 얼리기 <b>전에</b> 잰다. 아래 yaw 진단이 쓴다.
        bool haveCorpseYaw = m_rig.TryGetBodyYaw(out float corpseYaw);

        m_rig.SetKinematic(true);

        // ⚠ <b>블렌드 출발점은 지금 이 래그돌 자세다 — 뼈 길이 복원보다 반드시 먼저 잡는다.</b>
        // 뒤에 잡으면 ExitRagdollPose의 BindPose.RestoreAll이 누운 자세를 지워 몸이 툭 선다.
        bool blending =
            blend
            && m_animator != null
            && m_blend != null
            && m_blend.IsValid
            && m_state != RagdollState.BlendingToAnimator;

        if (blending)
            m_blend.Begin();

        ExitRagdollPose();

        // 캡슐을 되살린다. 이 시점의 루트는 캡슐 추종이 매 프레임 지면 위에 놓아 둔 자리라 그대로
        // 켜면 된다 — 그쪽이 <see cref="CapsuleBottomOffset"/>까지 보정한다.
        SetControllerEnabled(true);
        m_movement?.ClearExternalVelocity(); // 꺼져 있던 동안 쌓인 값이 도착지에서 바닥을 파고들지 않게 (#189)

        // <b>애니메이터를 여기서 켠다</b> — 진입에서 껐고(EnterRagdollPose), 아래 Update(0f)가 클립을
        // 평가하려면 켜져 있어야 한다.
        if (m_animator != null)
            m_animator.enabled = true;

        if (!blending)
        {
            m_state = RagdollState.Animated;
            return;
        }

        // Down은 아직 참이므로 바닥 대기 자세로 물려 들어가야 정착 포즈와의 거리가 가장 짧다.
        // 쓰기 전 값을 들고 있어야 애니메이터가 정말 뼈를 썼는지 판정할 수 있다(진단).
        bool haveLiveBefore = TryLiveBodyYaw(out float liveBefore);

        m_animator.Play(s_groundStateHash, 0, 0f);
        m_animator.Update(0f); // 이번 프레임 LateUpdate에서 바로 섞으려면 포즈가 이미 평가돼 있어야 한다

        // 여기가 재는 자리다 — 클립이 평가된 직후이고 블렌드가 아직 섞기 전이다.
        if (m_logRevivalYaw)
            LogRevivalYaw(haveCorpseYaw, corpseYaw, haveLiveBefore, liveBefore);

        m_state = RagdollState.BlendingToAnimator;
    }

    /// <summary>
    /// 씬 진입 재배치를 위해 즉시 일으킨다 — <see cref="PlayerMovement"/>의 재배치 경로 전용. (#656)
    /// <see cref="ExitToAnimator"/>만으로는 부족해 <see cref="m_skipThisEpisode"/>를 함께 세운다 (docs §8).
    /// </summary>
    public void ExitForReposition()
    {
        ExitToAnimator(blend: false);
        m_skipThisEpisode = true;
    }

    // ---- 매 프레임 ----

    /// <summary>
    /// 캡슐 추종은 <b>물리 스텝에 묶는다 — 프레임이 아니다.</b> (#759)
    /// 프레임마다 돌면 트랜스폼 주입이 스텝당 1회를 넘어 솔버가 관절 오차를 못 따라잡는다
    /// (100fps 호스트에서 발산 — 이것이 슬로모션의 원인이었다).
    /// ⚠ <b>PlayerMovement가 아니라 여기서 돈다</b> — 사망 중 소유권이 넘어가도 권위 피어가 돌게.
    /// 근거는 docs/player-ragdoll.md §5.
    /// </summary>
    private void FixedUpdate()
    {
        if (m_state != RagdollState.Ragdoll || !HasMoveAuthority)
        {
            ReleaseBeamedHold();
            return;
        }

        // 빔에 끌려 올라가는 동안은 <b>주종이 뒤집힌다</b> — 캡슐이 몸을 따라가는 것이 아니라
        // 몸이 캡슐을 따라간다. 근거는 아래 함수와 docs/506-explosion-ragdoll.md §14.
        if (m_incapacitation != null && m_incapacitation.IsBeamed)
        {
            TickBeamedBodyFollow();
            return;
        }

        ReleaseBeamedHold();
        TickCapsuleFollow();
    }

    /// <summary>
    /// 빔 흡입 중 <b>몸을 루트에 붙여 함께 올린다</b> — 권위 피어 전용. (UFO 흡입 · #819)
    ///
    /// <b>왜 반대인가.</b> 다른 래그돌 사유는 물리가 몸의 자리를 쥐고 캡슐이 그것을 따라간다
    /// (<see cref="TickCapsuleFollow"/>). 빔은 <b>남이 몸을 옮기는</b> 유일한 사유라, 끌어올리는
    /// 주체가 <c>PlayerTowedMotion</c>의 호송 추종(캡슐)이다. 그래서 이 구간만
    /// <see cref="IsCapsuleFollowingBody"/>가 거짓이라야 <c>PlayerMovement.Update</c>가 래그돌 분기에서
    /// 멈추지 않고 호송 분기까지 내려간다 — <b>그 분기가 안 돌면 몸이 아예 안 올라간다.</b>
    ///
    /// ⚠ 뼈를 <b>키네마틱으로 얼린다</b>. 동적인 채로 두면 중력이 매 스텝 끌어내려 올라가는 루트와
    /// 싸우고, 관절이 늘어나며 몸이 뒤집힌다. 얼리면 빔에 걸린 순간의 <b>축 늘어진 자세</b>가 그대로
    /// 떠오른다 — 이 기능이 원한 그림이다.
    /// </summary>
    private void TickBeamedBodyFollow()
    {
        if (m_rig == null || !m_rig.IsValid || m_root == null)
            return;

        if (!m_beamedHold)
        {
            m_beamedHold = true;

            // ⚠ 매달린 줄은 끊는다 — 관절 앵커는 이 델타로 같이 안 움직여서, 매인 채 올리면 위반이
            // 쌓이고 솔버가 몸을 쏜다 (<see cref="PlaceBodyBy"/>가 같은 이유로 같은 일을 한다).
            m_rope?.Detach();
            m_rig.SetKinematic(true);
            m_beamedLastRoot = m_root.position;
        }

        Vector3 delta = m_root.position - m_beamedLastRoot;
        m_beamedLastRoot = m_root.position;

        if (delta != Vector3.zero)
            m_rig.TranslateBy(delta); // 전 뼈를 한 델타로 — 상대 자세가 보존된다
    }

    // 빔이 끝났다(놓아줌·삼킴·다른 사유로 전이) — 얼려 둔 뼈를 물리에 돌려준다. 멱등.
    //
    // ⚠ <b>래치는 항상 내리되, 물리로 돌려주는 것은 아직 래그돌일 때만이다.</b> 이 함수는 빔만 끝난
    // 경우와 <b>래그돌 자체를 빠져나간</b> 경우 양쪽에서 불리는데, 후자는 ExitToAnimator가 기상
    // 블렌드를 넘기려고 방금 일부러 얼린 것이라 여기서 풀면 블렌드 중인 뼈가 다시 물리로 열린다.
    // 근거는 docs/506-explosion-ragdoll.md §14-5.
    private void ReleaseBeamedHold()
    {
        if (!m_beamedHold)
            return;

        m_beamedHold = false;

        if (m_state != RagdollState.Ragdoll)
            return;

        if (m_rig != null && m_rig.IsValid)
            m_rig.SetKinematic(false);
    }

    private void Update()
    {
        // 진입 추적의 기준선을 담는다 — Update 시작이 직전 프레임의 최종 자세를 읽는 유일한 지점이다.
        SampleEntryBaseline();

        PollRagdollCause();

        // 래그돌이 돌아가는 중에 물리 권위가 바뀐 경우를 받는다 — 안전망 (아래 주석).
        TickAuthorityHandover();

        if (m_state != RagdollState.Ragdoll)
            return;

        // 회수 불가로 확정된 몸은 권위와 무관하게 전 피어가 각자 즉시 재우고 감춘다 — 원격은
        // HasMoveAuthority 게이트에 걸려 이 자리에 못 오면 몸이 계속 남아 보인다. (#775/#819)
        if (m_incapacitation != null && m_incapacitation.IsBodyLost)
        {
            m_rig.SleepAll();
            HideLostBody();
            Settle();
            return;
        }

        // ⚠ <b>정착 판정은 권위만 돌린다.</b> 원격의 뼈는 키네마틱이라 속도가 항상 0이고, 이 게이트가
        // 없으면 무너지기도 전에 정착해 버린다. 원격의 종착 상태는 받는 정착 자세가 준다. (docs §10)
        if (!HasMoveAuthority)
            return;

        // 빔에 끌려 올라가는 동안은 정착·수면·재부착을 통째로 건너뛴다 — 뼈가 키네마틱이라 속도가
        // 항상 0이라, 두면 <b>공중에서 정착으로 굳고</b> 그 뒤 기상 모션이 상공에서 나간다.
        // 몸을 옮기는 것은 FixedUpdate의 TickBeamedBodyFollow 하나다.
        if (m_incapacitation != null && m_incapacitation.IsBeamed)
            return;

        // ⚠ <b>정착 게이트보다 앞이다</b> — 순간이동 뒤 줄이 끊긴 채 잠든 몸도 다시 매여야 하고,
        // 매는 순간 BeginRopePull이 깨우기까지 한다. 뒤로 내리면 잠든 몸은 영영 안 매인다.
        TickRopeReattach();

        // 잠든 뒤에는 <b>깨어났는지만</b> 본다 — 밟히거나 밀리면 PhysX가 스스로 깨우므로 이 한 줄이
        // 그 모든 경로를 받는다. 이것이 없던 것이 #763의 뿌리다. (docs §10)
        if (m_settled)
        {
            if (!m_rig.AllAsleep)
            {
                // 구조 채널링 중이면 <b>깨우지 않고 도로 재운다</b> — 밟혀 밀리면 게이지를 다 채운 뒤
                // "범위를 벗어남"으로 실패한다. 스트림도 되살리지 않는 것이 의도다.
                // (#865 · docs/865-down-ragdoll.md §3-2)
                if (m_incapacitation != null && m_incapacitation.IsBeingRevived)
                    m_rig.SleepAll();
                else
                    ResumeFromSleep();
            }

            return;
        }

        // 끌리는 동안에는 재우지 않는다 — 놓는 순간부터 다시 센다. (NpcRagdoll과 같은 자리)
        if (m_rope != null && m_rope.IsBeingCarried)
        {
            m_elapsedInRagdoll = 0f;
            return;
        }

        m_elapsedInRagdoll += Time.deltaTime;

        // <b>정착은 물리가 정한다</b> — 전 뼈가 하나도 안 남고 잠들어야 참이다. 옛 평균속도 판정이
        // "흔들거리다 갑자기 굳는" 어색함의 정체였다. (docs §10)
        if (m_rig.AllAsleep)
        {
            Settle();
            return;
        }

        if (m_elapsedInRagdoll < m_settleTimeoutSeconds)
            return;

        // 아직 공중이다 — 여기서 재우면 <b>떠 있는 시체</b>가 된다. 다만 맵 밖으로 떨어진 몸이
        // 영원히 갇히지 않게 무한정 기다리지는 않는다.
        if (!HasGroundUnderHips()
            && m_elapsedInRagdoll < m_settleTimeoutSeconds * k_lostBodyTimeoutFactor)
            return;

        // 타임아웃 — 지형에 물려 스스로 못 잠드는 몸이다. 대신 재운다.
        // ⚠ 키네마틱 얼림이 아니라 <b>물리 수면</b>이라, 밟거나 밧줄을 걸면 깨어남 폴링이 그대로 받는다.
        m_rig.SleepAll();
        Settle();
    }


    // 골반 밑에 지면이 있는가 — 정착 자격과 정착 정렬이 <b>같은 탐색</b>을 써야 모순이 안 생긴다.
    private bool HasGroundUnderHips() => TryGroundUnder(m_rig.Hips.position, out _);

    // 래그돌 진입 사유(다운·사망·비행)를 폴링한다 — 이벤트로는 잡을 수 없다(원인만 바뀌면 안 울린다).
    // 사유는 셋이지만 <see cref="PlayerIncapacitation.Cause"/>는 동시에 하나만 참일 수 있어 겹치지 않는다.
    // NPC의 <see cref="NpcRagdoll.WantsRagdoll"/>과 같은 자리다 — #815로 둘, #865로 셋이 됐다.
    //
    // ⚠ <b>다운→사망은 사유가 바뀌는데 래그돌은 이어진다</b> — 셋 중 유일한 "래그돌 중 원인 전이"이고,
    // 그래서 그 전이에 소유권(=물리 권위)이 움직이지 않아야 한다. 근거는 docs/865-down-ragdoll.md.
    // 부활은 <b>원인을 본 뒤에만</b> 성립한다 — 그 인과 가드의 근거는 docs/player-ragdoll.md §9.
    private void PollRagdollCause()
    {
        if (m_incapacitation == null)
            return;

        // 진입 사유는 넷이다 (#506 Die → #815 Launched → #865 Down → 빔 흡입). 사유를 나열하지 않고
        // IsRagdollCause 하나를 쓰는 이유는 <b>조준 히트박스와 술어를 하나로 묶기 위해서</b>다 —
        // IsAimTargetable이 같은 술어를 보므로, 뼈가 물리로 넘어가는 순간과 히트박스가 켜지는 순간이
        // 같은 값을 본다. 갈라지면 "래그돌인데 조준이 안 잡히는" 방향으로 #857이 되살아난다.
        bool wantsRagdoll = m_incapacitation.IsRagdollCause;

        // 접속 직후 이미 사망·비행 중이었다면 이번 원인은 건너뛴다 — 낙하는 이미 끝난 과거다.
        if (!m_polledOnce)
        {
            m_polledOnce = true;
            m_skipThisEpisode = wantsRagdoll;
        }

        if (wantsRagdoll)
        {
            m_sawCauseThisEpisode = true;
            m_awaitingCauseSeconds = 0f;
        }
        else if (IsRagdollActive && !m_sawCauseThisEpisode)
        {
            m_awaitingCauseSeconds += Time.deltaTime;
        }

        if (!wantsRagdoll)
        {
            m_skipThisEpisode = false;
            bool revivalIsReal =
                m_sawCauseThisEpisode || m_awaitingCauseSeconds >= k_causeSyncGraceSeconds;
            if (m_state == RagdollState.Ragdoll && revivalIsReal)
            {
                ExitToAnimator(blend: true); // 부활 — 정착 포즈에서 기상으로 잇는다
            }
            return;
        }

        if (!m_skipThisEpisode && m_state == RagdollState.Animated)
            EnterRagdoll(Vector3.zero); // 힘없이 무너지는 사망(진압봉·납치)·비행 진입. 임펄스는 각자 RPC로 따로 온다
    }

    private void LateUpdate()
    {
        // 블렌드는 LateUpdate에서 돈다 — 이 시점의 뼈 로컬값이 곧 애니메이터가 평가한 포즈다.
        // 완료되면 Animated로 돌아가고, 그때서야 AnimationDriver가 Down을 내려 기상 모션이 시작된다.
        if (m_state == RagdollState.BlendingToAnimator && m_blend.Tick(m_blendSeconds))
            m_state = RagdollState.Animated;

        // 스트림이 아직 몸을 쥐기 전이면 진입 시점의 자세를 붙든다 — <b>루트에 끌려가지 않게.</b>
        TickHoldPoseUntilStream();

        // 붙들기까지 끝난 뒤에 담는다 — 여기가 렌더 직전이라 화면에 보이는 값과 같다.
        TickSettleTrace();

        // ⚠ <b>LateUpdate여야 한다</b> — PlayerHeadLook이 시선을 얻는 시점이 여기다.
        TickEntryTrace();
    }

    /// <summary>
    /// 원격에서 <b>첫 자세 패킷이 오기 전</b> 구간을 메운다 — 진입 시점의 월드 자세를 붙든다.
    /// 안 붙들면 키네마틱 뼈가 이미 움직이기 시작한 루트를 계층으로 따라가 몸이 통째로 딸려 간다.
    ///
    /// ⚠ <c>IsStreamDriven</c>의 반대로 묻지 않는다 — 그것이 거짓인 경우가 "아직 안 왔다"와 "정착까지
    /// 다 받고 끝났다" 둘인데 뜻이 정반대다. 이 대입이 안전한 이유는 원격의 뼈가 키네마틱이라는 것뿐이고,
    /// <b>"렌더 전용이라 PhysX로 안 간다"는 이유를 다시 붙이지 말 것</b> (docs §5).
    /// </summary>
    private void TickHoldPoseUntilStream()
    {
        if (!m_holdPoseUntilStream || m_streamer == null || HasMoveAuthority)
            return;

        if (!m_streamer.IsAwaitingFirstPose)
        {
            m_holdPoseUntilStream = false;
            return;
        }

        m_rig.RestoreCapturedPose();
    }

    // "몸이 바닥에 있다"로 보는 골반 높이(m) — 이 안이면 루트 높이를 골반이 아니라 <b>지면</b>이
    // 준다(<see cref="TickCapsuleFollow"/>). NpcRagdoll의 같은 이름 상수와 같은 값이다.
    private const float k_groundedHipsHeight = 0.5f;

    // ---- 캡슐 추종 (#506 — 이 설계의 중심) ----

    /// <summary>
    /// 캡슐을 시체 밑으로 끌고 간다 — <b>권위 피어 전용</b>이고 <see cref="FixedUpdate"/>가 부른다.
    ///
    /// <b>왜 이게 중심인가.</b> 안 하면 캡슐이 사망 지점에 남았다가 정착 순간 1.15m를 텔레포트하고,
    /// 그 한 번의 늦은 점프가 이 기능의 거의 모든 버그의 뿌리였다. 매 스텝 따라가면 cm 단위 잔차로
    /// 줄고 원격은 점프 대신 연속 스트림을 받는다 — 실측과 옛 증상은 docs/player-ragdoll.md §4.
    /// </summary>
    internal void TickCapsuleFollow()
    {
        if (m_movement == null || m_rig == null || m_rig.Hips == null)
            return;

        // 골반 위치를 <b>3차원</b>으로 따라간다 — 수평만 맞추면 공중에 있는 동안 루트가 시체를
        // 대표하지 못한다(이름표·운반 조준·부활 히트박스가 전부 루트에 붙어 있다).
        Vector3 target = m_rig.Hips.position;

        // <b>단 몸이 바닥에 있으면 높이는 지면이 준다</b> — 정착 순간의 낙차를 없애기 위해 무너지는
        // 동안까지 넓혔다. 공중에서는 앉히지 않는다. (docs §4)
        bool haveGround = TryGroundUnder(m_rig.Hips.position, out Vector3 ground);
        bool bodyIsGrounded =
            m_settled
            || (haveGround && m_rig.Hips.position.y - ground.y <= k_groundedHipsHeight);

        if (haveGround && bodyIsGrounded)
            target.y = ground.y - CapsuleBottomOffset;

        // ⚠ <b>루트를 옮기기 전에 뼈를 잡아 두고, 아래에서 되돌린다</b> — 안 감싸면 진입 프레임에 몸
        // 전체가 골반 높이(약 0.9m)만큼 떠서 한 프레임 그려진다. 아래 FollowBodyYaw까지 감싼다.
        //
        // ⚠ <b>이 대입은 "렌더 전용"이 아니다 — 그 전제가 #759의 원인이었다.</b> 그래서 이 함수는
        // FixedUpdate에서만 돈다(스텝당 1회). 근거는 docs/player-ragdoll.md §5.
        m_rig.CapturePose();

        // 사망 중에는 CharacterController가 꺼져 있으므로 대입이 곧 이동이다. 스윕은 쓰지 않는다 —
        // 대리값은 지형을 존중할 이유가 없다 (docs §4).
        m_root.position = target;

        // ⚠ <b>yaw는 비행 중에만 따라간다</b> — 정착 후에도 돌리면 목이 비틀리고 되먹임 고리가 생긴다.
        // 상태가 아니라 <b>깃발</b>로 물어야 한다(시체도 끝까지 Ragdoll이다). 그리고 그 깃발은
        // m_settled가 아니라 <b>일방향 래치</b>여야 한다 — 밟혀 깨어난 몸이 다운된 본인의 시야를
        // 돌리지 않게 (m_yawFollowDone 주석, #865). (docs §11)
        if (!m_yawFollowDone)
            FollowBodyYaw();

        // 위 CapturePose의 짝 — 루트를 옮기고 돌린 뒤 뼈를 원래 월드 포즈로 되돌린다.
        m_rig.RestoreCapturedPose();
    }

    // 목표 yaw로 <b>감쇠 추종</b>한다 — 슬램하면 루트에 매달린 것들(이름표·아이템)이 한 프레임에
    // 통째로 돈다. 프레임률 독립 지수 감쇠이고, ⚠ 기준은 <b>fixedDeltaTime</b>이다(#759 — 부르는
    // 쪽이 물리 스텝마다 돈다). 근거는 docs/player-ragdoll.md §11.
    private void FollowBodyYaw()
    {
        if (!m_alignRootYawToBody || !TryGetRootYaw(out float yaw))
            return;

        float current = m_root.eulerAngles.y;
        float eased = m_rootYawFollowSpeed > 0f
            ? Mathf.LerpAngle(
                current,
                yaw,
                1f - Mathf.Exp(-m_rootYawFollowSpeed * Time.fixedDeltaTime)
            )
            : yaw;

        m_root.rotation = Quaternion.Euler(0f, eased, 0f);
    }

    // 루트가 향해야 할 yaw — 리그가 내는 순수한 몸 방향에 기상 클립 보정을 얹은 값.
    private bool TryGetRootYaw(out float yaw)
    {
        if (!m_rig.TryGetBodyYaw(out yaw))
            return false;

        yaw += m_rootYawOffset;
        return true;
    }

    // ---- 정착 ----

    /// <summary>
    /// 래그돌이 <b>돌아가는 중에</b> 물리 권위가 바뀐 경우를 받는다 — 안전망. (#865)
    ///
    /// <b>정상 경로에서는 걸리지 않는다.</b> 소유권 이관은 <c>PlayerIncapacitation.SetCause</c>가
    /// 원인 대입 직후 <b>같은 프레임</b>에 처리하고 이 폴링은 그 뒤 Update에서 도므로, 이관은 언제나
    /// <c>Animated</c> 구간에서 끝난다. 다운도 서버로 옮기게 되면서(#865) 다운→사망 전이에는 이관이
    /// 아예 없다. 남는 경로는 <b>비행 중 외부 피해로 다운이 되는 것</b> 하나다(비행은 오너 권위다).
    ///
    /// 안 받으면 양쪽 피어가 동시에 깨진다:
    ///  · 권위를 <b>잃은</b> 쪽 — 뼈가 동적으로 남은 채 자세 패킷까지 받아, 물리와 스트림이 같은 뼈를
    ///    매 프레임 번갈아 쓴다(<see cref="ReleaseBonesToPhysics"/>는 진입 때 한 번만 돈다).
    ///  · 권위를 <b>얻은</b> 쪽 — 뼈가 키네마틱으로 남고, <c>RagdollRig.AllAsleep</c>은 키네마틱 바디를
    ///    건너뛰므로 <b>곧바로 참</b>이 되어 무너지기도 전에 정착한다. 그 뒤 깨어남 폴링도 영구히
    ///    안 돌아 몸이 굳는다.
    ///
    /// 근거와 고장 전수는 docs/865-down-ragdoll.md.
    /// </summary>
    private void TickAuthorityHandover()
    {
        bool authority = HasMoveAuthority;
        if (authority == m_hadMoveAuthority)
            return;

        m_hadMoveAuthority = authority;
        if (m_state != RagdollState.Ragdoll)
            return; // 래그돌 밖에서의 이관은 정상 경로다 — 진입이 새 권위로 알아서 정한다

        // 이 함수가 이미 양방향으로 정확하다 — 잃은 쪽은 키네마틱 + 자세 붙들기, 얻은 쪽은
        // SetKinematic(false) 안의 Physics.SyncTransforms가 <b>지금 화면에 있는 자세</b>에서
        // 물리를 출발시킨다.
        ReleaseBonesToPhysics();

        // 멱등이다. ⚠ <b>양쪽 피어에서 부른다</b> — 원격 분기가 세우는 두 래치가 여기서 다시 서야 한다.
        m_streamer?.BeginStreaming();

        // 새 권위가 물리로 정착을 다시 판정한다. yaw 래치는 건드리지 않는다 — 이미 정착한 몸이면
        // 그대로 두는 것이 맞다.
        m_settled = false;
        m_elapsedInRagdoll = 0f;
    }

    // 잠든 몸이 다시 움직이기 시작했다 — 스트림을 되살린다. (NpcRagdoll.ServerResumeFromSleep와 짝)
    private void ResumeFromSleep()
    {
        m_settled = false;
        m_elapsedInRagdoll = 0f;
        m_streamer?.ResumeStreaming();
    }

    /// <summary>
    /// 정착 — <b>몸도 루트도 건드리지 않는다.</b> 깃발을 세우고 스트림을 끊는 것뿐이다.
    /// 루트 정렬이 사라져도 되는 이유는 <see cref="TickCapsuleFollow"/>가 이미 매 스텝 하고 있기
    /// 때문이다. 옛 4단계 정착이 무엇이었고 왜 지웠는지는 docs/player-ragdoll.md §10.
    /// </summary>
    private void Settle()
    {
        if (m_settled)
            return;

        m_settled = true;
        m_yawFollowDone = true; // 한 번 정착하면 이 에피소드에서 다시 안 돈다 (필드 주석)
        DumpSettleTrace();

        // 스트림을 끊고 마지막 자세를 한 번 더 보낸다 — 원격의 종착 상태다.
        // ⚠ 좌표계는 바뀌지 않으므로 원격 화면은 변하지 않는다. 그것이 사양이다. (docs §10)
        m_streamer?.EndStreaming();

        // 사망(Die)은 부활 키트가 별도로 풀지만, 비행(Launched)은 정착 자체가 복구 신호다 — 여기서
        // 서버에 알린다(#815). 이 함수는 권위 피어에서만 도므로(Update의 HasMoveAuthority 게이트)
        // 곧 그 오너가 통보를 보낸다.
        if (m_incapacitation == null)
            return;

        if (m_incapacitation.IsLaunched)
        {
            m_incapacitation.RequestLaunchSettled();
            return;
        }

        // 래그돌이 도는 중에 죽었다면 소유권 이관이 <b>이 순간까지 미뤄져 있다</b> (#957) — 지금이
        // 그것을 푸는 자리다. 미뤄 둔 것이 없으면 저쪽이 스스로 무동작이라 조건을 따지지 않는다.
        m_incapacitation.RequestDeathSettled();
    }

    // 루트 원점에서 캡슐 밑면까지의 높이 — 지면 점에 루트를 그대로 놓으면 캡슐이 떠서 출발한다 (docs §4).
    private float CapsuleBottomOffset =>
        m_controller == null ? 0f : m_controller.center.y - m_controller.height * 0.5f;

    // 골반 밑 지면 탐색 — 정착 자격 판정·정착 정렬·진입 계측이 <b>같은 것</b>을 쓴다.
    private bool TryGroundUnder(Vector3 hipsPosition, out Vector3 point) =>
        RagdollGround.TryGroundUnder(hipsPosition, m_groundProbeDistance, m_groundMask, out point);
}
