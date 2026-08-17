using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// NPC 사망 래그돌 (#571) — 죽은 몸(<see cref="NpcState.Dead"/>)의 애니메이터를 끄고 뼈를 물리에 넘긴다.
///
/// <b>이 클래스가 쥔 것은 "누가 위치를 쥐나"다.</b> 뼈를 물리에 넘기고 되돌리는 일은
/// <see cref="RagdollRig"/>가 한다 — 그쪽은 네트워크·권위·이동 프록시를 모르는 순수 물리라
/// 플레이어와 NPC가 그대로 공유한다. 여기 남은 것은 전부 <b>NPC 고유</b>다:
/// NavMeshAgent를 대리값으로 쓰는 것, 서버 권한 NetworkTransform, 사망 폴링.
///
/// <b>표현 계층 전용이다.</b> 뼈를 <b>매 틱</b> 동기화하지 않는다 — 위치 판정은 서버 트랜스폼이
/// 계속 쥔다. 그래서 NetworkBehaviour가 아니고, 피어로 나가야 하는 한 줄(얼린 자세)만
/// <see cref="NpcDeath"/>를 통해 쏜다.
///
/// <b>권위가 두 구간으로 갈린다 (#571).</b> 이게 이 클래스를 읽는 열쇠다:
/// <list type="bullet">
///   <item><b><c>Ragdoll</c> — 뼈가 주인이다.</b> 물리가 몸을 만들고 루트가 그 밑을 따라간다
///   (<see cref="TickRootFollow"/>). 각 피어가 자기 로컬 물리를 돌리므로 결과가 조금씩 갈리고,
///   그 표류만 스트리밍된 루트로 잡아 준다(<see cref="TickAlignBonesToRoot"/>).</item>
///   <item><b><c>Frozen</c> — 루트가 주인이다.</b> 정착하는 순간 전 뼈를 키네마틱으로 얼린다.
///   키네마틱 뼈는 <b>부모 트랜스폼을 그대로 따라가므로</b>(동적일 때와 정반대) 루트를 옮기면 몸이
///   따라온다 — 유치장 수감이 <c>transform.position</c> 한 줄이 되는 이유다.</item>
/// </list>
///
/// <b>얼리면 동기화할 것이 없어진다.</b> 자세가 상수가 되므로 <b>얼리는 순간 1회</b>만 보내면
/// (뼈 로컬 회전 + 골반 로컬 위치, <see cref="RagdollRig.CaptureLocalPose"/>) 그 뒤로는 루트 하나만
/// 복제하면 된다. 매 틱 정렬로 뼈를 끌어당기던 예전 구조는 <b>원격의 리지드바디가 영영 잠들지 못해
/// 바닥에서 비벼졌고</b>, 루트의 지면 판정 오차가 그대로 몸의 높이 오차가 됐다 — 둘 다 사라진다.
///
/// ⚠ <b>얼린 시체는 스스로 바닥을 찾지 않는다.</b> 루트를 벽·바닥 안에 놓으면 그대로 박힌다.
/// 그래서 얼리는 시점은 "물리가 이미 정착시킨 순간"이고, 명시적 배치는 바닥에 스냅된 좌표를 받는다
/// (<see cref="JailZone.RandomRestPointInRoom"/>).
///
/// <b>진입은 둘이다 — 사망과 기절</b> (#572 3단계). 성격이 정반대라 이 클래스를 읽을 때 함께 봐야 한다:
/// <list type="bullet">
///   <item><b>사망은 한 번이고 영구다.</b> 시체는 일어나지 않으므로 이탈 경로가 없다.</item>
///   <item><b>기절은 반복된다.</b> 같은 인스턴스가 몇 번이고 눕고 일어난다 — 그래서 이탈
///   (<see cref="ExitRagdoll"/>)과 <b>에피소드 단위 리셋</b>이 필요하다. 태우는 것은
///   <see cref="NpcStun.HasStunOverlay"/>(테이저·넉다운)뿐이고 넉백 착지 KO는 <b>뺀다</b> —
///   근거는 <see cref="WantsRagdoll"/>.</item>
/// </list>
///
/// <b><see cref="PlayerRagdoll"/>과 갈리는 것</b>
/// <list type="bullet">
///   <item><b>권위가 서버다.</b> 플레이어는 오너가 캡슐로 시체를 따라가고 그 루트를 스트리밍하지만,
///   NPC에는 오너가 없어 <b>서버가 그 역할</b>을 한다. 서버 외 전원이 원격이다.</item>
///   <item><b>기상 블렌드가 없다.</b> 애니메이터로 <b>즉시</b> 돌아간다(계획서 §2-6①(b)) — 한 프레임
///   튀는 대신 플레이어가 아직 못 고친 "부활 시 큰 회전"(corpse-split §4)을 물려받지 않는다.</item>
/// </list>
///
/// <b>붙이는 곳: NPC 프리팹 루트</b>(<see cref="NpcController"/>와 같은 오브젝트).
/// ⚠ <see cref="RagdollRig"/>는 여기가 아니라 <b>리그를 직속 자식으로 가진 오브젝트</b>(NPC는
/// <c>Model</c>)에 붙는다 — 그래서 <c>GetComponent</c>가 아니라 <c>GetComponentInChildren</c>으로 찾는다.
/// 부착은 <c>RagdollSetup</c>이 한다.
/// </summary>
public class NpcRagdoll : MonoBehaviour
{
    // 지면을 못 찾아도 결국은 정착시키는 최후 배수 — 맵 밖으로 떨어져 나간 시체가 Ragdoll 상태에
    // 영원히 갇히지 않게 하는 안전장치. 이 경로로 오면 시체는 허공에 굳지만 상태 기계는 계속 돈다.
    private const float k_lostBodyTimeoutFactor = 4f;

    // "몸이 바닥에 있다"로 보는 골반 높이(m) — 이 안이면 루트 높이를 골반이 아니라 <b>지면</b>이 준다
    // (<see cref="TickRootFollow"/>). 누운 시체의 골반은 약 0.15~0.25m이고, 서 있거나 날아가는 몸은
    // 그보다 훨씬 높다. 정확한 경계가 필요한 값이 아니라 <b>그 둘을 가르기만</b> 하면 되는 값이다.
    private const float k_groundedHipsHeight = 0.5f;

    // 원격의 "당겨오기가 끝났는지" 잔차 판정은 없어졌다 (#571) — 정착은 이제 권위 피어만 하고,
    // 원격은 그 결과(자세)를 받아 갈아끼우므로 스스로 정착 자격을 물을 일이 없다.

    private enum RagdollState
    {
        Animated, // 평시 — 전 Rigidbody 키네마틱, 애니메이터가 포즈를 쥔다
        Ragdoll, // 물리 중 — 애니메이터 정지, 무너지거나 날아가는 구간. <b>뼈가 루트를 끈다</b>
        Frozen, // 정착 완료 — 전 뼈 키네마틱으로 얼린다. <b>루트가 뼈를 끈다</b> (권위 반전, 아래 클래스 주석)
    }

    [Header("정착 판정")]
    [Tooltip("뼈 평균 속도(m/s)가 이 아래로 내려가면 멈춘 것으로 본다")]
    [SerializeField] private float m_settleSpeedThreshold = 0.15f;

    [Tooltip("위 속도 조건이 이만큼 유지되어야 정착으로 확정한다(초) — 한 프레임 튀는 값에 속지 않게")]
    [SerializeField] private float m_settleHoldSeconds = 0.3f;

    [Tooltip("정착 판정 타임아웃(초) — 지형에 껴서 영원히 떨리는 경우의 안전장치")]
    [SerializeField] private float m_settleTimeoutSeconds = 5f;

    [Header("기상 블렌드")]
    [Tooltip("래그돌 자세에서 애니메이터 자세로 섞는 시간(초) — 0이면 즉시 복귀(예전 동작). " +
             "줄을 풀거나 기절이 끝나 일어설 때 몸이 한 프레임에 튀는 것을 없앤다")]
    [SerializeField] private float m_blendSeconds = 0.3f;

    [Header("정착 후 정렬")]
    [Tooltip("시체 밑 지면을 찾는 레이캐스트 마스크 — 지형(Default). 래그돌 뼈는 다른 레이어라 걸리지 않는다")]
    [SerializeField] private LayerMask m_groundMask = 1;

    [Tooltip("골반 아래로 지면을 찾는 거리(m). 짧게 잡을 것 — 길면 얇은 실내 바닥을 뚫고 아래층 지면을 " +
             "찾아내 시체가 한 층 밑으로 순간이동한다. 못 찾으면 골반 높이를 쓴다")]
    [SerializeField] private float m_groundProbeDistance = 1.5f;

    [Tooltip("원격 피어가 착지한 시체를 서버 위치로 당겨오는 속도(m/s) — 수평만. " +
             "보정은 동력이 아니라 표류 방지다. 몸을 움직이는 것은 각 피어의 로컬 물리다")]
    [SerializeField] private float m_alignPullSpeed = 1.5f;

    [Tooltip("원격 피어가 비행 중 시체를 서버 골반으로 당겨오는 속도(m/s) — 3차원. " +
             "⚠ <b>시체의 비행 속도와 짝이다</b> — 폭발 임펄스를 연결하면 여기도 같이 올릴 것")]
    [SerializeField] private float m_flightAlignPullSpeed = 16f;

    [Tooltip("원격 시체가 스트리밍된 루트에서 이만큼(m) 벗어나면 보정을 스냅으로 바꾼다 — 안전망이다. " +
             "정상 동작에서는 걸리지 않아야 하고, 자주 걸리면 입력(임펄스)이 어긋난 것을 봐야 한다")]
    [SerializeField] private float m_alignSnapDistance = 2.5f;

    [Tooltip("원격 피어의 뼈 속도 상한(m/s). 튜닝 손잡이가 아니라 발산 차단선이다 — " +
             "권위 피어는 건드리지 않으므로 판정·궤적은 그대로다. 0이면 끈다")]
    [SerializeField] private float m_remoteSpeedClamp = 20f;

    [Header("진단")]
    [Tooltip("루트 추종·얼림·배치·기상을 실측해 콘솔에 남긴다 — <b>루트 높이의 주인이 골반인지 지면인지</b>를 " +
             "가르는 계측이다. 눈으로는 애매한 네 가지를 숫자로 갈라 준다:\n\n" +
             "· [루트추종] 비행 중에는 스냅=N·루트↔골반Y=0(3차원 추종), 바닥에서는 스냅=Y (임계값 검증)\n" +
             "· [얼림] 루트낙차 — 이것이 0이어야 클라의 침하가 없다 (B의 판정 기준)\n" +
             "· [배치] 여유 = 최저뼈Y − 바닥Y. 수감 도착 실측 기준값이 +0.005다\n" +
             "· [기상] NavMesh 샘플 거리 — 루트가 바닥에 있으면 짧아야 한다\n\n" +
             "확정되면 끈다")]
    [SerializeField] private bool m_logRootFollow;

    private NpcController m_owner;
    private RagdollRig m_rig; // 뼈 한 벌 — 물리 조작 전부를 여기 위임한다. 리그 소유자(Model)에 붙어 있다
    private RagdollRope m_rope; // 관절 밧줄 — 리그와 같은 오브젝트에 붙는다(RequireComponent)

    private Animator m_animator;
    private NavMeshAgent m_agent;
    private NpcAnimationDriver m_driver; // 기상 시점의 진실값 — IsProne (#572 3단계)

    // 기상 블렌드 — 래그돌 자세에서 <b>지금 애니메이터가 놓는 자세</b>로 끌고 간다. 순수 보간이라
    // MonoBehaviour가 아니다(그쪽 클래스 주석). 리그가 하나뿐인 NPC는 <b>같은 리그</b>를 섞는다 —
    // 플레이어는 시체/살아있는 리그가 갈려 있어 살아있는 쪽을 섞는 것이 유일한 차이다.
    private RagdollPoseBlend m_blend;
    private bool m_blending;

    // 기상 시 NavMesh를 다시 찾는 반경(m). 넉백 착지와 같은 성격이라 값도 비슷하게 잡는다.
    // 튜닝 손잡이가 아니라 "누운 자리 바로 밑"을 뜻하는 값이라 상수다.
    private const float k_navMeshSampleDistance = 2f;

    // 골반이 NetworkTransform으로 직접 복제되는가 — <b>프리팹 배선에서 읽는다.</b> (#572)
    //
    // <b>스위치를 따로 두지 않는다</b>(<see cref="PlayerRagdoll"/>과 같은 관례) — 배선과 코드가
    // 어긋날 여지를 없애려고 컴포넌트 존재 자체를 진실로 삼는다. 참이면 궤적의 주인이 루트에서
    // 골반으로 넘어가므로 셋이 함께 바뀐다: 원격 정렬이 필요 없어지고(오히려 싸운다), 비권위 피어의
    // 골반은 키네마틱으로 남아야 하고, 밧줄은 권위 피어만 묶는다.
    //
    // 프리팹에서 NetworkTransform을 빼면 셋 다 자동으로 옛 동작(전원이 각자 묶고 정렬로 좁힌다)으로
    // 돌아간다 — 그게 이 조건을 배선에서 읽는 이유다.
    private bool m_hipsIsNetworkSynced;

    // 골반·루트의 NetworkTransform — 순간이동을 <b>보간 없이</b> 보내는 데 쓴다(ServerPlaceCorpse).
    // 배선에서 읽는 것은 m_hipsIsNetworkSynced와 같은 이유다(위 주석) — 컴포넌트 존재가 진실이다.
    private Unity.Netcode.Components.NetworkTransform m_hipsNetTransform;
    private Unity.Netcode.Components.NetworkTransform m_rootNetTransform;

    // 원격 순간이동을 감싸는 일시 얼림이 남은 프레임 수 (0이면 감싸는 중이 아니다).
    // <b>상태(m_state)를 건드리지 않는다</b> — Frozen은 "정착했다"는 뜻이고 여기는 이동 중이다.
    private int m_teleportBracketFrames;

    // 감싸는 길이 — 골반 상태가 적용되고 몸이 그 자리에 도착하는 데 필요한 프레임.
    // 순간이동은 1프레임이지만, 골반 NT와 루트 NT의 상태가 <b>같은 프레임에 온다는 보장이 없어</b>
    // 여유를 둔다. 얼어 있는 동안 시체는 자세가 굳은 채 도착하므로 이 값이 커도 어색하지 않다
    // (호스트도 정착까지 0.3초를 굳은 채 보낸다) — 대신 짧으면 위반이 새므로 넉넉한 쪽으로 잡는다.
    private const int k_teleportBracketFrames = 4;

    private RagdollState m_state = RagdollState.Animated;
    private float m_stillTimer;
    private float m_elapsedInRagdoll;


    // 늦게 접속했는데 대상이 이미 죽어 있던 경우 — 이번 사망은 래그돌을 건너뛴다.
    // 그때의 물리 낙하는 "죽는 순간"이 아니라 이미 끝난 과거라, 재생하면 시체가 뒤늦게 한 번 더 무너진다.
    private bool m_skipThisEpisode;
    private bool m_polledOnce;

    /// <summary>래그돌이 애니메이터로부터 포즈를 빼앗고 있는가 — 표현 계층이 물러나는 판정에 쓴다.</summary>
    public bool IsRagdollActive => m_state != RagdollState.Animated;

    // 위치 권한 — 서버(또는 세션 없는 오프라인 Play)만 루트를 옮길 수 있다.
    // 클라가 옮겨봤자 서버 권한 NetworkTransform이 되돌린다.
    private bool HasMoveAuthority => !m_owner.IsSpawned || m_owner.IsServer;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
        m_agent = GetComponent<NavMeshAgent>();

        // ⚠ 리그는 <b>자식</b>에 있다 — NPC는 몸이 Synty 프리팹의 중첩 인스턴스(Model)라 리그가
        // 그 안에 들어 있고, RagdollRig는 리그를 직속 자식으로 가진 오브젝트에 붙기 때문이다.
        // 플레이어처럼 GetComponent로 찾으면 항상 null이다.
        m_rig = GetComponentInChildren<RagdollRig>(true);
        if (m_rig == null)
        {
            Debug.LogWarning(
                $"NpcRagdoll: RagdollRig를 찾지 못해 사망 래그돌을 끈다 — {name}. "
                    + "Tools > Ragdoll > Finish Setup 을 이 프리팹에 돌릴 것",
                this
            );
            enabled = false;
            return;
        }

        m_rig.EnsureCollected(); // Awake 순서는 보장되지 않는다 — 아래에서 뼈를 요구한다

        // 밧줄도 리그와 같은 오브젝트(Model)에 있다 — RagdollRope가 RagdollRig를 RequireComponent한다.
        m_rope = m_rig.GetComponent<RagdollRope>();

        // 애니메이터도 리그 쪽(Model)에 있다.
        m_animator = GetComponentInChildren<Animator>(true);

        // 드라이버는 루트에 있다(이 컴포넌트와 같은 오브젝트) — 기상 시점을 여기서 읽는다.
        m_driver = GetComponent<NpcAnimationDriver>();

        // ⚠ 섞는 대상은 <b>리그 최상단 이하 전 트랜스폼</b>이다 — 물리를 안 받는 뼈(목·손가락·발)까지
        // 포함해야 한다. 그것들은 래그돌 자세에 멈춰 있어, 빼놓으면 블렌드 첫 프레임에 목과 손이 튄다.
        m_blend = new RagdollPoseBlend(m_rig.BoneRoot);

        m_hipsNetTransform =
            m_rig.HipsBody != null
                ? m_rig.HipsBody.GetComponent<Unity.Netcode.Components.NetworkTransform>()
                : null;
        m_hipsIsNetworkSynced = m_hipsNetTransform != null;

        m_rootNetTransform = GetComponent<Unity.Netcode.Components.NetworkTransform>();
    }

    // ---- 원격 순간이동 (계획서 §4) ----

    /// <summary>
    /// 원격의 순간이동을 <b>얼림으로 감싼다</b> — <see cref="NpcCorpseHipsTransform"/> 전용 진입점.
    /// 골반 NT가 순간이동 상태를 적용하는 <b>그 프레임에</b> 불린다.
    ///
    /// <b>이것이 원격에 없던 얼림이다.</b> 뼈는 전부 골반의 자식이고 키네마틱 뼈는 부모 트랜스폼을
    /// 따라가므로, 얼린 채 골반이 점프하면 몸이 통째로 도착한다. 얼리지 않으면 <b>동적 뼈 열 개가
    /// 제자리에 남아</b> 관절이 전량 위반되고, 솔버가 그것을 메우며 초속 6261m을 뼈에 먹인다
    /// (계획서 §3-3 실측).
    ///
    /// <b>상태를 <see cref="RagdollState.Frozen"/>으로 바꾸지 않는다.</b> 저 상태는 "정착했다"는
    /// 뜻이고 여기는 이동 중이다 — 바꾸면 <see cref="Update"/>의 조기 반환이 걸려 도착 후 정착
    /// 판정이 돌지 않는다. 속도는 <see cref="RagdollRig.SetKinematic"/>이 양방향에서 지운다.
    ///
    /// <b>멱등</b>이다 — 감싸는 중에 상태가 한 번 더 오면 창을 다시 연다(골반·루트 NT의 상태가
    /// 같은 프레임에 온다는 보장이 없다).
    /// </summary>
    internal void BeginTeleportBracket()
    {
        if (m_rig == null || !m_rig.IsValid)
            return;

        // 얼어 있는 몸은 이미 루트의 키네마틱 자식이다 — 감쌀 것이 없다.
        if (m_state == RagdollState.Frozen)
            return;

        if (m_teleportBracketFrames <= 0)
            m_rig.SetKinematic(true);

        m_teleportBracketFrames = k_teleportBracketFrames;
    }

    // 감싸기를 끝낸다 — 도착한 자리에서 물리로 되돌린다. 그 뒤는 각 피어의 로컬 물리가 바닥에
    // 맞게 무너뜨린다.
    //
    // <b>원격도 여기서 끝나지 않는다</b> — 권위 피어가 도착지에서 다시 정착하며 자세를 뿌리고
    // (<see cref="ServerFreezeInPlace"/> → <see cref="ApplyFrozenPose"/>), 원격은 그것을 받아
    // 얼어붙는다. 이 감싸기가 메우는 것은 그 <b>사이 구간</b>뿐이다: 골반이 점프하는 프레임에
    // 동적 뼈가 제자리에 남아 관절이 전량 위반되는 것(실측 6261 m/s)을 막는다.
    private void TickTeleportBracket()
    {
        if (m_teleportBracketFrames <= 0)
            return;

        m_teleportBracketFrames--;
        if (m_teleportBracketFrames > 0)
            return;

        // 이 프레임에 이미 얼었다면(정착) 되돌리지 않는다 — 그쪽이 더 확정적인 상태다.
        if (m_state == RagdollState.Frozen)
            return;

        ReleaseBonesToPhysics();
    }

    /// <summary>
    /// 뼈를 물리로 놓아준다 — <b>골반만은 비권위 피어에서 키네마틱으로 남긴다.</b> (#572)
    ///
    /// 골반을 NetworkTransform이 복제하는 구성에서는 원격의 골반이 <b>물리가 아니라 스트림</b>의
    /// 소유물이다. <c>NetworkRigidbody</c>의 <c>AutoUpdateKinematicState</c>는 스폰·소유권 변경
    /// 시점에만 도는 값이라 래그돌의 토글과 어긋나므로 프리팹에서 꺼 두고 여기서 직접 관리한다.
    ///
    /// 나머지 뼈는 원격에서도 <b>동적으로 둔다</b> — 스트리밍된 골반에 관절로 매달려 각 피어의 로컬
    /// 물리가 흐느적임을 만든다. 전부 키네마틱으로 굳히면 시체가 골반을 따라 통째로 미끄러지는
    /// 조각상이 된다.
    ///
    /// <see cref="RagdollRig"/>가 아니라 여기서 하는 이유는 분리의 기준이다 — 저쪽에는
    /// <c>IsOwner</c>·<c>NetworkObject</c>가 한 번도 나오지 않는다.
    /// </summary>
    private void ReleaseBonesToPhysics()
    {
        m_rig.SetKinematic(false);

        if (m_hipsIsNetworkSynced && !HasMoveAuthority && m_rig.HipsBody != null)
            m_rig.HipsBody.isKinematic = true;
    }

    // ---- 밧줄 파사드 (#571 시체 끌기) ----
    //
    // 실물은 RagdollRope가 쥔다. 여기 파사드를 두는 이유는 <see cref="PlayerRagdoll"/>과 같다:
    // 호출부(NpcRopeDrag)가 "래그돌인 대상에게 밧줄을 묶는다"를 표현하기 때문이다 — 밧줄 컴포넌트를
    // 직접 찾게 하면 "래그돌이 아닐 때는 묶으면 안 된다"는 조건과 "리그가 Model에 있다"는 배치 지식이
    // 둘 다 호출부로 새어 나간다.

    /// <summary>
    /// 시체에 밧줄을 묶는다 — <b>각 피어가 자기 로컬 시체에</b> 건다. 표현·물리 계층이다.
    ///
    /// <b>얼린 몸을 먼저 녹인다</b> (#571). 관절 밧줄은 골반 Rigidbody를 <b>물리로</b> 끄는 것이라
    /// 키네마틱인 채로는 장력이 하나도 안 걸린다(<see cref="RagdollRope.Attach"/>가 <c>WakeAll</c>까지
    /// 부르는 이유가 그것이다). 여기가 <c>Frozen → Ragdoll</c> 복귀의 유일한 문이고, 줄을 놓으면
    /// 정착 판정이 다시 돌아 알아서 얼어붙는다.
    ///
    /// <b>골반이 스트림으로 오면 실제로 묶는 것은 권위 피어뿐이다</b> (#572, 불변식 8). 이 갈림이
    /// 견인 발산의 근원을 없앤다: <c>AttachCorpseRopeRpc</c>가 <c>SendTo.Everyone</c>이라 지금까지는
    /// <b>전 피어가 각자 밧줄을 묶었고</b>, 같은 관절에 <b>서로 다른 입력</b>이 들어갔다 — 앵커 위치가
    /// 피어마다 다르게 계산되기 때문이다(운반자가 원격이면 NetworkTransform 보간값 + 애니메이터가
    /// 얹는 걸음 흔들림, 그것도 피어마다 따로 평가된다). 강성 스프링에 다른 입력을 넣으면 다른 궤적이
    /// 나오고, 그 차이를 보정이 쫓다가 미끄러짐으로 보였다. 골반을 직접 복제하면 <b>원격은 끌 이유가
    /// 없다</b> — 권위 피어가 굴린 결과가 그대로 온다.
    ///
    /// ⚠ <b>녹이는 것은 전 피어가 한다.</b> 가드가 <see cref="Unfreeze"/> <b>뒤</b>에 있는 이유다 —
    /// 여기서 통째로 돌아가면 원격의 시체는 얼어붙은 채 골반만 끌려가는 조각상이 된다.
    /// (<see cref="PlayerRagdoll.BeginRopePull"/>은 애초에 얼지 않아 맨 앞에서 돌아간다)
    /// </summary>
    /// <param name="carrier">밧줄을 쥔 쪽. 보통 운반자의 손 앵커.</param>
    public void BeginRopePull(Transform carrier)
    {
        Unfreeze();

        if (m_hipsIsNetworkSynced && !HasMoveAuthority)
            return;

        m_rope?.Attach(carrier);
    }

    /// <summary>이 사람이 쥔 가닥만 푼다 — 줄다리기에서 한 명이 손을 뗄 때. <b>멱등</b>. (#638)
    /// 남은 참가자의 가닥은 그대로 끌기를 이어간다.</summary>
    /// <param name="carrier">푸는 쪽. <see cref="BeginRopePull"/>에 넘긴 것과 같은 기준이어야 한다.</param>
    public void EndRopePull(Transform carrier)
    {
        m_rope?.Detach(carrier);
    }

    /// <summary>걸린 밧줄을 <b>전부</b> 푼다 — 내려놓기·줄 끊김·운반자 소실. <b>멱등</b>(안 묶여 있으면 무동작).
    /// 얼리지 않는다 — 놓은 몸은 마저 무너져야 하므로 정착 판정에 맡긴다.</summary>
    public void EndRopePull()
    {
        m_rope?.Detach();
    }

    // ---- 얼림 / 녹임 (#571 권위 반전) ----

    /// <summary>지금 얼어 있는가 — 참이면 루트를 옮기는 것만으로 몸이 따라온다.</summary>
    public bool IsFrozen => m_state == RagdollState.Frozen;

    // 얼린다 — 전 뼈를 키네마틱으로 놓아 루트의 자식으로 되돌린다. 자세는 지금 그대로 굳는다.
    // 부르는 곳은 둘: 정착(ServerFreezeInPlace)과 원격의 포즈 수신(ApplyFrozenPose).
    private void Freeze()
    {
        m_rig.SetKinematic(true);
        m_state = RagdollState.Frozen;
    }

    // 애니메이터를 떼어낸다 — <b>얼리기 전에 반드시</b>. 키네마틱 뼈는 트랜스폼이 진실인데
    // 애니메이터도 같은 트랜스폼을 쓰므로, 켜 둔 채 얼리면 다음 프레임에 대기 포즈가 시체를
    // 덮어써 <b>죽은 몸이 서 있게 된다.</b> 이미 꺼져 있으면 무동작.
    private void StopAnimator()
    {
        if (m_state != RagdollState.Animated)
            return;

        if (m_animator != null)
            m_animator.enabled = false;

        m_rig.SetSkinsAlwaysVisible(true);
    }

    /// <summary>
    /// 녹인다 — 얼린 몸을 다시 물리에 넘긴다. 얼어 있지 않으면 무동작. (#571)
    ///
    /// <b>루트를 건드리지 않는다.</b> 뼈는 지금 서 있는 자리에서 그대로 동적으로 바뀌므로 화면은
    /// 이어지고, 그 순간부터 루트가 다시 몸을 따라간다(<see cref="TickRootFollow"/>).
    /// 얼린 채 루트로 끌려다니며 PhysX가 유도해 둔 속도는 <see cref="RagdollRig.SetKinematic"/>이
    /// 물리로 돌려주는 순간 지운다 — 안 지우면 놓는 순간 시체가 날아간다(그쪽 주석의 실측).
    /// </summary>
    public void Unfreeze()
    {
        if (m_state != RagdollState.Frozen)
            return;

        m_state = RagdollState.Ragdoll;
        m_stillTimer = 0f;
        m_elapsedInRagdoll = 0f;

        // 진단 — 물리가 몸을 되받는 순간의 상태. <b>녹임은 결백하다는 것이 실측으로 확인됐다</b>:
        // "얼어 있을 때 고친 트랜스폼이 여기 <c>SyncTransforms</c>에서 액터로 넘어가며 뼈가
        // 순간이동한다"는 가설을 세웠지만, 여유가 <c>−0.019 → −0.019</c>로 <b>변하지 않았다</b>.
        // 사이클마다 몸이 움직인 원인은 전부 얼림 쪽이었다(<see cref="ApplyFrozenPose"/>의 주석).
        float driftBefore = m_rig.MaxBindPositionDrift;
        float clearanceBefore = LowestBoneClearance();

        ReleaseBonesToPhysics();

        if (m_logRootFollow)
        {
            Debug.Log(
                $"[래그돌 녹임] {name} 권한={HasMoveAuthority} "
                    + $"드리프트={driftBefore:F4} 여유 {clearanceBefore:F3} → {LowestBoneClearance():F3} "
                    + $"| 평균속도={m_rig.AverageSpeed:F2}",
                this
            );
        }
    }

    /// <summary>
    /// 얼린 자세를 받아 그대로 재현한다 — 정착 브로드캐스트의 수신구. (#571)
    /// <see cref="NpcDeath"/>가 부르고, <b>서버도 자기 것을 받는다</b>(그쪽 RPC 주석).
    ///
    /// 여기서 로컬 물리의 결과를 <b>버린다.</b> 각 피어가 따로 굴린 몸은 조금씩 다른 자리에
    /// 누워 있는데, 그 차이를 매 프레임 당겨서 좁히던 것이 예전 구조다(정렬). 이제는 서버가
    /// 확정한 자세로 한 번에 갈아끼우고 얼린다 — 그 뒤로는 어긋날 여지가 없다.
    ///
    /// <b>이것이 원격의 종착 상태다</b> (#572 후속). 원격에는 정착 판정도 얼림도 없으므로
    /// (<see cref="Update"/>가 비권위에서 끊긴다) 이 수신이 없으면 시체가 <b>끝나는 지점을 갖지
    /// 못한다</b> — 실측에서 순간이동 1.95초 뒤에도 뼈가 4.17 m/s로 계속 떨었다.
    ///
    /// <b>매다는 자리가 피어마다 갈린다</b> — 아래 <c>hangsOnHips</c>. 골반이 복제되는 배선에서
    /// 원격은 몸을 <b>골반에</b> 매달고, 그 외에는 예전대로 루트에 매단다.
    /// </summary>
    public void ApplyFrozenPose(Quaternion[] boneRotations, Vector3 hipsLocalPosition)
    {
        if (m_rig == null || !m_rig.IsValid)
            return;

        // 아직 무너지지도 않은 몸(늦게 접속해 이번 사망을 건너뛴 피어)도 여기서 시체가 된다.
        StopAnimator();

        // ⚠ <b>원격은 자세를 받지 않는다 — 골반만 스트림, 팔다리는 로컬 물리다.</b> (#572)
        //
        // <b>한때 여기서 원격도 얼렸다가 되돌렸다</b>(2026-08-14). 근거는 "원격에 종착 상태가 없다"
        // (얼림=0/212, 순간이동 1.95초 뒤에도 4.17 m/s)였는데, <b>그 실측은 순간이동이 클라에서
        // 6261 m/s로 터지던 때 찍은 것이다</b> — 폭발한 몸이 안 멈추는 것은 당연하고, 종착 상태의
        // 부재가 아니라 <b>도착이 깨끗하지 않은 것</b>이 병이었다. 그쪽을 고치고 나면 로컬 물리는
        // 스스로 가라앉고 PhysX가 재운다.
        //
        // 그리고 원격을 영구히 얼리면 <b>두 가지가 새로 생긴다</b>: 정착 순간 자세가 호스트 것으로
        // 갈아끼워지는 튐과, 키네마틱이 된 뼈가 <b>계층을 따라가</b> 루트 하강에 끌려 땅에
        // 들어갔다 나오는 것(실측: 7프레임 동안 골반y가 루트y를 그대로 따라 0.402 → 0.278).
        // 둘 다 <b>동적 뼈는 부모를 따라가지 않는다</b>는 이 파일의 전제를 깨서 생긴다.
        //
        // <b>동기화는 순간이동 순간에만 한다</b> — <see cref="BeginTeleportBracket"/>이 골반이
        // 점프하는 그 프레임만 얼려 몸을 함께 옮기고, 끝나면 곧바로 물리로 돌려준다.
        if (m_hipsIsNetworkSynced && !HasMoveAuthority)
        {
            // 늦게 접속해 아직 애니메이터를 쥐고 있던 몸만 물리로 내려놓는다. 이미 무너지는
            // 중이었으면 그대로 둔다 — 건드릴수록 궤적이 끊긴다.
            if (m_state == RagdollState.Animated)
            {
                m_state = RagdollState.Ragdoll;
                ReleaseBonesToPhysics();
            }

            return;
        }

        // <b>얼리는 것이 먼저다.</b> 동적인 채로 자세를 쓰면 다음 물리 스텝이 PhysX의 포즈로 덮는다 —
        // 키네마틱으로 바꾸고 나서야 트랜스폼이 진실이 된다. 아래가 실패해도 얼어 있는 편이 낫다
        // (그 피어의 로컬 물리 자세로 굳을 뿐, 계속 흔들리지는 않는다).
        Freeze();

        // ⚠ <b>여기 <c>m_rig.RestoreBindBoneLengths()</c>가 있었다 — 빼냈다.</b> (실측 2026-08-14)
        //
        // <b>그 줄은 "받는 쪽"을 위한 것이었다.</b> 근거는 "받는 자세는 로컬 회전뿐이라(길이는 관절이
        // 유지한다는 전제) 늘어난 리그에 입히면 보낸 쪽과 다른 몸이 나온다 — 갈아끼우기 직전에 길이를
        // 되돌려 그 전제를 실제로 참으로 만든다"였다.
        //
        // <b>그런데 받는 쪽은 위 조기 반환으로 여기 오지 않는다</b>(#572 골반 복제). 실제로 이 줄을
        // 타는 피어는 <b>보낸 쪽(권위) 하나뿐</b>이고, 그쪽은 누구와 자세를 맞출 필요가 없다 —
        // 방금 자기가 캡처해 보낸 값을 자기가 되받는 것이다. 근거가 통째로 비어 있었다.
        //
        // <b>그리고 그 줄이 두 증상의 원인이었다.</b> 제자리에서 밧줄을 묶었다 풀기 4회 실측:
        //  · <c>드리프트 0.0368 / 0.0387 / 0.0388 / 0.0385 / 0.0383 → 0.0000</c> — 매 사이클 팔다리가
        //    <b>3.8cm를 한 프레임에</b> 이동했다("팔다리가 한번에 이동")
        //  · 그 교정이 몸을 바닥 안으로 밀어, 얼림 직후 <c>여유 +0.047</c>이 다음 녹임 시점에
        //    <c>−0.02</c>였다 — <b>키네마틱이라 물리가 밀어낼 수 없어</b> 박힌 채 있다가 녹는 순간
        //    탈출로 떠올랐다("바닥에 붙었다가 살짝 떴다가", 왕복 약 7cm)
        //
        // <b>0.038은 오차가 아니라 평형값이다.</b> 끌지 않아도 0.3초 만에 같은 값으로 되돌아왔고
        // (다섯 번 다 0.037~0.039) 누적도 없었다 — 바닥에 누운 리그가 자기 무게와 접촉으로 관절이
        // 벌어지는 정상 상태다(<c>RagdollSetup</c>이 projection을 끄고 solver 반복으로만 붙든다).
        // 매번 0으로 강제하면 물리가 매번 되돌리므로, 고치는 것이 아니라 <b>씨름하는 것</b>이었다.
        //
        // <b>빼면 오히려 물리가 매끄러워진다</b> — 녹는 순간이 강제된 바인드 자세가 아니라 평형에서
        // 출발하므로 첫 스텝의 관절 위반이 사라진다.
        //
        // ⚠ <b>이 제거의 유일한 리스크는 누적이다.</b> 시체는 <c>ExitRagdoll</c>을 영영 타지 않으므로
        // 이제 길이를 되돌리는 곳이 <b>하나도 없다.</b> 아래 로그가 그것을 감시한다 — 사이클을
        // 거듭해도 0.04 근처에 머물러야 하고, 계속 자라면 평형이 아니라 누적이므로 이 줄을 되살리되
        // <b>블렌드로 흡수</b>할 것(<see cref="RagdollPoseBlend"/>·<see cref="m_blendSeconds"/>).
        if (m_logRootFollow)
        {
            float drift = m_rig.MaxBindPositionDrift;
            Debug.Log(
                $"[래그돌 뼈길이] {name} 권한={HasMoveAuthority} 드리프트={drift:F4} "
                    + $"여유={LowestBoneClearance():F3} "
                    + $"| 되돌리지 않는다 — 사이클을 거듭해도 0.04 근처여야 한다"
                    + $"{(drift > 0.08f ? " ⚠누적되고 있다" : "")}",
                this
            );
        }

        if (!m_rig.ApplyLocalPose(boneRotations, hipsLocalPosition))
        {
            Debug.LogWarning(
                $"NpcRagdoll: 받은 자세의 뼈 수가 맞지 않아 버린다 — {name} "
                    + $"(받음 {(boneRotations == null ? 0 : boneRotations.Length)}, 이 피어 {m_rig.BoneCount})",
                this
            );
        }
    }

    /// <summary>
    /// 시체를 통째로 옮긴다 — <b>서버(또는 오프라인) 전용.</b> 부르는 곳은 유치장 수감
    /// (<see cref="NpcCustody.SendCorpseToJail"/>) 하나다. (#571)
    ///
    /// <b>얼리고 나서 옮긴다.</b> 얼린 뼈는 루트의 키네마틱 자식이라 루트를 옮기면 딸려 오고,
    /// 그 루트는 NetworkTransform이 이미 복제하고 있다 — 그래서 <b>원격에 따로 보낼 것이 없다.</b>
    /// (예전에는 뼈를 피어마다 평행이동시키고 정렬을 0.5초 재워야 했다)
    ///
    /// 끌고 온 시체는 밧줄 때문에 녹아 있으므로(<see cref="BeginRopePull"/>) 여기서 다시 얼린다.
    ///
    /// <b>얼린 채로 끝낸다 — 도착지에서 물리를 다시 돌리지 않는다.</b> (실측 2026-08-14)
    /// 예전에는 "끌려오던 자세가 바닥과 안 맞는다"며 녹였는데, 실측된 도착 여유가 +0.005다(이미
    /// 맞아 있다). 반대로 그 녹임이 <b>감옥에서 264 m/s를 만들어 시체를 벽에 박았다</b> — 근거는
    /// 아래 ④ 뒤 주석이다. 얼림은 순간이동의 <b>수단이자 종착</b>이 됐다.
    ///
    /// <b>계속 끌려갈 시체만 물리로 돌아간다</b> — 관절 밧줄은 키네마틱 골반에 장력이 안 걸려
    /// <see cref="BeginRopePull"/>이 스스로 녹이기 때문이다. 그래서 퇴장은 녹고 수감은 얼어 있는다.
    ///
    /// ⚠ <b>옮기는 동안 얼어 있어야 하는 것은 여전하다</b>: 동적 리지드바디는 부모 트랜스폼을
    /// 따라가지 않아 녹인 채 루트만 옮기면 <b>루트만 가고 몸은 남는다.</b>
    /// </summary>
    /// <param name="position">시체가 놓일 지면 지점 — 루트(발밑) 기준이다.</param>
    public void ServerPlaceCorpse(Vector3 position)
    {
        if (m_rig == null || !m_rig.IsValid)
        {
            transform.position = position;
            return;
        }

        if (!IsFrozen)
            ServerFreezeInPlace();

        // ⚠ <b>줄을 먼저 끊는다 — 이것이 없으면 시체가 발사된다.</b> (계획서 §5-4)
        //
        // 관절 밧줄은 골반을 <b>운반자의 앵커에</b> 묶는다. 그 상태로 시체만 수백 m 옮기면 관절이 그
        // 거리만큼 위반되고, 다음 물리 스텝에서 솔버가 그것을 메우며 거대한 임펄스를 먹인다 —
        // 실측(호스트, 퇴장): <b>237 m/s</b>. 시체는 목표 지점에서 8m를 날아가 지형에 박혔다.
        //
        // <b>수감 경로는 우연히 무사했다</b>: <c>PlayerEscorter.ReleaseAllTethersOnCorpse</c>가 배치
        // 전에 관절까지 걷기 때문이다. 하지만 그 해제는 <b>운반자를 찾아내야</b> 성립하므로 다인에서
        // 빗나갈 수 있고, 실제로 수감이 같은 증상으로 터진 라운드가 있었다(실측 253 m/s).
        // 그래서 <b>배치하는 이 자리</b>에서 한 번 더 보장한다 — 호출부가 무엇을 놓쳤든 상관없게.
        EndRopePull();

        transform.position = position;

        // ⚠ <b>뼈 액터를 먼저 따라오게 한다 — 골반만 혼자 가는 것을 막는다.</b>
        //
        // 이 프로젝트는 <c>m_AutoSyncTransforms = 0</c>이라 위 한 줄은 <b>트랜스폼만</b> 옮긴다.
        // 그런데 아래 <see cref="ServerTeleportNetTransforms"/>의 골반 <c>Teleport</c>는
        // <c>NetworkRigidbody.UseRigidBodyForMotion = 1</c> 때문에 <b>액터</b>를 쓴다 — 즉 골반
        // 액터만 573m를 가고 나머지 열 개는 출발지에 남는다. 실측이 그대로 찍혔다(③④ 뼈분기최대
        // 575.114, ④ 골반 rb차 0.000).
        //
        // 그 상태로 <see cref="Unfreeze"/>가 물리에 돌려주면 <c>SetKinematic</c>의
        // <c>SyncTransforms</c>가 <b>나머지 열 개를 뒤늦게</b> 573m 옮기고, 그 이동이 키네마틱
        // 타깃으로 잡혀 첫 물리 스텝에 속도로 살아난다 — 도착 프레임 <b>278 m/s</b>가 그것이다.
        // (예전에는 <c>Teleport</c>가 없어 열한 개가 <b>같이</b> 옮겨졌고, 그래서 호스트가 멀쩡했다)
        //
        // 여기서 한 줄 밀어 두면 <c>Teleport</c>가 쓸 델타가 0이 되어 그 갈림이 성립하지 않는다.
        Physics.SyncTransforms();

        // <b>얼어 있는 지금 보낸다</b> — 뼈가 루트의 키네마틱 자식이라 위 한 줄로 이미 함께 왔고,
        // 그래서 여기서 읽는 골반 위치가 곧 도착 자세다. 녹인 뒤에 보내면 물리가 한 스텝 굴러
        // 보내는 값과 원격이 재현할 자세가 어긋난다.
        ServerTeleportNetTransforms();

        // 도착 상태 — <b>여유(최저뼈Y − 바닥Y)가 기준값이다.</b> 실측 +0.005로 "이미 맞아 있다"가
        // 확인돼 이 함수가 Unfreeze를 버렸으므로(아래 주석), 루트 높이를 건드리면 여기가 먼저 깨진다.
        // 속도는 얼어 있으므로 0이어야 한다 — 0이 아니면 어딘가에서 물리로 풀린 것이다.
        if (m_logRootFollow)
        {
            float clearance = LowestBoneClearance();
            Debug.Log(
                $"[래그돌 배치] {name} 목표={position.ToString("F2")} 루트={transform.position.ToString("F2")} "
                    + $"| 여유={clearance:F3}"
                    + $"{(float.IsNaN(clearance) ? " ⚠바닥을 못 찾았다(정착 판정이 영영 안 돈다)" : clearance < -0.02f ? " ⚠바닥에 박혔다" : clearance > 0.15f ? " ⚠떠 있다" : " (정상)")}"
                    + $" | 얼림={IsFrozen} 평균속도={m_rig.AverageSpeed:F2} 줄={m_rope != null && m_rope.IsAttached}",
                this
            );
        }

        // ⚠ <b>여기서 녹이지 않는다 — 얼린 채로 끝낸다.</b> (실측 2026-08-14)
        //
        // 예전에는 "얼린 자세는 끌려오던 자세라 감옥 바닥과 안 맞으니 물리로 한 번 더 무너뜨린다"며
        // 무조건 <c>Unfreeze()</c>를 불렀다. <b>실측이 그 전제를 부쉈다</b> — 얼린 채 도착한 시점의
        // 여유(최저뼈y − 바닥y)가 <b>+0.005</b>다. 이미 맞아 있다. <see cref="ServerFreezeInPlace"/>가
        // 루트를 골반 밑 지면에 스냅해 두고, 목적지 좌표도 지면에 스냅된 값이라
        // (<c>JailZone.RandomRestPointInRoom</c>) 그 관계가 그대로 옮겨오기 때문이다.
        //
        // <b>그리고 그 녹임이 남은 사고의 유일한 출처였다.</b> 배치 시퀀스 ①~⑤는 양쪽 경로 모두
        // 깨끗한데(뼈v최대=0.00, 뼈분기최대=0.000), 속도는 전부 <b>녹인 뒤 첫 물리 스텝</b>에서
        // 태어났다. 열린 지형(퇴장)은 살아남지만 <b>사방이 막힌 감옥은 아니다</b>: 수감 도착
        // 프레임에 264 m/s가 들어가 몸이 1.3m 솟아 벽에 박혔고, 그 자리에서는 아래로 쏜 지면
        // 레이가 아무것도 못 잡아(여유=NaN) <b>정착 판정이 영영 돌지 않는다.</b>
        //
        // 시체는 일어나지 않으므로 물리를 한 번 더 돌려서 얻는 것은 <b>자세의 자연스러움뿐</b>이고,
        // 잃는 것은 배치의 결정성이다. 그 거래를 반대로 한다.
        //
        // ⚠ <b>퇴장도 예외가 아니다 — 줄을 다시 매지 않는다.</b> (실측 2026-08-14)
        //
        // 여기에는 "끌려오던 시체는 계속 끌 수 있어야 하니 끊은 줄을 같은 운반자에게 다시 맨다"가
        // 있었다. 그런데 <see cref="BeginRopePull"/>은 <b>묶기 전에 녹인다</b>(키네마틱 골반에는
        // 장력이 안 걸린다) — 즉 그 한 줄이 <b>퇴장 경로에만 물리 복귀를 되살리고 있었다.</b>
        //
        // <b>그리고 터지는 쪽은 정확히 그쪽이다.</b> 같은 실행에서 두 경로가 갈렸다:
        // <list type="bullet">
        //   <item>수감(얼린 채 유지) — 여유 +0.047 · 최대v <b>0.00</b> · 90프레임 내내 얼림=1</item>
        //   <item>퇴장(물리 복귀) — 최대v <b>417.07</b> · 최저뼈y −2.127 · 얼림=0으로 안 멈춘다</item>
        // </list>
        // 폭발 시점의 줄=0·끌림=0이라 <b>장력 때문이 아니다</b> — 순간이동 직후 물리로 돌아간 것
        // 자체가 원인이고, 그래서 수감에서 <c>Unfreeze()</c>를 없앤 근거가 여기에도 그대로 적용된다.
        //
        // ⚠ <b>이건 동작 변경이다</b>: 퇴장하면 줄이 풀린 채 시체가 문 밖에 눕는다. 계속 끌려면
        // 다시 잡아야 한다 — 그때는 순간이동 직후가 아니라 평범한 자리에서 녹으므로 안전하다.
        // 되돌리려면 아래 두 줄을 살리되, 먼저 "순간이동 직후 물리 복귀"를 안전하게 만들 것.
        //
        //   if (carrier != null)
        //       BeginRopePull(carrier);
    }

    /// <summary>
    /// 순간이동을 <b>보간 없이</b> 원격에 보낸다 — 서버 전용. (계획서 §4-1)
    ///
    /// <c>Teleport</c>는 해당 상태 갱신 1회에 대해 보간을 끄는 NGO 기본기다. 안 쓰면 원격이 이 거리를
    /// <b>여러 프레임에 걸쳐 보간하고</b>(실측 573.94m을 21프레임 / ~0.5초), 그 구간 전체가 관절
    /// 위반으로 남는다.
    ///
    /// <b>루트와 골반 둘 다 보낸다.</b> 실측에서 <b>루트 NT도 같이 보간했다</b> — 골반만 스냅시키면
    /// 루트가 뒤늦게 따라와 스캔·이름표·상호작용 콜라이더가 한동안 옛 자리에 남는다.
    ///
    /// 이 호출이 원격에서 <see cref="NpcCorpseHipsTransform"/>의 얼림을 깨우는 신호이기도 하다 —
    /// 그쪽은 <c>IsTeleportingNextFrame</c>을 보고 감싼다. <b>둘은 같은 이벤트의 양쪽 끝이다.</b>
    /// </summary>
    private void ServerTeleportNetTransforms()
    {
        // 세션이 아니면 보낼 곳이 없다 — 오프라인 Play에서는 위 한 줄이 곧 이동의 전부다.
        if (m_owner == null || !m_owner.IsSpawned)
            return;

        if (m_rootNetTransform != null)
            m_rootNetTransform.Teleport(transform.position, transform.rotation, transform.localScale);

        if (m_hipsNetTransform != null && m_rig.Hips != null)
        {
            Transform hips = m_rig.Hips;
            m_hipsNetTransform.Teleport(hips.position, hips.rotation, hips.localScale);
        }
    }

    // ---- 진입 ----

    /// <summary>
    /// 래그돌 진입 — <b>멱등이다.</b> 이미 물리 중이면 임펄스만 누적하고, 정착했으면 무동작.
    ///
    /// 멱등이어야 하는 이유는 도착 순서다. 지금은 사망 폴링 하나뿐이라 순서 문제가 없지만,
    /// 폭발 임펄스를 연결하면(후속) 사망 사실과 임펄스가 서로 다른 오브젝트에서 와
    /// 순서가 보장되지 않는다 — 플레이어 쪽이 그 순서로 두 번 물렸다(506 §9-19).
    /// </summary>
    /// <param name="impulse">밀려나는 속도(m/s). 힘없이 무너지는 사망은 <see cref="Vector3.zero"/>.</param>
    public void EnterRagdoll(Vector3 impulse)
    {
        if (m_rig == null || !m_rig.IsValid)
            return;

        if (m_state == RagdollState.Ragdoll)
        {
            m_rig.ApplyImpulse(impulse); // 늦게 도착한 임펄스 — 누적한다
            return;
        }

        if (m_state != RagdollState.Animated)
            return; // 이미 정착했다 — 다시 날리지 않는다

        StopAnimator(); // 상태를 바꾸기 전에 — 이 함수는 Animated일 때만 도는 멱등 함수다

        m_state = RagdollState.Ragdoll;
        m_stillTimer = 0f;
        m_elapsedInRagdoll = 0f;

        // 블렌드 중에 다시 쓰러지면(재기절) 섞던 것을 버린다 — 안 버리면 무너지는 몸을
        // 애니메이터 자세로 도로 끌어당긴다.
        m_blending = false;

        m_haveClientSample = false; // 진단 — 지난 에피소드 값과 비교해 가짜 점프가 찍히지 않게
        m_dipTraceFrames = 0;
        m_haveSnapSample = false;

        ReleaseAgentForRagdoll();

        ReleaseBonesToPhysics();
        m_rig.ApplyImpulse(impulse);

    }

    // ---- 매 프레임 ----

    private void Update()
    {
        PollRagdollTriggers();

        // 얼어 있으면 볼 것이 없다 — 루트가 주인이고 자세는 상수다. 이 조기 반환이 곧
        // "정착한 시체는 매 프레임 아무 비용도 쓰지 않는다"는 뜻이다.
        if (m_state != RagdollState.Ragdoll)
            return;

        // 무너지는 동안에는 <b>뼈가 주인</b>이라 루트가 그 밑을 따라간다 — 플레이어의
        // TickCapsuleFollow에 대응한다. <b>이 컴포넌트가 직접 돌린다</b>: NpcController.Update는
        // 클라에서 즉시 return하고 서버에서도 사망 게이트에서 끊기므로 저기서는 부를 자리가 없다.
        //
        // 얼린 뒤에는 돌지 않는다 — 그때부터는 반대로 루트가 뼈를 끈다.
        TickRootFollow();

        // 정착 판정은 <b>권위 피어만</b> 한다 (#571). 예전에는 각 피어가 자기 물리로 따로 정착했고,
        // 그래서 피어마다 다른 자세로 굳은 뒤 그 차이를 매 프레임 정렬로 좁혀야 했다.
        // 이제는 서버가 정착시켜 그 자세를 한 번 뿌리고, 원격은 받아서 갈아끼운다.
        if (!HasMoveAuthority)
        {
            // ⚠ <b>원격 전용 발산 차단선.</b> 원인 수정이 아니라 값싼 방어선이다 — 원격의 뼈는
            // 스트리밍된 골반에 관절로 매달려 있어서, 골반이 크게 움직이는 순간(순간이동·급한 견인)
            // 관절 위반을 솔버가 메우며 <b>중력으로는 나올 수 없는 속도</b>를 먹인다. 실측 6261 m/s.
            //
            // <b>권위 피어에는 걸지 않는다.</b> 저쪽 속도는 정착 판정의 입력이자 실제 궤적이라
            // 자르면 판정이 바뀐다. 원격의 속도는 표현일 뿐이라 잘라도 잃는 것이 없다.
            //
            // 얼어 있을 때는 돌지 않는다 — 이 조기 반환보다 위에서 이미 끊긴다(뼈가 키네마틱이다).
            m_rig.ClampSpeed(m_remoteSpeedClamp);
            return;
        }

        // <b>끌리는 동안에는 정착 판정을 돌리지 않는다.</b> (#572)
        //
        // 정착은 "몸이 스스로 멈췄다"를 재는 것이고 타임아웃은 그 위의 안전장치 — <b>지형에 껴서
        // 영원히 떨리는 몸</b>을 위한 것이다. 끌리는 몸은 낀 것이 아니라 정상적으로 움직이는 중이라
        // 둘 다 대상이 아니다.
        //
        // 얼면 골반이 키네마틱이 되어 <b>관절 밧줄의 장력이 하나도 안 걸린다</b>
        // (<see cref="BeginRopePull"/> 주석). 그런데 녹이는 것은 <b>묶는 순간뿐</b>이라 묶인 뒤에
        // 얼면 되돌릴 사람이 없다 — 실측으로 두 경로가 다 나왔다: 초속 7.73m로 끌려가다 5초
        // 타임아웃에 얼어붙었고, 죽자마자 묶어 세워 두면 0.3초 만에 "멈춤"으로 얼어붙었다.
        // 둘 다 시체는 고정되고 밧줄만 늘어났다.
        //
        // <b>플레이어에는 없는 문제다</b> — 저쪽 정착은 <c>RestToPhysics</c>로 끝나 뼈가 동적으로
        // 남으므로 정착한 시체도 그대로 끌린다. NPC만 "정착 = 얼림"이라(#571 8단계 권위 반전)
        // 정착이 밧줄과 배타적이 됐고, 그 사실이 판정에 반영돼 있지 않았다.
        //
        // 타이머를 <b>0으로 되돌린다</b> — 놓는 순간부터 다시 재야 한다. 누적을 남기면 오래 끌던
        // 시체가 놓자마자 굳어 마저 무너지지 못한다(<see cref="EndRopePull"/>의 "정착 판정에 맡긴다").
        //
        // ⚠ <c>IsAttached</c>가 아니라 <c>IsBeingCarried</c>다. 운반자가 사라져도 관절은 남으므로
        // (그쪽 주석) 그걸로 미루면 <b>시체가 영영 안 굳는다.</b>
        if (m_rope != null && m_rope.IsBeingCarried)
        {
            m_stillTimer = 0f;
            m_elapsedInRagdoll = 0f;
            return;
        }

        m_elapsedInRagdoll += Time.deltaTime;

        m_stillTimer = m_rig.AverageSpeed <= m_settleSpeedThreshold
            ? m_stillTimer + Time.deltaTime
            : 0f;

        if (m_stillTimer < m_settleHoldSeconds && m_elapsedInRagdoll < m_settleTimeoutSeconds)
            return;

        if (!HasGroundUnderHips()
            && m_elapsedInRagdoll < m_settleTimeoutSeconds * k_lostBodyTimeoutFactor)
            return;

        ServerFreezeInPlace();
    }

    private void LateUpdate()
    {
        // 기상 블렌드 — <b>LateUpdate여야 한다.</b> 이번 프레임에 애니메이터가 이미 놓은 자세가
        // 곧 블렌드의 목표라, 여기서 읽어야 재생 중인 기상 클립을 향해 <b>살아있는 목표</b>로
        // 수렴한다. 목표를 시작 시점에 고정하면 클립은 흘러가는데 블렌드만 옛 프레임을 향해 가서
        // 끝나는 순간 툭 튄다(RagdollPoseBlend 클래스 주석).
        if (m_blending && m_blend.Tick(m_blendSeconds))
            m_blending = false;

        // 원격의 시체를 스트리밍된 루트에 맞춘다. LateUpdate인 이유는 NetworkTransform이 이번
        // 프레임에 적용한 루트 위치를 읽어야 한 프레임 늦지 않기 때문이다.
        //
        // <b>무너지는 동안에만 돈다</b> (#571). 얼린 뒤에는 뼈가 루트의 키네마틱 자식이라 계층이
        // 정확히 붙여 주고, 자세는 서버가 뿌린 그대로다 — 좁힐 차이가 없다.
        // 예전에는 정착 후에도 계속 당겼는데, 그것이 원격 리지드바디를 <b>영영 못 자게 만들어</b>
        // 바닥에서 비벼지는 원인이었다.
        //
        // <b>골반을 직접 복제하면 이 보정을 끈다</b> (#572) — 궤적의 주인이 루트에서 골반으로
        // 넘어가므로 좁힐 잔차가 없고, 켜 두면 스트림이 놓은 골반을 매 프레임 루트 쪽으로 밀어 서로
        // 싸운다. 지금 프리팹이 그 배선이라 이 함수는 <b>실제로는 돌지 않는다</b> — 남겨 둔 것은
        // 골반 복제를 빼면 곧바로 옛 동작으로 돌아갈 수 있게 하기 위해서다. (PlayerRagdoll과 같다)
        if (!m_hipsIsNetworkSynced && !HasMoveAuthority && m_state == RagdollState.Ragdoll)
            TickAlignBonesToRoot();

        // ⚠ <b>얼린 뒤에도 골반 스트림은 누르지 않는다.</b> 여기 "얼린 자세가 스트림에 밀리니
        // 매 프레임 도로 덮어야 한다"는 주석이 구현 없이 남아 있었다 — <b>거꾸로 읽은 것이다.</b>
        // 원격의 얼린 몸은 <b>골반에</b> 매달려 있으므로(<see cref="ApplyFrozenPose"/>) 스트림이
        // 골반을 쓰는 것이 곧 몸을 제자리에 두는 일이고, 덮으면 그때부터 스트림과 싸운다.
        // 자세에서 스트림이 쥐지 않는 부분(뼈 로컬 회전)은 상수라 눌러 줄 것이 없다.

        // 순간이동 감싸기를 마무리한다 — <b>LateUpdate여야 한다.</b> 골반 NT가 이번 프레임에 적용한
        // 위치에서 물리로 되돌려야, 되돌리는 순간의 자세가 곧 도착한 자세가 된다.
        TickTeleportBracket();

        // 클라의 침하를 <b>직접</b> 잰다 — 여기가 마지막 표집 지점이다(다음이 렌더).
        if (m_logRootFollow && !HasMoveAuthority && IsRagdollActive)
            TickClientDipProbe();
    }

    // 래그돌이어야 하는지를 폴링한다 — 읽는 값이 전부 동기화 값이라 전 피어가 같은 답을 얻는다.
    //
    // 이벤트가 아니라 폴링인 이유는 플레이어 쪽과 같다(PlayerRagdoll.PollDeath): 표현 계층이
    // 동기화 값을 매 프레임 읽는 편이 도착 순서에 기대지 않아 단순하다. 기절이 붙으면서 이점이
    // 더 커졌다 — 기절을 푸는 경로가 여섯이나 되는데(시간 만료·수감·연행·줄 풀림·사망·넉백)
    // <b>값만 보면 전부 자동으로 덮인다.</b>
    //
    // ⚠ <b>폴링이 이벤트보다 안전한 지점이 하나 더 있다.</b> 저 경로들은 오버레이를 걷은 <b>뒤에</b>
    // 상태를 바꾸는데, 그 사이가 전부 <b>하나의 동기 호출</b>이라 Update가 중간 상태를 볼 수 없다.
    // 사망이 대표적이다(NpcDeath.ServerEnterDead ②가 오버레이를 걷고 ⑤가 Dead로 전이한다):
    // OnStunnedChanged를 구독했다면 ②에서 "기절이 풀렸다"로 읽어 몸을 <b>한 번 일으켰다가</b>
    // 곧바로 다시 무너뜨렸을 것이다. 폴링은 ⑦까지 끝난 뒤에 보므로 그 튐이 아예 없다.
    private void PollRagdollTriggers()
    {
        bool wants = WantsRagdoll();

        // 접속 직후 이미 쓰러져 있었다면 이번 에피소드는 건너뛴다 — 그 무너짐은 "지금"이 아니라
        // 이미 끝난 과거라, 재생하면 몸이 뒤늦게 한 번 더 무너진다.
        if (!m_polledOnce)
        {
            m_polledOnce = true;
            m_skipThisEpisode = wants;
        }

        if (!wants)
        {
            // ⚠ <b>에피소드가 끝나면 반드시 내린다.</b> 사망은 한 번이라 켜진 채 둬도 됐지만 기절은
            // 반복되므로, 안 내리면 그 피어는 <b>이후 모든 기절 래그돌을 영구히 건너뛴다</b>(§2-1).
            // 죽은 몸은 wants가 계속 참이라 여기로 오지 않는다 — 시체의 skip은 그대로 보존된다.
            m_skipThisEpisode = false;

            ExitRagdoll();
            return;
        }

        if (!m_skipThisEpisode && m_state == RagdollState.Animated)
            EnterRagdoll(Vector3.zero); // 힘없이 무너진다. 폭발은 임펄스를 따로 준다(후속)
    }

    /// <summary>
    /// 지금 이 몸이 래그돌이어야 하는가 — <b>진입과 이탈을 같은 식 하나로 답한다.</b> (#572 3단계)
    ///
    /// 조건을 한 곳에 모으는 이유는 기절이 <b>반복되기</b> 때문이다. 진입 조건과 이탈 조건을 따로
    /// 쓰면 둘이 어긋나는 순간 몸이 눕지도 서지도 못한 채 낀다.
    /// </summary>
    private bool WantsRagdoll()
    {
        // 사망은 영구다 — 시체는 일어나지 않으므로 이 분기가 곧 "이탈 없음"이다.
        if (m_owner.Death.IsDead)
            return true;

        // ⚠ <b>진입 조건과 유지 조건을 갈라 묻는다.</b> 하나로 합치면 "누울 이유"가 사라지는 순간
        // 몸이 벌떡 서는데, <b>누워 있어야 하는 이유는 진입 이유보다 오래 간다.</b>
        //
        // 대표적인 경로가 밧줄이다: <c>ServerApplyRopeDrag</c>가 묶자마자 <c>ExitStun</c>을 불러
        // 오버레이를 걷고(#292 — 안 걷으면 묶자마자 도망친다), 줄을 풀면 묶임 표시도 곧바로 빠진다.
        // 진입 조건(기절)만 보면 그 두 지점에서 각각 래그돌이 풀려 <b>바닥에 누운 몸이 그 자리에서
        // 애니메이션 클립으로 갈아끼워진다.</b>
        if (IsRagdollActive)
        {
            // 이미 래그돌이면 묻는 것은 하나다 — <b>아직 바닥에 있어야 하는가.</b>
            //
            // <see cref="NpcAnimationDriver.IsProne"/>이 그 답을 통째로 든다: 기절해 누운 것,
            // 줄에 눕혀진 것, <b>줄이 풀린 뒤 일어나기를 기다리는 구간</b>(<c>StandUp.IsStandingUp</c>)이
            // 전부 참이고, <b>기상 모션이 실제로 나가는 순간</b> 거짓이 된다
            // (<c>HandleStandUp</c>이 <c>RefreshProne</c>을 부른다).
            //
            // 그래서 §2-4가 요구한 "래그돌 이탈 시점 = 기상 모션 시점"이 값 하나로 표현되고,
            // 몸은 <b>래그돌로 누워 있다가 일어날 때가 되어서야</b> 애니메이터에 넘어간다 —
            // 그 순간 재생되는 클립이 "바닥에 누움 → 일어남"이라 그림이 이어진다.
            return m_driver == null || m_driver.IsProne;
        }

        // 아직 애니메이터가 쥐고 있다 — <b>새로 태울 이유</b>가 있는지 묻는다.
        //
        // <b>기절은 오버레이만 태운다</b>(팀 확정). 넉백 착지 KO(<see cref="NpcState.Stunned"/>)를
        // 빼는 이유는 에이전트 소유권이 정면으로 부딪히기 때문이다: 래그돌은 에이전트에서 손을 떼야
        // 몸이 눕는데(<see cref="EnterRagdoll"/>), 넉백은 착지 시 <c>EndKnockback</c>이 에이전트를
        // <b>켜면서 Warp</b>한다. IsStunned가 아니라 HasStunOverlay를 보는 것이 그 갈림이다.
        //
        // 밧줄은 진입 이유가 아니다 — 묶기는 <b>이미 무력화된 대상</b>에만 걸리므로(#446) 그때는
        // 위 유지 분기에 있다.
        if (!m_owner.Stun.HasStunOverlay)
            return false;

        // 기상 모션이 이미 나간 뒤라면(짧은 기절의 끝자락) 태우지 않는다 — 일어나는 몸을 다시 눕힌다.
        return m_driver == null || m_driver.IsProne;
    }

    /// <summary>
    /// 애니메이터에게 몸을 돌려준다 — 기절에서 깨어나는 유일한 문. 이미 <c>Animated</c>면 무동작. (#572)
    ///
    /// <b>블렌드 없이 즉시 돌아간다</b>(계획서 §2-6①(b)). 한 프레임 튀지만, 플레이어가 아직 못 고친
    /// "부활 시 큰 회전"(corpse-split §4)을 물려받지 않는다.
    ///
    /// <b>표현과 위치를 가른다.</b> 뼈·애니메이터는 전 피어가 되돌리고, NavMesh 재부착은 권위 피어만
    /// 한다 — 원격의 에이전트는 스폰 때부터 영구히 꺼져 있다(<c>NpcController.OnNetworkSpawn</c>).
    /// </summary>
    private void ExitRagdoll()
    {
        if (m_state == RagdollState.Animated)
            return;

        // ① 뼈를 애니메이터에게 돌려준다 — <b>키네마틱이 먼저다.</b> 동적인 채로 포즈를 쓰면
        //    다음 물리 스텝이 PhysX의 결과로 덮는다.
        m_rig.SetKinematic(true);

        // ⚠ <b>출발점은 지금 이 래그돌 자세다</b> — 아래 두 줄(바인드 포즈 복원·애니메이터 복귀)보다
        //    반드시 먼저 잡는다. 뒤로 밀면 이미 갈아끼워진 자세에서 출발해 블렌드가 아무 일도 안 한다.
        bool blending = m_blendSeconds > 0f && m_blend != null && m_blend.IsValid;
        if (blending)
            m_blend.Begin();

        // ⚠ <b>뼈 길이를 되돌린다</b> (§1-2). 물리가 관절을 늘려 놓은 localPosition은 애니메이터가
        //    고쳐 주지 않는다 — 애니메이터는 <b>회전만</b> 쓰기 때문이다. 리그가 하나뿐인 NPC에는
        //    플레이어의 RagdollPose.Copy 필터가 놓일 자리가 없어, 안 되돌리면 기절할 때마다
        //    누적되다 2차·3차에서 사지가 늘어나며 바닥을 뚫는다.
        //
        // 블렌드가 이 복원까지 부드럽게 만든다 — 늘어난 뼈 길이가 한 프레임에 튀지 않고 섞여 돌아온다.
        m_rig.RestoreBindPose();


        if (m_animator != null)
            m_animator.enabled = true;

        m_rig.SetSkinsAlwaysVisible(false); // StopAnimator가 켠 것을 되돌린다

        m_state = RagdollState.Animated;
        m_blending = blending;
        m_stillTimer = 0f;
        m_elapsedInRagdoll = 0f;

        // ② 위치·에이전트는 권위 피어만
        if (HasMoveAuthority)
            ServerReattachToNavMesh();
    }

    /// <summary>
    /// 에이전트에게서 몸을 넘겨받는다 — 눕기 전에 <b>반드시</b>. (#572 3단계)
    ///
    /// <b>기절은 에이전트를 끄지 않는다.</b> 막아야 하는 것은 "에이전트가 매 프레임 트랜스폼을
    /// NavMesh 위로 써 버리는 것"뿐인데, 그건 <c>updatePosition</c>이 하는 일이라 그것만 떼면 된다.
    /// 통째로 끄면 <c>enabled == false</c>가 되어 <c>isStopped</c>·<c>SetDestination</c>이 예외를 던지고,
    /// <b>그 사이에 일어나는 상태 전이가 통째로 깨진다</b> — 실측으로 <c>NpcResistState.Exit</c>과
    /// <c>NpcEscortedState.Enter</c>가 각각 터졌다. 코드베이스의 전제가 <b>"전이 시점에 에이전트는
    /// 살아 있다"</b>이고(<c>NpcRopeDrag.StartRopeDrag</c> 주석), 기절 래그돌은 <b>산 NPC에</b>
    /// 얹히므로 그 전제 안에 있어야 한다.
    ///
    /// <b>사망은 반대로 통째로 끈다</b> — 시체는 NavMesh로 돌아가지 않고(#571), 죽은 몸에는 깨질
    /// 전이도 없다(상태 기계가 사망 이탈을 거부한다). 여기는 확인 사살이다:
    /// <c>NpcDeath.ServerEnterDead</c> ⑥이 이미 껐다.
    /// </summary>
    private void ReleaseAgentForRagdoll()
    {
        if (m_agent == null)
            return;

        if (m_owner.Death.IsDead)
        {
            if (m_agent.enabled)
                m_agent.enabled = false;
            return;
        }

        // 산 몸 — 켜 둔 채로 손만 뗀다. 되돌리는 것은 ExitRagdoll이다.
        m_agent.updatePosition = false;
        m_agent.updateRotation = false;
    }

    /// <summary>
    /// 깨어난 몸을 NavMesh에 다시 붙인다 — <b>서버(또는 오프라인) 전용.</b> (#572)
    /// <c>NpcKnockback.EndKnockback</c>이 넉백 비행에 대해 하는 일을 기절 래그돌에 대해 한다.
    ///
    /// <b>규칙은 "뗀 쪽이 되돌린다"다.</b> 위치 갱신을 뗀 것은
    /// <see cref="ReleaseAgentForRagdoll"/>이므로 되돌리는 것도 여기다 — 기절 해제
    /// (<c>NpcStun.ExitStun</c>)에 맡기면 <b>떼지도 않은 것을 되돌리는</b> 함수가 되고, 그쪽은 사망
    /// 경로에서도 불려 시체의 에이전트까지 되살릴 수 있다.
    /// </summary>
    private void ServerReattachToNavMesh()
    {
        if (m_agent == null)
            return;

        // ⚠ <b>플래그는 무조건 되돌린다 — 아래 가드보다 먼저다.</b> 이건 우리가 뗀 것이라 밧줄·넉백이
        // 에이전트를 쥐고 있든 말든 우리 몫이고, 떼어 둔 채 넘기면 그쪽이 나중에 에이전트를 켜도
        // 위치 갱신이 꺼진 채라 <b>그 NPC는 영영 걷지 못한다.</b>
        m_agent.updatePosition = true;
        m_agent.updateRotation = true;

        // 위치는 다른 구간이 쥐고 있으면 손대지 않는다 — 그쪽이 끝날 때 자기 자리에서 붙인다
        // (<c>NpcRopeDrag.StopRopeDrag</c> / <c>NpcKnockback.EndKnockback</c>).
        if (m_owner.Rope.IsRoped || m_owner.Knockback.IsKnockedBack)
            return;

        // ⚠ <b>실패해도 켠다.</b> 회수 안전망(<c>NpcController.TickNavMeshRecovery</c>)은
        // <c>enabled == false</c>인 구간을 <b>건너뛰므로</b>, 꺼 둔 채 두면 회수가 영영 오지 않는다.
        // 켜 두면 붙이지 못한 몸도 1초 뒤 더 넓은 반경으로 다시 시도된다 (#557).
        // 기절 경로에서는 애초에 켜져 있어 무동작이다 — 멱등하게 둔다.
        m_agent.enabled = true;

        // 통행 마스크로 착지점을 찾는다 — 못 가는 영역(Jail)에 Warp되면 경로가 안 잡혀 고착된다 (#415)
        Vector3 sampleFrom = transform.position;
        bool sampled = NavMesh.SamplePosition(
            sampleFrom,
            out NavMeshHit ground,
            k_navMeshSampleDistance,
            m_agent.areaMask
        );
        if (sampled)
            m_agent.Warp(ground.position);

        // 기상 — <b>샘플 거리가 루트 높이의 결과다.</b> 루트가 골반 높이에 있으면 NavMesh를 위에서
        // 찾게 되어 거리가 길고, 바닥에 있으면 짧아야 한다. 반경(k_navMeshSampleDistance = 2m)에
        // 가까워지면 실패가 나기 시작한다.
        if (m_logRootFollow)
        {
            Debug.Log(
                $"[래그돌 기상] {name} 샘플={(sampled ? "성공" : "실패")} "
                    + $"거리={(sampled ? Vector3.Distance(sampleFrom, ground.position).ToString("F3") : "-")} "
                    + $"반경={k_navMeshSampleDistance:F1} | onNavMesh={m_agent.isOnNavMesh}",
                this
            );
        }

        if (!m_agent.isOnNavMesh)
        {
            Debug.LogWarning(
                "NpcRagdoll: 기절에서 깨어난 자리를 NavMesh에 붙이지 못했다 — 회수 대기: "
                    + $"{name} @{transform.position.ToString("F1")}",
                this
            );
        }
    }

    // ---- 루트 추종 (서버 전용) ----

    /// <summary>
    /// 루트를 시체 밑으로 끌고 간다 — <b>서버(또는 오프라인) 전용.</b>
    ///
    /// <b>왜 필요한가.</b> 이걸 안 하면 루트는 죽은 자리에 그대로 남고 시체만 굴러간다. 루트는
    /// 스캔·이름표·상호작용 콜라이더가 매달린 자리이자 <b>NetworkTransform이 복제하는 유일한 값</b>이라,
    /// 루트가 시체를 대표하지 못하면 원격 피어는 시체가 어디 있는지 알 방법이 없다.
    ///
    /// 스윕을 쓰지 않고 위치를 직접 대입한다 — 대리값은 지형을 존중할 이유가 없다. 에이전트가
    /// 꺼져 있으므로(EnterRagdoll) 대입이 곧 이동이다.
    ///
    /// <b>yaw는 건드리지 않는다.</b> 플레이어는 기상 클립이 "루트 전방을 향해 누워 있다"를 전제해
    /// 루트를 몸 방향으로 돌려야 했지만(FollowBodyYaw), 시체는 일어나지 않으므로 그 이유가 없다.
    /// 돌리면 오히려 손해다 — 리지드바디가 없는 뼈(Neck·손·발)만 계층을 따라 돌아 목이 비틀린다.
    ///
    /// <b>얼린 뒤에는 돌지 않는다</b> (#571 권위 반전). 무너지는 동안에만 뼈가 주인이고, 정착해
    /// 얼고 나면 반대로 루트가 뼈를 끈다. 밧줄로 끌 때 몸만 가고 루트가 남던 문제(실측: 시체 8.26m,
    /// 루트 0.00m)는 <b>밧줄이 몸을 녹이기</b> 때문에 그대로 막힌다 — 끄는 동안은 항상 이 상태다.
    /// </summary>
    private void TickRootFollow()
    {
        if (!HasMoveAuthority || m_rig.Hips == null)
            return;

        // ⚠ <b>루트를 옮기기 전에 뼈를 잡아 두고, 옮긴 뒤 되돌린다.</b>
        //
        // "동적 리지드바디는 부모 트랜스폼을 따라가지 않는다"는 이 파일의 전제는 <b>다음 물리
        // 스텝이 포즈를 되써 준 뒤부터</b> 참이다. PhysX가 월드 포즈를 써 넣으면 Unity는 그것을
        // <b>그 시점의 부모 기준 로컬</b>로 저장하므로, 그 뒤 Update에서 부모를 옮기면 자식의
        // 월드는 부모 × 로컬로 <b>같이 끌려간다.</b> 렌더는 Update·LateUpdate 다음이라 그 어긋난
        // 몸이 한 프레임 그려지고, 다음 FixedUpdate에서 되쓰이며 툭 내려온다.
        //
        // <b>진입 프레임이 그 한 번이다.</b> 평소 이 함수는 잔차 몇 cm를 따라가지만 진입 때는
        // 루트가 발밑(y≈0)에서 골반(y≈0.9)으로 <b>한 방에 뛴다</b> — 그 프레임에 몸 전체가
        // 골반 높이만큼 떠서 그려진다. 물리를 거치지 않으므로 겹침 탈출 속도 상한
        // (<see cref="RagdollRig"/>의 k_maxDepenetrationVelocity)으로는 줄지 않는다.
        //
        // <b><see cref="ServerFreezeInPlace"/> ①④와 같은 패턴이다</b> — 저쪽은 얼리는 순간의
        // 같은 왕복(실측 14.6cm)을 이 방식으로 잡았고, 진입 쪽에만 빠져 있었다.
        //
        // 되돌리는 대입은 <b>렌더 전용</b>이다: 이 프로젝트는 <c>m_AutoSyncTransforms = 0</c>이라
        // 트랜스폼에 쓴 값이 액터로 넘어가지 않는다. PhysX의 포즈는 손대지 않은 채, 화면에
        // 그려지는 자리만 제자리로 돌린다.
        m_rig.CapturePose();

        Vector3 target = m_rig.Hips.position;

        // <b>몸이 바닥에 있으면 루트 높이는 지면이 준다</b> — 골반 높이를 쓰는 것은 <b>공중에 있는
        // 동안만</b>이다. <see cref="PlayerRagdoll.TickCapsuleFollow"/>의 <c>Settled</c> 분기와 같은
        // 처리이고, NPC만 안 받고 있었다.
        //
        // <b>얼릴 때의 낙차를 미리 없애는 것이 목적이다.</b> <see cref="ServerFreezeInPlace"/>는
        // 루트를 골반 밑 지면으로 내리는데, 그때까지 루트가 골반 높이(누운 시체 약 0.2m)에 있었으면
        // 그 0.2m가 <b>한 프레임에</b> 떨어진다. 서버는 ①④(CapturePose/RestoreCapturedPose)가 감싸
        // 무사하지만 <b>클라에는 그 감싸기가 없다</b> — 클라의 골반은 키네마틱이라(비권위 피어,
        // <see cref="ReleaseBonesToPhysics"/>) 루트의 자식으로서 <b>따라 내려가고</b>, 골반 NT가
        // 자기 월드 위치를 다시 쓸 때까지 몸 전체가 가라앉아 보인다. 미리 지면에 있으면 낙차가 0이라
        // 끌 것이 애초에 없다.
        //
        // ⚠ <b>여기 "골반 높이를 그대로 쓴다"가 있었다.</b> 근거는 "매 프레임 지면을 찾아 루트
        // 높이를 고치면 원격에서 그 오차가 곧 몸의 높이 오차가 된다(정렬이 루트를 따라가므로)"였는데,
        // <b>그 정렬(<see cref="TickAlignBonesToRoot"/>)은 지금 배선에서 한 번도 돌지 않는다</b> —
        // 골반이 직접 복제되면서(#572) 조건에서 걸러진다. 몸은 루트가 아니라 <b>스트리밍된 골반</b>에
        // 매달려 있으므로 루트의 높이 오차가 몸으로 전파될 경로가 없어졌다.
        //
        // <b>지면 판정은 <see cref="ServerFreezeInPlace"/>와 같은 것을 쓴다</b>(<see cref="GroundUnder"/>
        // 와 같은 <see cref="TryGroundUnder"/>) — 두 곳이 다른 높이를 내면 얼리는 순간 그 차이가
        // 그대로 낙차로 남아 이 처리가 무의미해진다.
        bool haveGround = TryGroundUnder(target, out Vector3 ground);
        float hipsHeight = haveGround ? target.y - ground.y : float.NaN;
        bool snapped = haveGround && hipsHeight <= k_groundedHipsHeight;

        float followY = target.y; // 스냅이 없었다면 갔을 자리 — 경계 로그가 이것과 실제를 가른다
        if (snapped)
            target.y = ground.y;

        float rootYBefore = transform.position.y;
        transform.position = target;

        m_rig.RestoreCapturedPose();

        if (m_logRootFollow)
        {
            LogSnapEdge(hipsHeight, snapped, rootYBefore, followY, target.y);
            TickRootFollowLog(hipsHeight, snapped);
        }
    }

    // ---- 진단 (m_logRootFollow) ----
    //
    // 네 지점을 각각 <b>불변식 하나씩</b>으로 잰다. 눈으로 "정상인 것 같다"와 숫자로 "0.000이다"를
    // 가르는 것이 목적이라, 한 줄에 판정까지 붙여 둔다(⚠ 표시). 확정되면 통째로 지운다.

    private float m_rootFollowLogTimer;

    // 클라 침하 탐지용 직전 프레임 값 — 에피소드마다 리셋한다(EnterRagdoll). 안 하면 지난 사망의
    // 값과 비교해 첫 프레임에 가짜 점프가 찍힌다.
    private float m_prevClientRootY;
    private float m_prevClientHipsY;
    private bool m_haveClientSample;
    private int m_dipTraceFrames;

    // 스냅 경계를 넘은 프레임을 가려내기 위한 직전 값 — 위와 같이 에피소드마다 리셋한다.
    private bool m_prevSnapped;
    private bool m_haveSnapSample;

    /// <summary>
    /// <b>경계를 넘는 그 프레임만</b> 찍는다 — 권위 피어 전용(<see cref="TickRootFollow"/> 안).
    ///
    /// <b>1초에 한 줄인 <c>[래그돌 루트추종]</c>으로는 이걸 못 잡는다.</b> 저쪽은 "지금 루트 높이의
    /// 주인이 누구인가"를 훑는 로그라 <b>바뀌는 순간</b>을 놓친다 — 골반이 임계값을 지나는 것은
    /// 한 프레임짜리 사건이다.
    ///
    /// 재려는 것은 하나다: <b>루트가 이 프레임에 얼마나 뛰었나.</b> 그 값이 그대로 클라로
    /// 스트리밍되고, 클라의 키네마틱 골반이 계층을 따라 그만큼 끌려 내려간다 — 이 PR이 얼림
    /// 시점에서 없앤 것과 <b>같은 낙차</b>다(<see cref="ServerFreezeInPlace"/>의 <c>루트낙차</c>).
    ///
    /// ⚠ <b>루트가 움직인 것 자체는 증거가 아니다.</b> 무너지는 몸을 따라가느라 루트는 매 프레임
    /// 움직인다. 그래서 <b>스냅이 없었다면 갔을 자리</b>(<paramref name="followY"/>)를 나란히 찍어
    /// 정상적인 낙하와 <b>경계가 만든 계단</b>을 가른다 — 둘의 차이가 곧 불연속의 크기다.
    /// </summary>
    private void LogSnapEdge(
        float hipsHeight,
        bool snapped,
        float rootYBefore,
        float followY,
        float rootY
    )
    {
        if (m_haveSnapSample && snapped != m_prevSnapped)
        {
            float step = rootY - rootYBefore; // 실제로 스트리밍된 한 프레임 이동
            float continuous = followY - rootYBefore; // 예전 동작(골반 그대로 따라가기)이었다면
            float discontinuity = step - continuous;

            Debug.Log(
                $"[래그돌 경계] {name} 스냅 {(m_prevSnapped ? "Y→N" : "N→Y")} "
                    + $"| 골반높이={hipsHeight:F3} 임계={k_groundedHipsHeight:F2} "
                    + $"| 루트Y {rootYBefore:F3} → {rootY:F3} "
                    + $"(Δ={step:F3}, 연속이었다면 Δ={continuous:F3}) 계단={discontinuity:F3}"
                    + $"{(Mathf.Abs(discontinuity) > 0.02f ? " ⚠클라가 이만큼 끌려간다" : " (무시할 크기)")}",
                this
            );
        }

        m_prevSnapped = snapped;
        m_haveSnapSample = true;
    }

    /// <summary>
    /// <b>클라가 실제로 무엇을 보는지</b> 잰다 — 원격 전용, <c>LateUpdate</c> 맨 끝(다음이 렌더).
    ///
    /// 호스트 쪽 <c>[래그돌 얼림]</c>은 <b>원인</b>(루트가 뛰었나)만 재고, 이 침하는 <b>클라의 렌더
    /// 결과</b>다. 둘은 다른 것이라 따로 재야 한다.
    ///
    /// 세 값이 각각 다른 것을 가른다:
    /// <list type="bullet">
    ///   <item><c>루트Δ</c> — 스트림된 루트가 이 프레임에 뛰었는가. 이것이 침하의 방아쇠다.</item>
    ///   <item><c>골반Δ</c> — 화면에 그려지는 몸이 이 프레임에 내려갔는가. <b>이게 증상 그 자체다.</b></item>
    ///   <item><c>렌더−물리</c> — 골반의 트랜스폼과 PhysX 액터 포즈의 차이. 0이 아니면 몸이
    ///   <b>계층에 끌려간 것</b>이고(물리는 안 움직였는데 트랜스폼만 이동), 그것이 이 버그의 서명이다.</item>
    /// </list>
    ///
    /// 조용할 때는 아무것도 찍지 않는다 — 뛰거나 내려간 프레임과 그 뒤 3프레임(회복 구간)만 남긴다.
    /// </summary>
    private void TickClientDipProbe()
    {
        if (m_rig == null || m_rig.Hips == null)
            return;

        float rootY = transform.position.y;
        float hipsY = m_rig.Hips.position.y; // 렌더 트랜스폼 = 화면에 그려지는 자리
        float hipsRbY = m_rig.HipsBody != null ? m_rig.HipsBody.position.y : float.NaN; // PhysX 액터

        if (m_haveClientSample)
        {
            float rootDelta = rootY - m_prevClientRootY;
            float hipsDelta = hipsY - m_prevClientHipsY;

            // ⚠ <b>루트가 움직인 것을 방아쇠로 쓰면 안 된다.</b> 끌려가는 시체는 루트가 매 프레임
            // 움직이므로(2m/s면 프레임당 0.033m) 로그가 도배된다. 재려는 것은 "루트가 움직였나"가
            // 아니라 <b>"화면의 몸이 내려갔나"</b>다 — 방아쇠는 그쪽에 둔다.
            //
            // 둘 중 하나면 찍는다:
            //  · <c>골반Δ</c>가 아래로 튐 — 증상 그 자체. 바닥을 따라 끌리는 수평 이동은 걸리지 않는다
            //  · <c>렌더−물리</c>가 벌어짐 — <b>계층에 끌려간 것의 서명</b>. 물리는 가만있는데
            //    트랜스폼만 움직였다는 뜻이라, 정상 동작에서는 0에 붙어 있어야 한다
            bool hipsDipped = hipsDelta < -0.02f;
            bool draggedByHierarchy =
                !float.IsNaN(hipsRbY) && Mathf.Abs(hipsY - hipsRbY) > 0.02f;

            if (hipsDipped || draggedByHierarchy || m_dipTraceFrames > 0)
            {
                Debug.Log(
                    $"[래그돌 클라] {name} 루트Δ={rootDelta:F3} 골반Δ={hipsDelta:F3} "
                        + $"| 루트Y={rootY:F3} 골반렌더Y={hipsY:F3} 골반물리Y={hipsRbY:F3} "
                        + $"렌더-물리={hipsY - hipsRbY:F3} "
                        + $"| 상태={m_state} 골반키네마틱="
                        + $"{(m_rig.HipsBody != null ? m_rig.HipsBody.isKinematic.ToString() : "?")}"
                        + $"{(hipsDipped ? " ⚠골반하강" : "")}"
                        + $"{(draggedByHierarchy ? " ⚠계층에끌려감(물리는안움직였다)" : "")}",
                    this
                );

                m_dipTraceFrames = hipsDipped || draggedByHierarchy ? 3 : m_dipTraceFrames - 1;
            }
        }

        m_prevClientRootY = rootY;
        m_prevClientHipsY = hipsY;
        m_haveClientSample = true;
    }

    // 시체 밑 여유 — <b>최저뼈Y − 바닥Y.</b> 음수면 몸이 바닥을 파고들었다.
    // 수감 도착의 기준값이 +0.005다(ServerPlaceCorpse 주석의 실측).
    private float LowestBoneClearance()
    {
        if (!TryGroundUnder(m_rig.Hips.position, out Vector3 ground))
            return float.NaN;

        return m_rig.LowestBoneY - ground.y;
    }

    // 루트 높이의 주인이 지금 누구인가 — 1초에 한 줄.
    //
    // 비행 중이면 <c>스냅=N</c>이고 <c>루트↔골반Y=0.000</c>이어야 한다(루트가 골반을 3차원으로
    // 따라간다). 바닥에 있으면 <c>스냅=Y</c>이고 루트↔골반Y가 음수(루트가 골반보다 아래 = 지면)다.
    // <b>임계값 k_groundedHipsHeight가 맞는지 재는 유일한 계측이다</b> — 날아가는 시체가 스냅=Y로
    // 찍히면 임계값이 너무 크고, 바닥에 누운 시체가 스냅=N이면 너무 작다.
    private void TickRootFollowLog(float hipsHeight, bool snapped)
    {
        m_rootFollowLogTimer += Time.deltaTime;
        if (m_rootFollowLogTimer < 1f)
            return;

        m_rootFollowLogTimer = 0f;

        float rootToHipsY = transform.position.y - m_rig.Hips.position.y;
        bool carried = m_rope != null && m_rope.IsBeingCarried;

        Debug.Log(
            $"[래그돌 루트추종] {name} 권한={HasMoveAuthority} 끌림={carried} "
                + $"| 골반높이={hipsHeight:F3} 임계={k_groundedHipsHeight:F2} 스냅={(snapped ? "Y" : "N")} "
                + $"| 루트↔골반Y={rootToHipsY:F3}"
                + $"{(!snapped && Mathf.Abs(rootToHipsY) > 0.01f ? " ⚠비행인데 루트가 골반과 어긋났다" : "")}"
                + $" | 여유={LowestBoneClearance():F3} 평균속도={m_rig.AverageSpeed:F2}",
            this
        );
    }

    private bool HasGroundUnderHips() => TryGroundUnder(m_rig.Hips.position, out _);

    // ---- 원격 정렬 ----

    /// <summary>
    /// 원격 피어의 시체를 스트리밍된 루트에 맞춘다 — <b>원격(= 서버 아닌 전원) 전용.</b>
    ///
    /// 서버의 루트가 골반을 따라오므로(<see cref="TickRootFollow"/>) <b>스트리밍된 루트가 곧 서버
    /// 골반의 위치다</b> — 뼈를 따로 동기화하지 않고도 원격이 서버의 궤적을 받는다.
    ///
    /// 리그 루트 오프셋으로는 못 고친다 — <b>동적 리지드바디는 부모 트랜스폼을 따르지 않는다.</b>
    /// 그래서 뼈의 <c>position</c>에 직접 델타를 더한다(<see cref="RagdollRig.TranslateBy"/>).
    ///
    /// ⚠ <b>동력이 아니라 표류 방지다.</b> 몸을 움직이는 것은 각 피어의 로컬 물리이고, 여기서 하는
    /// 일은 그 결과가 루트에서 서서히 벗어나는 것을 막는 것뿐이다. 상한을 올려 "끌어오게" 만들려던
    /// 시도가 플레이어 쪽에서 발산으로 끝났다(506 §9-16).
    /// </summary>
    private void TickAlignBonesToRoot()
    {
        bool grounded = HasGroundUnderHips();

        Vector3 delta = transform.position - m_rig.Hips.position;

        // 착지 후에만 높이를 뺀다 — 그때는 각 피어의 지형 충돌이 높이의 주인이고 같은 지형이라
        // 편차가 작다. 반대로 <b>공중에서는 3차원으로 맞춘다</b>: 높이를 정해 줄 접촉이 없어
        // 서버 골반의 고도를 받아야 원격도 같은 궤적을 그린다.
        if (grounded)
            delta.y = 0f;

        float distance = delta.magnitude;
        if (distance < 1e-4f)
            return;

        float maxStep = (grounded ? m_alignPullSpeed : m_flightAlignPullSpeed) * Time.deltaTime;

        // 잔차가 임계를 넘으면 스냅한다 — 이미 눈에 띄게 틀렸으므로 포즈 보존이 의미가 없고,
        // 느린 상한으로는 영구히 못 따라잡는다. 안전망이라 정상 동작에서는 걸리지 않아야 한다.
        if (distance > m_alignSnapDistance)
            maxStep = distance;
        if (distance > maxStep)
            delta *= maxStep / distance;

        // <b>몸 전체를 같은 델타로 옮긴다.</b> 포즈는 이미 이 피어의 로컬 물리가 만들고 있고,
        // 여기서 하는 일은 그 궤적을 서버 것에 맞추는 것뿐이다. 뼈마다 다르게 옮기면 포즈가 깨진다.
        // (골반만 옮겨 흐느적임을 만들려던 시도는 질량비 때문에 실패한다 — 506 §10-5)
        m_rig.TranslateBy(ClampByWall(delta));
    }

    private Vector3 ClampByWall(Vector3 delta)
    {
        const float k_skin = 0.02f; // 벽에 딱 붙이지 않고 살짝 띄운다 — 겹치면 탈출 임펄스가 생긴다

        Vector3 from = m_rig.Hips.position;
        if (
            !Physics.Linecast(
                from,
                from + delta,
                out RaycastHit hit,
                m_groundMask,
                QueryTriggerInteraction.Ignore
            )
        )
            return delta;

        return delta.normalized * Mathf.Max(0f, hit.distance - k_skin);
    }

    // ---- 정착 = 얼림 (#571 권위 반전) ----

    /// <summary>
    /// 지금 자세 그대로 얼린다 — <b>서버(또는 오프라인) 전용.</b> 정착 판정과 유치장 배치가 부른다.
    ///
    /// <b>"정착 = 물리를 계속 돌리되 그대로 두기"에서 "정착 = 자세를 확정하고 멈추기"로 바뀌었다.</b>
    /// 얼리면 세 가지가 한꺼번에 끝난다: 바닥에서 비벼질 접촉이 사라지고, 뼈가 루트의 자식으로
    /// 되돌아와 <b>루트만 옮기면 몸이 따라오고</b>, 자세가 상수가 되어 원격에 1회만 보내면 된다.
    ///
    /// 순서를 지키지 않으면 몸이 튄다. <b>동적 뼈는 루트를 따라가지 않고 키네마틱 뼈는 따라가므로</b>,
    /// 옮기는 일을 <b>전환 앞</b>에 두는 것이 요점이다:
    /// <list type="number">
    ///   <item>전 뼈의 월드 포즈를 캡처</item>
    ///   <item><b>루트를 골반 밑 지면으로 이동</b> — 아직 동적이라 뼈는 그대로 있다</item>
    ///   <item>전 rb를 키네마틱으로 전환 — 이 순간부터 뼈가 루트에 매인다</item>
    ///   <item>캡처한 월드 포즈를 다시 적용 (안전망 — 보통 무동작)</item>
    /// </list>
    ///
    /// ⚠ ②와 ③이 뒤바뀌어 있었다 (#572 후속). 그러면 <b>몸 전체가 루트를 따라 내려갔다가 ④에서
    /// 도로 올라온다</b> — 같은 프레임 안의 왕복이지만, 내려놓을 때마다 한 프레임 뜨는 것처럼
    /// 보이는 원인이었다. 실측으로 매 정착마다 14.6cm를 오르내리고 있었다.
    ///
    /// 예전에는 ⑤로 <b>다시 물리에 풀어 줬다</b>(<c>RestToPhysics</c> — 플레이어 쪽에는 아직 남아 있다).
    /// 그 근거는 "골반을 붙들면 몸이
    /// 찢어진다"(506 §9-7)였는데, 그건 <b>일부만</b> 키네마틱으로 붙들 때의 이야기다 — 전부 한꺼번에
    /// 얼리면 서로 당기는 관절이 없어 자세가 그대로 굳는다.
    /// </summary>
    private void ServerFreezeInPlace()
    {
        if (!HasMoveAuthority)
            return;

        StopAnimator(); // 무너지지 않은 몸을 그대로 얼리는 경로(유치장 배치)가 있다
        m_rig.CapturePose();
        Vector3 landedHips = m_rig.Hips.position;

        // ⚠ <b>이 낙차가 B의 판정 기준이다.</b> 루트가 여기서 뛰면 그 점프가 클라로 나가고, 클라의
        // 키네마틱 골반이 계층을 따라 끌려 내려간다(그쪽에는 아래 ①④ 감싸기가 없다).
        // TickRootFollow가 루트를 미리 지면에 놓아 두면 0.000이어야 한다.
        float rootYBeforeFreeze = transform.position.y;

        // ⚠ <b>루트를 먼저 옮기고 나서 얼린다</b> (#572 후속). 순서가 뒤집혀 있었다.
        //
        // 예전에는 얼린 다음 루트를 옮겼는데, 그러면 <b>이미 키네마틱이 된 뼈가 루트를 따라
        // 14.6cm 내려갔다가</b> 아래 <c>RestoreCapturedPose</c>로 도로 올라왔다 — 같은 프레임
        // 안이지만 <b>몸 전체를 내렸다 올리는 왕복</b>이 실제로 들어 있었고, 내려놓을 때마다
        // 한 프레임 뜨는 것처럼 보이는 원인이었다.
        //
        // <b>동적 리지드바디는 부모 트랜스폼을 따라가지 않는다</b> — 이 클래스가 곳곳에서 기대는
        // 바로 그 성질이다. 그래서 얼리기 <b>전</b>에 옮기면 뼈는 아무 데도 안 간다.
        transform.position = GroundUnder(landedHips);

        m_rig.SetKinematic(true);

        // 이제는 안전망이다 — 뼈가 움직이지 않았으므로 보통 무동작이다. 남겨 두는 이유는 골반보다
        // <b>위</b>에 있는 무관절 트랜스폼(리그 루트)이 계층을 따라 내려가기 때문이다: 그 밑의 뼈는
        // 월드 포즈를 쥔 리지드바디라 영향이 없지만, 한 줄로 못박아 두는 편이 안전하다.
        m_rig.RestoreCapturedPose();

        m_state = RagdollState.Frozen;

        if (m_logRootFollow)
        {
            float drop = transform.position.y - rootYBeforeFreeze;
            Debug.Log(
                $"[래그돌 얼림] {name} 루트낙차={drop:F3}"
                    + $"{(Mathf.Abs(drop) > 0.02f ? " ⚠클라가 이만큼 끌려 내려간다" : " (클라 침하 없음)")}"
                    + $" | 골반높이={landedHips.y - transform.position.y:F3} 여유={LowestBoneClearance():F3} "
                    + $"평균속도={m_rig.AverageSpeed:F2}",
                this
            );
        }

        ServerBroadcastPose();
    }

    // 얼린 자세를 원격에 1회 보낸다 — 세션이 아니면(오프라인 Play) 보낼 곳이 없다.
    // 배선은 NpcDeath가 쥔다: 이 컴포넌트는 NetworkBehaviour가 아니다(클래스 주석).
    private void ServerBroadcastPose()
    {
        if (m_owner == null || !m_owner.IsSpawned)
            return;

        if (m_poseBuffer == null || m_poseBuffer.Length != m_rig.BoneCount)
            m_poseBuffer = new Quaternion[m_rig.BoneCount];

        if (m_rig.CaptureLocalPose(m_poseBuffer, out Vector3 hipsLocal))
            m_owner.Death.ServerSendFrozenPose(m_poseBuffer, hipsLocal);
    }

    // 보낼 자세를 담는 버퍼 — 시체당 한 번 쓰지만 매번 새로 할당할 이유도 없다.
    private Quaternion[] m_poseBuffer;

    // 정착 정렬용 지면 — 여기까지 왔다면 보통 지면이 있다(Update가 없으면 정착을 미룬다).
    // 못 찾는 경우는 맵 밖으로 떨어진 시체뿐이고, 그때는 골반 높이를 그대로 쓴다.
    //
    // ⚠ 플레이어의 CapsuleBottomOffset 보정이 여기 없다 — 그건 CharacterController 캡슐 밑면이
    // 루트 원점보다 위에 있어서 필요했던 것이고, NPC 루트 원점은 발밑이라 지면 점이 곧 루트다.
    private Vector3 GroundUnder(Vector3 hipsPosition)
    {
        bool hitGround = TryGroundUnder(hipsPosition, out Vector3 point);
        return hitGround ? point : hipsPosition;
    }

    // 골반 밑 지면 탐색 — 정착 자격 판정과 정착 정렬이 공유한다.
    // 탐색 거리를 짧게 잡는 것이 중요하다: 길게 쏘면 얇은 실내 바닥을 뚫고 아래층을 찾아내
    // 시체가 정착하는 순간 한 층 밑으로 순간이동한다.
    private bool TryGroundUnder(Vector3 hipsPosition, out Vector3 point)
    {
        const float k_probeLift = 0.5f; // 골반이 바닥에 파묻혀 있어도 레이가 지면 위에서 출발하게

        bool hitGround = Physics.Raycast(
            hipsPosition + Vector3.up * k_probeLift,
            Vector3.down,
            out RaycastHit hit,
            k_probeLift + m_groundProbeDistance,
            m_groundMask,
            QueryTriggerInteraction.Ignore
        );

        point = hitGround ? hit.point : hipsPosition;
        return hitGround;
    }
}
