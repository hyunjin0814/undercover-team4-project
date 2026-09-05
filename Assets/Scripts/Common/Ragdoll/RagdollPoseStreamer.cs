using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 래그돌 자세를 <b>전 뼈 통째로</b> 원격에 흘려보낸다 — 권위 피어가 굴린 물리를 나머지가 재생한다.
/// (계획서 <c>docs/728-ragdoll-pose-streaming.md</c> 1단계)
///
/// <b>기존 구조의 불변식 1을 뒤집는 부품이다.</b> 지금까지는 각 피어가 자기 물리를 굴리고 궤적만
/// 골반 하나로 받았다(<c>docs/ragdoll.md</c> 불변식 1). 그래서 피어마다 몸이 갈렸고, 그 차이를
/// 좁히는 보정·스냅·속도캡이 층층이 쌓였다. 이 부품은 그 전제를 바꾼다 — <b>시뮬레이션은 하나뿐이고
/// 원격은 결과만 받는다.</b> 원격의 뼈에는 물리가 돌지 않으므로 <b>위반될 관절이 원리적으로 없다.</b>
///
/// <b>이 부품은 물리를 모른다.</b> 뼈를 키네마틱으로 두는 것도, 언제 래그돌이 되는지도 소유자
/// (<c>NpcRagdoll</c>·<c>PlayerRagdoll</c>)가 정한다. 여기가 아는 것은 <b>"지금 보낼 자세"와
/// "받은 자세를 입히는 법"</b>뿐이다. 반대쪽 짝인 <see cref="RagdollRig"/>가 네트워크를 모르는 것과
/// 같은 선이다.
///
/// <b>골반은 처음부터 끝까지 <u>월드</u>다 — 국면에 따라 갈리지 않는다.</b>
///
/// 루트 NetworkTransform과 완전히 독립이라 두 스트림의 지연 차가 오차로 새지 않는다. 로컬로 보내면
/// 몸이 루트 보간값 위에 얹혀 실측 0.25m까지 벌어지던 <c>루트↔골반수평</c> 항이 되살아난다.
///
/// ⚠ <b>예전에는 정착에서 로컬로 갈아탔다 — 그 이음새를 없앴다.</b> 목적은 "정착한 시체는 루트를
/// 옮기면 딸려온다"였는데, 원격의 루트가 호스트와 조금만 달라도 그 차이가 통째로 <b>정착 순간의
/// 점프</b>가 됐다. 게다가 자세는 신뢰 RPC로 즉시·루트는 NT 보간으로 늦게 도착해 <b>두 값의 동시
/// 도착이 구조적으로 불가능</b>했다. 지금은 순간이동 쪽이 뼈를 직접 옮기고 자세를 다시 쏜다
/// (<c>NpcRagdoll.ServerPlaceCorpse</c>).
///
/// <b>그래서 정착 패킷을 받아도 화면은 변하지 않는다.</b> 그것이 사양이다 — 정착은 "스트림이
/// 멈췄다"는 사실일 뿐이고, 몸은 이미 그 자세를 그리고 있다.
///
/// <b><c>NpcDeath</c>가 정착 자세를 1회 뿌리던 경로를 흡수했다.</b> 그쪽은 지웠고, 그 1회는
/// 이제 스트림의 <b>마지막 패킷</b>(<see cref="EndStreaming"/>)이다 — 자세가 가는 통로는 하나만 남긴다.
/// </summary>
// ⚠ <b>루트 추종보다 뒤에 돈다.</b> #759 수정으로 <c>PlayerRagdoll</c>·<c>NpcRagdoll</c>의 루트
// 추종이 <c>FixedUpdate</c>로 내려오면서 이 클래스의 캡처와 <b>같은 페이즈</b>가 됐다. 순서를 안
// 박으면 미지정이 되고, 루트 회전이 뼈의 <b>로컬</b> 값을 바꾸므로(캡처가 보내는 것이 그 로컬이다)
// 어느 쪽이 먼저 도느냐로 보내는 자세가 갈린다. 뒤에 두어 <b>이번 스텝의 루트</b>를 기준으로 뜬다.
[DefaultExecutionOrder(100)]
[DisallowMultipleComponent]
public class RagdollPoseStreamer : NetworkBehaviour
{
    /// <summary>
    /// 누가 이 시체의 자세를 정하는가 — <b>프리팹 값으로 못박는다.</b>
    ///
    /// 자동 감지에 기대지 않는 이유는 NGO의 <c>HasAuthority</c>가 클라이언트-서버 모드에서 항상
    /// <c>IsServer</c>이기 때문이다. NPC는 그게 맞지만 <b>플레이어 시체는 오너 권한</b>이라
    /// (루트 NetworkTransform의 <c>AuthorityMode</c>와 짝) 그대로 쓰면 조용히 틀린다.
    /// </summary>
    public enum PoseAuthority
    {
        Server, // NPC — 서버가 물리를 굴린다
        Owner,  // 플레이어 — 그 몸의 오너가 굴린다
    }

    // 원격이 들고 있을 스냅샷 수. 보간에 2개가 필요하고, 지터·재정렬에 쓸 여유가 조금 있으면 된다.
    // 크게 잡을 이유는 없다 — 오래된 스냅샷은 어차피 재생 시점보다 뒤라 쓸 일이 없고,
    // 버퍼가 길수록 화면이 그만큼 더 늦다.
    private const int k_maxSnapshots = 4;

    // 시퀀스 비교의 절반 지점 — ushort가 한 바퀴 돌아도 "더 새것"을 올바로 가른다.
    // (65535 다음이 0이므로 대소 비교를 그대로 쓰면 랩어라운드 순간 스트림이 통째로 막힌다)
    private const ushort k_sequenceHalfRange = 32768;

    [Header("권위")]
    [Tooltip("이 시체의 자세를 누가 정하는가 — NPC는 Server, 플레이어는 Owner.\n\n" +
             "⚠ 루트 NetworkTransform의 AuthorityMode와 <b>반드시 같아야</b> 한다. 어긋나면 " +
             "몸과 루트가 서로 다른 피어에서 계산돼 시체가 이름표를 두고 떠난다")]
    [SerializeField] private PoseAuthority m_authority = PoseAuthority.Server;

    [Header("송신")]
    [Tooltip("몇 번의 물리 스텝마다 한 번 보내는가 — 50Hz 기준 2면 25Hz, 4면 12.5Hz.\n\n" +
             "<b>대역폭의 유일한 1차 손잡이다</b>(계획서 §1-6). 실측으로 시체 1구당 원격 1인 기준 " +
             "<b>약 2KB/s</b>(25Hz · 페이로드 82B)이고, 서버 업링크는 여기에 <b>동시 시체 수 × 원격 " +
             "수</b>가 곱해진다. 예산이 빠듯하면 여기부터 올린다 — 잃는 것은 보간 지연뿐이고 " +
             "정착 자세는 그대로다")]
    [SerializeField] private int m_sendEveryFixedSteps = 2;

    [Tooltip("몇 번의 물리 스텝마다 <b>뼈 길이</b>를 한 번 보내는가 — 50Hz 기준 25면 2Hz. " +
             "<b>0이면 끈다</b>(정착 패킷만 나르던 예전 동작).\n\n" +
             "권위 쪽 뼈는 무너지는 동안 관절이 늘어나는데 원격은 물리를 안 굴려 바인드 그대로다. " +
             "예전에는 그 차를 <b>정착 패킷 한 번</b>으로 몰아 넘겼고, 원격은 그것을 한 프레임에 " +
             "통째로 입혀 <b>정착 순간 상체가 내려앉았다</b>(실측 3.9cm · 2026-09-02, 클라에서만 " +
             "보인다). 드리프트는 몇 초에 걸쳐 자라는 값이라 0.5초마다 갱신하면 보정 한 번이 1cm " +
             "미만으로 쪼개지고, 그 구간은 몸이 빨라 눈에 안 띈다.\n\n" +
             "<b>비용은 208B × 주기</b>(뼈 17 기준). 2Hz면 약 0.4KB/s로 시체 1구·원격 1인 기준 " +
             "2.1 → 2.5KB/s다. <b>자세 패킷(86B·25Hz)은 안 바뀐다</b> — 별도 RPC다")]
    [SerializeField] private int m_lengthEveryFixedSteps = 25;

    [Header("수신")]
    [Tooltip("원격이 얼마나 뒤처진 시점을 그리는가(초) — 송신 주기의 2배가 기본값이다.\n\n" +
             "이만큼 늦게 그려야 다음 스냅샷이 이미 도착해 있어 <b>보간할 두 점</b>이 생긴다. " +
             "짧으면 패킷 하나만 늦어도 재생이 끝점에 부딪혀 시체가 멈칫하고, 길면 그만큼 " +
             "화면이 늦는다")]
    [SerializeField] private float m_interpolationDelay = 0.08f;

    private RagdollRig m_rig;

    // ---- 송신 상태 (권위 피어) ----

    private bool m_streaming;
    private ushort m_sequence;
    private int m_stepsSinceSend;
    private int m_stepsSinceLengths;
    private Quaternion[] m_sendBuffer;  // 캡처용 — 매 스텝 새로 할당할 이유가 없다
    private Vector3[] m_lengthBuffer;   // 뼈 길이 — 정착·순간이동 패킷 + m_lengthEveryFixedSteps 주기
    private uint[] m_packedBuffer;      // 실제로 선에 실리는 것 — 쿼터니언당 4바이트
    private Quaternion[] m_unpackBuffer; // 수신 쪽 — 푸는 자리

    // ---- 수신 상태 (원격) ----

    // 도착한 자세들 — 0이 가장 오래됐다. 링이 아니라 밀어내기인 것은 길이가 4라 옮기는 비용이
    // 인덱스 산술보다 싸고, "0이 가장 오래됐다"가 보간 코드를 훨씬 읽기 쉽게 만들기 때문이다.
    private Snapshot[] m_snapshots;
    private int m_snapshotCount;

    private ushort m_newestSequence;
    private bool m_haveSequence;

    // 스트림이 이 몸을 쥐고 있는가 — 참인 동안 원격은 매 프레임 자세를 대입한다.
    // 마지막(정착) 패킷을 받으면 거짓이 되고, 그때부터 몸은 루트 계층이 옮긴다.
    private bool m_streamDriven;

    private Quaternion[] m_applyBuffer; // 두 스냅샷을 섞어 담는 자리

    // ---- 국면 추적 ----
    //
    // <b>둘을 가르는 이유는 <see cref="IsAwaitingFirstPose"/>에 적혀 있다</b> — "아직 안 왔다"와
    // "다 받고 끝났다"를 하나로 물으면 정착 자세가 진입 자세로 덮인다.
    private bool m_expectingStream; // 전 피어 — 지금 자세가 흘러야 하는 국면인가
    private bool m_hasReceivedPose;    // 원격 — 이번 국면에 한 개라도 받았는가
    private bool m_warnedBoneMismatch; // 배선 불일치 경고를 한 번만 내기 위해

    // 기상으로 재생이 끝났다 — 그 뒤 도착한 스냅샷을 버리는 래치다. 애니메이터가 몸을 되받은
    // 뒤에 자세가 들어오면 그쪽과 매 프레임 싸운다.
    //
    // ⚠ <b>정착은 여기 해당하지 않는다.</b> 정착 패킷은 좌표계가 스트림과 같고 번호도 달고 오므로,
    // 뒤늦게 온 언리라이어블 스냅샷은 시퀀스 가드가 알아서 버린다 — 래치가 필요 없다.
    private bool m_streamEnded;

    // 뼈 길이 전용 시퀀스 가드. <b>자세의 m_newestSequence와 따로 두는 것이 요점이다</b> — 둘은
    // 같은 카운터에서 번호를 받지만 주기가 달라(25Hz vs 2Hz), 자세로 갱신하면 방금 보낸 길이가
    // 곧바로 "옛것"이 되어 통째로 버려진다. 여기를 올리는 것은 <b>길이를 실제로 입힌 패킷</b>뿐이다:
    // 주기 길이 · 정착 · 순간이동. 그래서 정착 뒤에 늦게 도착한 주기 길이도 자동으로 버려진다.
    private ushort m_newestLengthSequence;
    private bool m_haveLengthSequence;

    private struct Snapshot
    {
        public float Time;         // 받은 시각(로컬). ⚠ 서버 시각이 아니다 — 아래 TickApply 주석
        public Vector3 HipsWorld;
        public Quaternion[] Rotations;
    }

    // ---- 질의 ----

    /// <summary>
    /// 이 피어가 자세를 정하는 쪽인가 — 물리를 굴리고 보내는 쪽. <b>세션이 아니면 항상 참이다</b>
    /// (오프라인 Play에서는 자기가 유일한 피어다).
    ///
    /// 바깥에 열지 않는다 — 소유자는 자기 권위(<c>HasMoveAuthority</c>)로 판단하고, 이쪽은
    /// 송수신 게이트가 스스로 삼킨다(<see cref="BeginStreaming"/> 주석).
    /// </summary>
    private bool IsPoseAuthority
    {
        get
        {
            if (!IsSpawned)
                return true;

            return m_authority == PoseAuthority.Server ? IsServer : IsOwner;
        }
    }

    /// <summary>
    /// 지금 스트림이 이 몸의 자세를 쥐고 있는가 — 원격에서만 참이 된다.
    /// 소유자가 "내가 물리로 건드려도 되는가"를 묻는 자리다.
    /// </summary>
    public bool IsStreamDriven => m_streamDriven;

    /// <summary>
    /// 원격이 <b>아직 첫 자세를 못 받았는가</b> — 소유자가 그 빈 구간을 메울지 묻는 자리다.
    ///
    /// ⚠ <b><see cref="IsStreamDriven"/>의 반대가 아니다.</b> 저것이 거짓인 경우는 둘인데 뜻이
    /// 정반대다: <b>아직 안 왔다</b>(메워야 한다)와 <b>다 오고 끝났다</b>(정착 자세가 확정이니
    /// 건드리면 안 된다). 하나로 물으면 정착 자세를 받은 직후 다시 메우기가 켜져 <b>진입 시점의
    /// 자세(서 있는 몸)가 정착 자세를 덮어쓴다</b> — 실측된 증상이 정확히 그것이었다:
    /// 다 쓰러진 시체가 마지막에 벌떡 선 자세로 바뀐다.
    /// </summary>
    public bool IsAwaitingFirstPose => m_expectingStream && !m_hasReceivedPose;

    /// <summary>
    /// 정착 자세를 받아 입혔다 — <b>원격에서만 발행된다.</b> 소유자가 여기서 몸을 얼린다.
    ///
    /// 자세 자체는 이 부품이 이미 입혔으므로 구독자가 할 일은 <b>상태 전이</b>뿐이다. 그 분업이
    /// 이 클래스가 물리를 모르는 이유이기도 하다 — 무엇이 "얼림"인지는 소유자만 안다.
    /// </summary>
    public event System.Action OnSettledPoseReceived;

    // ⚠ <b>여기 "골반 위치는 옛 배선(골반 NT)에 맡긴다"는 전환기 스위치가 있었다 — 걷어냈다.</b>
    //
    // 근거는 "둘 다 골반을 쥐면 같은 서버 값의 다른 지연본이 엇갈려 떤다"였는데, <b>맡기는 쪽이
    // 훨씬 나빴다.</b> 골반 NT는 FixedUpdate에 적용되고 루트 NT는 그와 다른 시점에 적용되는데,
    // 원격의 뼈는 전부 키네마틱이라 <b>그 사이에 루트를 따라 통째로 끌려간다.</b> 래그돌 중 루트는
    // 골반을 따라다니므로 진입에 발밑→골반으로 <b>0.9m를 한 방에</b> 뛰고 정착에 지면으로 되돌아온다
    // — 실측된 증상이 정확히 그것이었다: <b>클라에서 시체가 떠서 시작하고 착지할 때 땅속에 들어갔다
    // 나온다.</b> 서버는 <c>CapturePose</c>/<c>RestoreCapturedPose</c>가 그 왕복을 감싸 무사하지만
    // 클라에는 그 감싸기가 없다(<c>NpcRagdoll.TickRootFollow</c> 주석이 미리 적어 두고 있었다).
    //
    // <b>월드로 매 프레임 못박는 것이 그 경로를 통째로 끊는다</b>(계획서 §1-3) — 루트가 어디로
    // 가든 몸은 스트림이 놓은 자리에 있는다. 지연 문제가 아니라 <b>앵커가 두 클럭에 걸쳐 있던</b>
    // 문제였고, 그래서 핑이 0이어도 똑같이 났다.

    private void Awake()
    {
        // ⚠ 리그가 붙는 자리는 개체마다 다르다 — NPC는 <c>Model</c> 밑, 플레이어는 <c>Corpse</c> 밑이다.
        // 평시 비활성일 수 있으므로 includeInactive를 켠다.
        m_rig = GetComponentInChildren<RagdollRig>(true);
        if (m_rig == null)
        {
            Debug.LogWarning(
                $"RagdollPoseStreamer: RagdollRig를 찾지 못해 자세 스트리밍을 끈다 — {name}",
                this
            );
            enabled = false;
            return;
        }

        // 소유자의 Awake보다 먼저 돌 수 있다 — 리그 수집은 멱등이라 여기서 보장해도 된다.
        m_rig.EnsureCollected();
    }

    // ---- 송신 (권위 피어) ----

    /// <summary>
    /// 자세를 흘려보내기 시작한다 — 소유자가 래그돌에 진입할 때 부른다. <b>멱등</b>.
    ///
    /// 권위가 아니면 무동작이다 — 호출부가 권위를 따로 묻지 않아도 되게 여기서 삼킨다.
    /// (소유자 쪽 진입 경로는 전 피어에서 도는 폴링이라 분기를 저쪽에 두면 매 호출부에 번진다)
    /// </summary>
    public void BeginStreaming()
    {
        if (m_rig == null || !m_rig.IsValid)
            return;

        // ⚠ <b>이 두 줄은 원격에서도 세운다</b> — 소유자가 <see cref="IsAwaitingFirstPose"/>로
        // "첫 패킷이 오기 전 빈 구간"을 메우는데, 그 판정에 <b>지금이 자세를 기다리는 국면인가</b>가
        // 필요하기 때문이다. 실제 송신은 아래 권위 게이트가 막는다.
        m_expectingStream = true;
        m_hasReceivedPose = false; // 새 국면 — 이번 무너짐의 첫 패킷을 다시 기다린다

        // 새 국면이 열렸으니 래치를 내린다 — 기상으로 한 번 끊긴 몸이 다시 무너질 때 여기를 지난다.
        //
        // ⚠ <b>잠든 시체를 깨우는 경로는 여기가 아니라 <see cref="ResumeStreaming"/>이다.</b>
        // 그쪽은 래치를 내리지 않는데, 시체는 <c>StopStreaming</c>을 지나지 않으므로(사망은 영구라
        // 기상이 없다) 래치가 서 있을 수가 없다. 기절했다 깨어난 몸이 다시 무너지는 경우만 여기를
        // 지나고, 그때는 이 대입이 필요하다.
        m_streamEnded = false;
        m_haveLengthSequence = false; // 새 국면 — 이번 무너짐의 첫 길이는 무조건 받는다

        if (!IsPoseAuthority)
            return;

        m_streaming = true;
        m_stepsSinceSend = 0;
        m_stepsSinceLengths = 0;
    }

    /// <summary>
    /// 스트림을 끊고 <b>마지막 자세를 한 번 더</b> 보낸다 — 물리가 잠들었을 때 부른다. <b>멱등</b>.
    ///
    /// <b>좌표계는 바뀌지 않는다 — 스트리밍과 똑같이 월드다.</b> 그래서 이 패킷을 받아도 원격의
    /// 화면은 <b>변하지 않는다</b>. 그것이 사양이다: 정착은 "스트림이 멈췄다"는 사실일 뿐이고,
    /// 몸은 이미 마지막 자세를 그리고 있다.
    ///
    /// <b>예전에는 여기서 월드→로컬로 갈아탔다.</b> 정착 뒤 몸을 루트에 매달아 유치장 순간이동을
    /// 루트 한 줄로 만들려는 것이었는데, 그 전환이 <b>이음새</b>였다: 원격의 루트가 호스트와 조금만
    /// 달라도 그 차이가 통째로 몸의 점프가 됐고, 자세는 신뢰 RPC로 즉시·루트는 NT 보간으로 늦게
    /// 도착해 <b>두 값의 동시 도착이 구조적으로 불가능</b>했다. 지금은 순간이동 쪽이 뼈를 직접
    /// 옮기고 자세를 다시 쏘므로(<c>NpcRagdoll.ServerPlaceCorpse</c>) 갈아탈 이유가 없다.
    ///
    /// <b>신뢰 전송이다.</b> 이 한 패킷이 원격의 <b>종착 상태</b>라 잃으면 그 시체는 마지막으로
    /// 도착한 언리라이어블 자세로 남는다.
    /// </summary>
    public void EndStreaming()
    {
        if (!m_streaming)
            return;

        m_streaming = false;
        m_expectingStream = false;

        if (!IsSpawned || m_rig == null || !m_rig.IsValid || m_rig.Hips == null)
            return;

        EnsureSendBuffer();
        if (!m_rig.CaptureLocalPose(m_sendBuffer, out _))
            return;

        // ⚠ <b>뼈 길이를 함께 싣는다</b> — 이 패킷이 원격의 종착 상태라, 여기가 틀리면 그 시체는
        // 끝까지 다른 몸으로 남는다 (docs/npc-ragdoll.md §8).
        m_rig.CaptureBoneLengths(m_lengthBuffer);

        m_sequence = unchecked((ushort)(m_sequence + 1));
        FinalPoseRpc(m_sequence, m_rig.Hips.position, Pack(m_sendBuffer), m_lengthBuffer);
    }

    /// <summary>
    /// 잠든 몸이 다시 움직이기 시작했다 — 스트림을 재개한다. <b>멱등</b>.
    /// 밧줄·발길질·폭발 어느 쪽이든 권위 피어가 깨어남을 감지하면 부른다.
    /// </summary>
    public void ResumeStreaming()
    {
        // ⚠ <c>m_streamEnded</c>는 <b>기상</b>으로만 선다 — 정착은 세우지 않는다. 그래서 잠들었다
        // 깨어난 몸은 여기를 통과하고, 애니메이터가 몸을 되받은 뒤에는 통과하지 못한다.
        if (m_streaming || m_streamEnded || !IsPoseAuthority)
            return;

        m_streaming = true;
        m_expectingStream = true;
        m_stepsSinceSend = 0;
        m_stepsSinceLengths = 0;
    }

    /// <summary>
    /// 자세를 <b>보간 없이</b> 한 번 보낸다 — 시체를 통째로 옮겼을 때(유치장 배치) 부른다.
    ///
    /// 평범한 스냅샷으로 보내면 원격이 출발지와 도착지 <b>사이를 보간하며</b> 시체가 맵을 가로질러
    /// 날아간다. 이 패킷은 원격의 스냅샷 버퍼를 비우고 새 자세만 남긴다 — NetworkTransform의
    /// <c>Teleport</c>와 같은 성격이고, 그쪽과 <b>같은 프레임에</b> 나가야 루트와 몸이 함께 뛴다.
    /// </summary>
    public void SendTeleportPose()
    {
        if (!IsPoseAuthority || !IsSpawned || m_rig == null || !m_rig.IsValid || m_rig.Hips == null)
            return;

        EnsureSendBuffer();
        if (!m_rig.CaptureLocalPose(m_sendBuffer, out _))
            return;

        // 길이도 함께 — 옮겨 놓은 몸을 원격이 <b>도착 즉시</b> 같은 모양으로 그리게 한다. 안 실으면
        // 다시 무너져 정착할 때까지(수 초) 바인드 길이 몸으로 누워 있다 (docs/npc-ragdoll.md §8).
        m_rig.CaptureBoneLengths(m_lengthBuffer);

        m_sequence = unchecked((ushort)(m_sequence + 1));
        TeleportPoseRpc(m_sequence, m_rig.Hips.position, Pack(m_sendBuffer), m_lengthBuffer);
    }

    public void StopStreaming()
    {
        m_streaming = false;
        m_expectingStream = false;

        // 원격 쪽 — 재생을 끊는다. 안 끊으면 애니메이터가 놓은 포즈를 매 프레임 덮어쓴다.
        m_streamDriven = false;
        m_snapshotCount = 0;
        m_haveSequence = false;
        m_streamEnded = true; // 늦게 온 스냅샷이 기상 자세를 덮지 못하게
    }

    // 캡처는 <b>FixedUpdate</b>다 — 물리가 진실인 자리에서 떠야 스텝 사이 보간값이 섞이지 않는다.
    // (뼈는 래그돌 중 Interpolate라 Update에서 읽으면 물리가 만든 적 없는 중간 포즈가 나온다)
    private void FixedUpdate()
    {
        if (!m_streaming || !IsSpawned || !IsPoseAuthority)
            return;

        // ⚠ <b>길이가 먼저다.</b> 아래 자세 게이트가 <c>return</c>으로 빠져나가므로, 뒤에 두면
        // 길이 카운터가 자세를 보내는 틱에만 돌아 주기가 통째로 어긋난다.
        if (m_lengthEveryFixedSteps > 0 && ++m_stepsSinceLengths >= m_lengthEveryFixedSteps)
        {
            m_stepsSinceLengths = 0;
            SendLengths();
        }

        m_stepsSinceSend++;
        if (m_stepsSinceSend < m_sendEveryFixedSteps)
            return;

        m_stepsSinceSend = 0;
        SendSnapshot();
    }

    private void SendSnapshot()
    {
        if (m_rig == null || !m_rig.IsValid || m_rig.Hips == null)
            return;

        EnsureSendBuffer();

        // 골반 로컬 위치는 버린다 — 스트리밍 중에는 <b>월드</b>를 보낸다(클래스 주석 §1-3).
        if (!m_rig.CaptureLocalPose(m_sendBuffer, out _))
            return;

        m_sequence = unchecked((ushort)(m_sequence + 1));
        StreamPoseRpc(m_sequence, m_rig.Hips.position, Pack(m_sendBuffer));
    }

    /// <summary>
    /// 뼈 길이만 따로 흘려보낸다 — <b>자세 패킷에 끼워 넣지 않는다.</b>
    ///
    /// 주기가 다르고(25Hz vs 2Hz) 핫 패킷의 크기를 건드리지 않기 위해서다. <b>언리라이어블로
    /// 충분하다</b> — 하나를 놓쳐도 다음 것이 곧 오고, 최종값은 <see cref="FinalPoseRpc"/>가
    /// 신뢰 전송으로 못박는다.
    /// </summary>
    private void SendLengths()
    {
        if (m_rig == null || !m_rig.IsValid)
            return;

        EnsureSendBuffer();
        if (!m_rig.CaptureBoneLengths(m_lengthBuffer))
            return;

        m_sequence = unchecked((ushort)(m_sequence + 1));
        StreamLengthsRpc(m_sequence, m_lengthBuffer);
    }

    private void EnsureSendBuffer()
    {
        if (m_sendBuffer == null || m_sendBuffer.Length != m_rig.BoneCount)
            m_sendBuffer = new Quaternion[m_rig.BoneCount];

        if (m_packedBuffer == null || m_packedBuffer.Length != m_rig.BoneCount)
            m_packedBuffer = new uint[m_rig.BoneCount];

        if (m_lengthBuffer == null || m_lengthBuffer.Length != m_rig.BoneCount)
            m_lengthBuffer = new Vector3[m_rig.BoneCount];
    }

    // ---- 압축 ----
    //
    // <b>쿼터니언 하나를 16B → 4B로 줄인다</b>(smallest-three). NGO가 자기
    // <c>NetworkTransform</c>에 쓰는 것과 <b>같은</b> 유틸리티라 직접 짜지 않았다.
    //
    // <b>원리는 docs/quaternion-compression.md에 있다</b> — 왜 4바이트에 들어가는가, 오차가 어디서 오는가.
    //
    // 오차는 성분당 10비트라 사양상 약 0.1°(실측 최대 0.194°)다 — 무너지는 시체에서 보이는 크기가 아니고,
    // 정착 자세도 같은 압축을 쓴다(둘을 가르면 마지막 스트림과 정착 사이에
    // 그 0.1°만큼 튀는 이음새이 생긴다).
    private uint[] Pack(Quaternion[] rotations)
    {
        for (int i = 0; i < rotations.Length; i++)
        {
            Quaternion rotation = rotations[i];
            m_packedBuffer[i] = QuaternionCompressor.CompressQuaternion(ref rotation);
        }

        return m_packedBuffer;
    }

    // 푸는 자리 — 버퍼를 돌려주므로 호출부는 <b>바로 써야 한다</b>(다음 패킷이 덮어쓴다).
    private Quaternion[] Unpack(uint[] packed)
    {
        if (m_unpackBuffer == null || m_unpackBuffer.Length != packed.Length)
            m_unpackBuffer = new Quaternion[packed.Length];

        for (int i = 0; i < packed.Length; i++)
            QuaternionCompressor.DecompressQuaternion(ref m_unpackBuffer[i], packed[i]);

        return m_unpackBuffer;
    }




    // ---- 수신 (원격) ----

    /// <summary>
    /// 흘러오는 자세 — <b>언리라이어블</b>이라 유실·순서 뒤바뀜을 전제한다.
    ///
    /// 언리라이어블인 이유는 자세가 <b>누적되지 않는 값</b>이기 때문이다. 하나를 놓쳐도 다음 패킷이
    /// 완전한 상태를 들고 오므로 재전송은 늦은 정보를 늦게 배달할 뿐이다. 반대로 마지막
    /// 정착 자세(<see cref="FinalPoseRpc"/>)는 <b>뒤가 없어서</b> 신뢰 전송이어야 한다.
    ///
    /// 회전은 <b>압축해서</b> 온다 — 쿼터니언당 4B(<see cref="Pack"/>·<see cref="Unpack"/>).
    /// 페이로드는 86B(뼈 17개)이고 시체 1구당 원격 1인 기준 약 2.1KB/s다(25Hz · 2026-09-02 실측).
    /// </summary>
    [Rpc(SendTo.NotMe, Delivery = RpcDelivery.Unreliable)]
    private void StreamPoseRpc(ushort sequence, Vector3 hipsWorld, uint[] packed)
        => ReceivePose(sequence, hipsWorld, packed, terminal: false);

    /// <summary>
    /// 흘러오는 <b>뼈 길이</b> — 자세와 따로, 훨씬 낮은 주기로 온다
    /// (<see cref="m_lengthEveryFixedSteps"/>). 언리라이어블인 이유는 자세와 같다: 누적되지 않는
    /// 값이라 하나를 놓쳐도 다음 것이 완전한 상태를 들고 온다.
    ///
    /// <b>왜 보내야 하는가는 docs/npc-ragdoll.md §8이다</b> — 권위 쪽 뼈는 무너지는 동안 늘어나고,
    /// 원격은 물리를 안 굴려 바인드 그대로라 안 보내면 같은 회전을 다른 골격에 입힌 몸이 된다.
    /// </summary>
    [Rpc(SendTo.NotMe, Delivery = RpcDelivery.Unreliable)]
    private void StreamLengthsRpc(ushort sequence, Vector3[] lengths)
        => ReceiveLengths(sequence, lengths);

    /// <summary>
    /// <b>스트림의 마지막 패킷</b> — 물리가 잠들었다. 신뢰 전송이고 <b>좌표계는 스트리밍과 같은
    /// 월드다.</b>
    ///
    /// <b>받아도 화면이 바뀌지 않는 것이 사양이다.</b> 원격은 이미 이 자세를 그리고 있고, 이 패킷은
    /// "여기서 멈춘다"를 <b>유실 없이</b> 알릴 뿐이다. 예전에는 여기서 로컬 좌표로 갈아타며 몸을
    /// 루트에 매달았고, 그 전환이 정착 순간의 점프였다(<see cref="EndStreaming"/> 주석).
    ///
    /// <b>뼈 길이를 함께 나른다</b>(<paramref name="lengths"/>) — 권위 쪽 시체는 무너지는 동안 뼈가
    /// 늘어나고 그 길이가 영구히 남는데(실측 0.036~0.206m), 원격은 물리를 안 굴려 바인드 그대로라
    /// 안 보내면 <b>같은 회전을 다른 골격에 입힌 몸</b>이 된다. 근거·실측은 docs/npc-ragdoll.md §8.
    ///
    /// ⚠ <b>늦게 접속한 피어에는 오지 않는다</b>(신뢰 RPC의 성질). 이미 누워 있던 시체를 자세 없이
    /// 보게 되는 구멍이고, 계획서 6단계에서 닫는다(권위 피어가 마지막 자세를 캐시했다가 새
    /// 접속자에게만 다시 쏜다).
    /// </summary>
    [Rpc(SendTo.NotMe)]
    private void FinalPoseRpc(ushort sequence, Vector3 hipsWorld, uint[] packed, Vector3[] lengths)
        => ReceivePose(sequence, hipsWorld, packed, terminal: true, lengths);

    /// <summary>시체를 통째로 옮겼다 — 보간을 끊고 이 자세만 남긴다. (<see cref="SendTeleportPose"/>)</summary>
    [Rpc(SendTo.NotMe)]
    private void TeleportPoseRpc(ushort sequence, Vector3 hipsWorld, uint[] packed, Vector3[] lengths)
    {
        m_snapshotCount = 0; // 출발지 스냅샷을 버린다 — 안 버리면 그 사이를 보간하며 날아간다
        m_haveSequence = false;
        ReceivePose(sequence, hipsWorld, packed, terminal: false, lengths);
    }

    /// <summary>
    /// 받은 자세를 스냅샷 버퍼에 넣는다 — <b>스트림과 정착이 같은 경로를 탄다.</b>
    ///
    /// 둘을 가르지 않는 것이 이 구조의 요점이다: 정착은 <b>마지막 스냅샷</b>일 뿐이라 보간이 그대로
    /// 이어지고, 재생을 끊거나 좌표계를 갈아탈 이유가 없다. 뒤늦게 도착한 언리라이어블 스냅샷은
    /// <b>시퀀스 가드가 알아서 버린다</b> — 정착 패킷도 번호를 달고 오기 때문이다.
    /// </summary>
    private void ReceivePose(
        ushort sequence,
        Vector3 hipsWorld,
        uint[] packed,
        bool terminal,
        Vector3[] lengths = null
    )
    {
        if (m_rig == null || !m_rig.IsValid || packed == null)
            return;

        // 기상으로 재생이 끝난 뒤 도착한 것 — 받으면 애니메이터가 놓은 포즈와 매 프레임 싸운다.
        if (m_streamEnded)
            return;

        if (packed.Length != m_rig.BoneCount)
        {
            // ⚠ <b>조용히 넘기지 않는다.</b> 자세가 안 오는 것과 화면상 증상이 같아지므로
            // 배선이 어긋난 것임을 말해 줘야 한다. 매 패킷 터지므로 <b>한 번만</b> 낸다.
            if (!m_warnedBoneMismatch)
            {
                m_warnedBoneMismatch = true;
                Debug.LogWarning(
                    $"RagdollPoseStreamer: 뼈 수가 달라 자세를 버린다 — {name} "
                        + $"받은={packed.Length} 내리그={m_rig.BoneCount}. 피어마다 리그가 다른 프리팹이다",
                    this
                );
            }

            return;
        }

        m_hasReceivedPose = true;

        // ⚠ 옛 패킷을 버린다. 언리라이어블은 순서를 보장하지 않으므로, 이 검사가 없으면 시체가
        // 이따금 한 스냅샷 뒤로 튄다.
        if (m_haveSequence && !IsNewer(sequence, m_newestSequence))
        {
            return;
        }

        m_newestSequence = sequence;
        m_haveSequence = true;
        m_streamDriven = true;

        // ⚠ <b>길이가 자세보다 먼저다.</b> 자세는 회전뿐이라 팔다리 <b>위치</b>는 이 길이 위에
        // 얹혀 계층 수학으로 만들어진다 — 순서가 뒤집히면 이번 프레임은 옛 길이로 그려진다.
        //
        // 한 번 쓰면 남는다(원격의 뼈는 키네마틱이라 아무도 덮지 않는다). 그래서 <b>신뢰 1회
        // 패킷에만</b> 실어도 그 뒤 흘러오는 스냅샷이 같은 골격 위에서 재생된다.
        if (lengths != null && lengths.Length == m_rig.BoneCount && m_rig.ApplyBoneLengths(lengths))
        {
            // 이 패킷이 골격을 못박았다 — 이보다 옛 주기 길이가 뒤늦게 와도 되돌리지 못하게 한다.
            m_newestLengthSequence = sequence;
            m_haveLengthSequence = true;
        }

        PushSnapshot(hipsWorld, Unpack(packed));

        if (!terminal)
            return;

        // 자세가 더 오지 않는 국면 — 소유자에게 알린다. <b>재생은 계속 돈다</b>:
        // <see cref="TickApply"/>가 마지막 스냅샷을 붙들고, 그 한 줄이 원격의 몸을 루트에서
        // 떼어 놓는다(루트가 흔들려도 몸은 스트림이 놓은 자리에 있는다).
        m_expectingStream = false;
        OnSettledPoseReceived?.Invoke();
    }

    /// <summary>
    /// 흘러온 뼈 길이를 <b>즉시</b> 입힌다 — 보간하지 않는다.
    ///
    /// <see cref="RagdollRig.ApplyBoneLengths"/>는 <c>localPosition</c>만, 자세 재생
    /// (<see cref="ApplyPose"/>)은 <c>localRotation</c>만 쓰므로 둘은 서로 싸우지 않는다.
    /// 다음 프레임의 재생이 <b>새 골격 위에</b> 회전을 얹는다.
    /// </summary>
    private void ReceiveLengths(ushort sequence, Vector3[] lengths)
    {
        if (m_rig == null || !m_rig.IsValid || lengths == null)
            return;

        // 기상으로 재생이 끝난 뒤 도착한 것 — 애니메이터가 되받은 몸의 골격을 건드리면 안 된다.
        if (m_streamEnded)
            return;

        // 뼈 수가 어긋나면 조용히 버린다 — 경고는 자세 쪽이 이미 한 번 낸다.
        if (lengths.Length != m_rig.BoneCount)
            return;

        if (m_haveLengthSequence && !IsNewer(sequence, m_newestLengthSequence))
            return;

        m_newestLengthSequence = sequence;
        m_haveLengthSequence = true;

        m_rig.ApplyBoneLengths(lengths);
    }

    private void PushSnapshot(Vector3 hipsWorld, Quaternion[] rotations)
    {
        EnsureSnapshotBuffers();

        if (m_snapshotCount == k_maxSnapshots)
        {
            // 가장 오래된 것을 밀어낸다 — 배열은 재사용하고 내용만 앞으로 당긴다.
            Snapshot oldest = m_snapshots[0];
            for (int i = 0; i < k_maxSnapshots - 1; i++)
                m_snapshots[i] = m_snapshots[i + 1];

            m_snapshots[k_maxSnapshots - 1] = oldest;
            m_snapshotCount = k_maxSnapshots - 1;
        }

        Snapshot slot = m_snapshots[m_snapshotCount];
        slot.Time = Time.time;
        slot.HipsWorld = hipsWorld;
        for (int i = 0; i < rotations.Length; i++)
            slot.Rotations[i] = rotations[i];

        m_snapshots[m_snapshotCount] = slot;
        m_snapshotCount++;
    }

    private void EnsureSnapshotBuffers()
    {
        int bones = m_rig.BoneCount;

        if (m_snapshots == null || m_snapshots.Length != k_maxSnapshots)
        {
            m_snapshots = new Snapshot[k_maxSnapshots];
            m_snapshotCount = 0;
        }

        for (int i = 0; i < m_snapshots.Length; i++)
        {
            if (m_snapshots[i].Rotations == null || m_snapshots[i].Rotations.Length != bones)
                m_snapshots[i].Rotations = new Quaternion[bones];
        }

        if (m_applyBuffer == null || m_applyBuffer.Length != bones)
            m_applyBuffer = new Quaternion[bones];
    }

    // 적용은 <b>Update</b>다 — 원격의 뼈는 물리에 참여하지 않으므로 물리 틱에 묶일 이유가 없고,
    // 렌더 주기에 맞춰 그려야 부드럽다.
    private void Update()
    {
        if (!m_streamDriven || IsPoseAuthority)
            return;

        TickApply();
    }



    // 재생 시점을 <see cref="m_interpolationDelay"/>만큼 뒤로 물려 두 스냅샷 사이를 섞는다.
    //
    // ⚠ <b>스냅샷 시각이 로컬 수신 시각이라 네트워크 지터가 그대로 재생에 실린다.</b> 더 정확한
    // 방법은 시퀀스 번호와 송신 주기로 시각을 <b>재구성</b>하거나(<c>시작시각 + (seq−시작seq) ×
    // 주기</c>) 서버 시각을 함께 싣는 것이다. 1단계에서는 넣지 않았다 — 지연 버퍼가 대부분을
    // 흡수하고, 실측에서 떨림이 남으면 그때 붙일 자리로 이 주석을 남긴다. (계획서 7단계)
    private void TickApply()
    {
        if (m_snapshotCount == 0)
            return;

        float renderTime = Time.time - m_interpolationDelay;

        // 재생 시점이 가장 오래된 스냅샷보다 앞이면(=버퍼가 아직 안 찼다) 그것을 그대로 쓴다.
        if (m_snapshotCount == 1 || renderTime <= m_snapshots[0].Time)
        {
            ApplySnapshot(m_snapshots[0]);
            return;
        }

        for (int i = 0; i < m_snapshotCount - 1; i++)
        {
            Snapshot from = m_snapshots[i];
            Snapshot to = m_snapshots[i + 1];

            if (renderTime > to.Time)
                continue;

            float span = to.Time - from.Time;
            float t = span > 0.0001f ? Mathf.Clamp01((renderTime - from.Time) / span) : 1f;
            ApplyBlend(from, to, t);
            return;
        }

        // 재생 시점이 가장 새 스냅샷보다 뒤다 — 패킷이 늦거나 끊겼다. <b>외삽하지 않고 붙든다.</b>
        // 시체가 잠깐 멈춰 보이는 편이 없는 데이터로 지어낸 자세보다 낫고, 정착 패킷이 곧 온다.
        ApplySnapshot(m_snapshots[m_snapshotCount - 1]);
    }

    private void ApplyBlend(Snapshot from, Snapshot to, float t)
    {
        for (int i = 0; i < m_applyBuffer.Length; i++)
            m_applyBuffer[i] = Quaternion.Slerp(from.Rotations[i], to.Rotations[i], t);

        ApplyPose(m_applyBuffer, Vector3.Lerp(from.HipsWorld, to.HipsWorld, t));
    }

    private void ApplySnapshot(Snapshot snapshot) => ApplyPose(snapshot.Rotations, snapshot.HipsWorld);

    // 회전은 리그가 입히고, 골반만 월드로 못박는다.
    //
    // <see cref="RagdollRig.ApplyLocalPose"/>에 <b>지금의 골반 로컬 위치를 그대로</b> 넘기는 것이
    // 이상해 보이지만 의도적이다 — 저 함수는 (회전 + 골반 로컬 위치) 한 쌍을 받는데 여기서 바꾸고
    // 싶은 것은 회전뿐이고, 골반은 바로 아래 줄에서 월드로 덮어쓴다. 리그에 월드 오버로드를 새로
    // 만들지 않은 것은 <b>리그를 안 건드린다</b>는 이 작업의 선 때문이다.
    private void ApplyPose(Quaternion[] rotations, Vector3 hipsWorld)
    {
        Transform hips = m_rig.Hips;
        if (hips == null)
            return;

        m_rig.ApplyLocalPose(rotations, hips.localPosition);
        hips.position = hipsWorld;
    }

    // ushort 랩어라운드를 견디는 "더 새것인가" 판정 — 차이를 부호 없는 반바퀴로 읽는다.
    private static bool IsNewer(ushort candidate, ushort current)
        => unchecked((ushort)(candidate - current)) is > 0 and < k_sequenceHalfRange;

}
