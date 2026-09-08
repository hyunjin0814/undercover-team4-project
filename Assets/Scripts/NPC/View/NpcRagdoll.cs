using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// NPC 래그돌 — 죽거나 기절한 몸의 애니메이터를 끄고 뼈를 물리에 넘긴다. <b>표현 계층 전용</b>이라
/// NetworkBehaviour가 아니다(피어로 나가는 자세는 <see cref="RagdollPoseStreamer"/>가 전담).
///
/// <b>시뮬레이션은 하나뿐이다.</b> 권위 피어(서버)만 물리를 굴리고 원격의 뼈는 전부 키네마틱이라
/// 받은 자세를 입히기만 한다.
///
/// <b>상태 셋이 이 클래스를 읽는 열쇠다:</b>
/// <list type="bullet">
///   <item><c>Animated</c> — 애니메이터가 포즈를 쥔다</item>
///   <item><c>Ragdoll</c> — <b>뼈가 주인</b>. 물리가 몸을 만들고 루트가 그 밑을 따라간다</item>
/// </list>
///
/// <b>시체도 끝까지 <c>Ragdoll</c>이다.</b> 정착은 상태가 아니라 "물리가 잠들었다"는 국면이라
/// (<see cref="m_settled"/>) 뼈는 동적으로 남는다 — 그래서 밟히거나 폭발에 밀리면 알아서 다시
/// 움직이고, 스트림이 그것을 이어 받는다. 예전에는 여기에 <c>Frozen</c>이 있어 뼈를 키네마틱으로
/// 되돌렸고, 그 전이가 정착 순간의 어색함과 유치장 발사 사고의 뿌리였다.
///
/// <b>설계 근거·실측·되살리면 안 되는 것들은 <c>docs/npc-ragdoll.md</c>에 있다.</b>
/// 이 파일을 고치기 전에 그쪽의 해당 항목을 먼저 볼 것 — 특히 §4(순서가 중요한 자리)와 §5(지웠다).
///
/// 붙이는 곳: <b>NPC 프리팹 루트</b>(<see cref="NpcController"/>와 같은 오브젝트).
/// <see cref="RagdollRig"/>는 리그를 직속 자식으로 가진 <c>Model</c>에 붙으므로 자식에서 찾는다.
/// </summary>
public partial class NpcRagdoll : MonoBehaviour
{
    // 기상 시 NavMesh를 다시 찾는 반경(m) — "누운 자리 <b>바로</b> 밑"을 뜻하는 값이라 상수다.
    // 넓히면 구조물 위에 걸친 몸이 기상하면서 아래로 툭 떨어져 순간이동으로 보인다 (#913).
    private const float k_navMeshSampleDistance = 0.75f;

    private enum RagdollState
    {
        Animated,
        Ragdoll,
    }

    [Header("정착 판정")]
    [Tooltip("물리가 스스로 안 잠드는 몸을 강제로 재우는 시간(초) — 지형에 껴서 영원히 떨리는 " +
             "경우의 안전장치다.\n\n" +
             "<b>평시 정착은 이 값과 무관하다</b> — PhysX가 알아서 재운다. 여기까지 왔다는 것은 " +
             "몸이 무언가에 물려 떨고 있다는 뜻이고, 그때만 Sleep()을 대신 불러 준다")]
    [SerializeField] private float m_settleTimeoutSeconds = 5f;

    [Header("기상 블렌드")]
    [Tooltip("래그돌 자세에서 애니메이터 자세로 섞는 시간(초) — 0이면 즉시 복귀. " +
             "줄을 풀거나 기절이 끝나 일어설 때 몸이 한 프레임에 튀는 것을 없앤다")]
    [SerializeField] private float m_blendSeconds = 0.3f;

    [Header("정착 후 정렬")]
    [Tooltip("시체 밑 지면을 찾는 레이캐스트 마스크 — 지형(Default). 래그돌 뼈는 다른 레이어라 안 걸린다")]
    [SerializeField] private LayerMask m_groundMask = 1;

    [Tooltip("골반 아래로 지면을 찾는 거리(m). 짧게 잡을 것 — 길면 얇은 실내 바닥을 뚫고 아래층을 " +
             "찾아내 시체가 한 층 밑으로 순간이동한다")]
    [SerializeField] private float m_groundProbeDistance = 1.5f;

    private NpcController m_owner;
    private RagdollRig m_rig; // 뼈 한 벌 — 물리 조작 전부를 위임한다. 리그 소유자(Model)에 붙어 있다
    private RagdollRope m_rope; // 관절 밧줄 — 리그와 같은 오브젝트
    private Animator m_animator;
    private NavMeshAgent m_agent;
    private NpcAnimationDriver m_driver; // 기상 시점의 진실값 — IsProne
    private Unity.Netcode.Components.NetworkTransform m_rootNetTransform; // 복제되는 유일한 트랜스폼
    private RagdollPoseStreamer m_streamer; // 자세가 피어로 나가는 유일한 통로

    private RagdollPoseBlend m_blend; // 기상 블렌드 — 래그돌 자세 → 지금 애니메이터가 놓는 자세
    private bool m_blending;

    private RagdollState m_state = RagdollState.Animated;
    // 언제 정착시킬 것인가 — 경과·타임아웃·공중 유예를 쥔다(플레이어와 같은 절차).
    private readonly RagdollSettlePolicy m_settle = new RagdollSettlePolicy();

    // 정책에 넘길 지연 평가 — 매 프레임 메서드 그룹을 넘기면 호출마다 델리게이트가 할당된다.
    private System.Func<bool> m_hasGroundUnderHips;

    // 물리가 잠들어 자세 스트림을 끊었는가 — <b>상태가 아니라 국면이다.</b>
    //
    // ⚠ 예전의 <c>Frozen</c>과 다르다. 그것은 뼈를 키네마틱으로 되돌리는 <b>되돌릴 수 없는 전이</b>라
    // 상태 셋의 하나였는데, 이것은 "지금 잠들어 있다"를 적어 둔 것뿐이라 뼈는 여전히 동적이다.
    // 그래서 밟히거나 폭발에 밀리면 물리가 알아서 깨어나고 이 깃발만 내려간다.
    private bool m_settled;

    // 늦게 접속했는데 대상이 이미 쓰러져 있던 경우 — 이번 에피소드는 건너뛴다.
    private bool m_skipThisEpisode;
    private bool m_polledOnce;

    /// <summary>래그돌이 애니메이터로부터 포즈를 빼앗고 있는가 — 표현 계층이 물러나는 판정에 쓴다.</summary>
    public bool IsRagdollActive => m_state != RagdollState.Animated;

    // 위치 권한 — 서버(또는 세션 없는 오프라인 Play)만 루트를 옮길 수 있다.
    private bool HasMoveAuthority => !m_owner.IsSpawned || m_owner.IsServer;

    private void Awake()
    {
        m_hasGroundUnderHips = HasGroundUnderHips;

        m_owner = GetComponent<NpcController>();
        m_agent = GetComponent<NavMeshAgent>();
        m_driver = GetComponent<NpcAnimationDriver>();
        m_rootNetTransform = GetComponent<Unity.Netcode.Components.NetworkTransform>();

        // ⚠ 리그·애니메이터·밧줄은 전부 자식(Model)에 있다 — GetComponent로 찾으면 항상 null이다.
        m_rig = GetComponentInChildren<RagdollRig>(true);
        if (m_rig == null)
        {
            Debug.LogWarning(
                $"NpcRagdoll: RagdollRig를 찾지 못해 래그돌을 끈다 — {name}. "
                    + "Tools > Ragdoll > Finish Setup 을 이 프리팹에 돌릴 것",
                this
            );
            enabled = false;
            return;
        }

        m_rig.EnsureCollected(); // 같은 오브젝트의 Awake 순서는 보장되지 않는다
        m_rope = m_rig.GetComponent<RagdollRope>();
        SetupWallFix(); // #980 — 벽에 박힌 팔을 고치는 쪽. 리그가 잡힌 뒤여야 한다
        m_animator = GetComponentInChildren<Animator>(true);

        // 섞는 대상은 리그 최상단 이하 전 트랜스폼 — 물리를 안 받는 뼈(목·손가락·발)까지 넣어야
        // 블렌드 첫 프레임에 목과 손이 튀지 않는다.
        m_blend = new RagdollPoseBlend(m_rig.BoneRoot);

        // 스트리머는 루트에 있다 — NGO가 비활성 GameObject의 NetworkBehaviour를 스폰에서 제외한다.
        m_streamer = GetComponent<RagdollPoseStreamer>();
        if (m_streamer == null)
        {
            Debug.LogWarning(
                $"NpcRagdoll: RagdollPoseStreamer가 없다 — {name}. 원격에 자세가 가지 않아 "
                    + "시체가 진입 자세로 굳는다. RagdollSetup을 이 프리팹에 돌릴 것",
                this
            );
            return;
        }

        m_streamer.OnSettledPoseReceived += HandleSettledPoseReceived;
    }

    private void OnDestroy()
    {
        if (m_streamer != null)
            m_streamer.OnSettledPoseReceived -= HandleSettledPoseReceived;
    }

    // ---- 매 물리 스텝 ----

    /// <summary>
    /// 루트 추종은 <b>물리 스텝에 묶는다 — 프레임이 아니다.</b> (#759 — 플레이어와 같은 원인·같은 수정)
    ///
    /// ⚠ <b>이것이 슬로모션의 원인이었다.</b> <see cref="TickRootFollow"/>는 루트를 옮긴 뒤 뼈
    /// 리지드바디의 트랜스폼에 월드 자세를 되쓰는데, 그 대입이 "렌더 전용"이 아니다 — 다음 스텝
    /// 직전에 PhysX로 flush되고 동적 바디에는 텔레포트로 먹는다. <c>Update</c>에 두면 프레임마다
    /// 솔버 상태가 무효화되는데 물리는 50Hz라, 호스트가 50fps를 넘는 만큼 관절 오차가 쌓인다.
    /// 근거와 실측은 <c>docs/759-ragdoll-slowmotion-handoff.md</c> §2.
    ///
    /// <b>정착한 몸은 건너뛴다</b> — 레이캐스트도 자세 캡처도 하지 않는다. 잠든 몸은 루트가 다시
    /// 따라갈 곳이 없고, 시체가 쌓이는 라운드에서 이 생략이 스텝당 비용을 시체 수에 비례하지
    /// 않게 만든다.
    /// </summary>
    private void FixedUpdate()
    {
        if (m_state != RagdollState.Ragdoll || !HasMoveAuthority)
            return;

        // ⚠ 벽 탈출이 미는 동안에는 루트 추종을 쉰다 — 위 주석대로 이 함수의 포즈 대입은 동적
        // 바디에 텔레포트로 먹으므로, 같은 스텝에 넣은 탈출 속도를 헛돌게 만들 수 있다 (#980).
        if (!m_settled && !IsWallFixBusy)
            TickRootFollow();

        // ⚠ <b>마지막이어야 한다</b> — 위의 포즈 대입보다 뒤에 속도를 넣어야 물리 스텝이 그것을
        // 본다. 그리고 정착한 몸에도 돌아야 한다: 박힌 채 잠든 시체가 이 기능의 대표 사례다.
        TickWallFix();
    }

    // ---- 매 프레임 ----

    private void Update()
    {
        PollRagdollTriggers();

        if (m_state != RagdollState.Ragdoll)
            return;

        // 원격은 자세를 받을 뿐이다 — 물리가 돌지 않으므로 잠들 몸도, 따라갈 골반도 없다.
        if (!HasMoveAuthority)
            return;

        // 잠든 뒤에는 <b>깨어났는지만</b> 본다. 밟히거나 폭발에 밀리면 PhysX가 스스로 깨우므로
        // 이 한 줄이 그 모든 경로를 받는다 — 깨우는 쪽마다 알림을 심을 필요가 없다.
        if (m_settled)
        {
            if (!m_rig.AllAsleep)
                ServerResumeFromSleep();

            return;
        }

        // ⚠ IsAttached가 아니라 IsBeingCarried다 — 운반자가 사라져도 관절은 남는다.
        bool carried = m_rope != null && m_rope.IsBeingCarried;

        switch (m_settle.Tick(m_rig.AllAsleep, carried, m_settleTimeoutSeconds, m_hasGroundUnderHips))
        {
            case ERagdollSettleStep.Settle:
                ServerSettleInPlace();
                break;

            // 타임아웃 — 지형에 물려 스스로 못 잠드는 몸이다. 대신 재운다.
            // ⚠ <b>키네마틱 얼림이 아니라 물리 수면이다.</b> 그래서 밟거나 밧줄을 걸면 위의
            // <c>AllAsleep</c> 검사가 그대로 깨어남을 받는다 — 강제로 재운 몸도 예외가 아니다.
            case ERagdollSettleStep.ForceSleepThenSettle:
                m_rig.SleepAll();
                ServerSettleInPlace();
                break;
        }
    }

    private void LateUpdate()
    {
        // 기상 블렌드는 LateUpdate여야 한다 — 이번 프레임에 애니메이터가 놓은 자세가 곧 목표라,
        // 여기서 읽어야 재생 중인 기상 클립을 향해 살아있는 목표로 수렴한다.
        if (m_blending && m_blend.Tick(m_blendSeconds))
            m_blending = false;

        TickHoldPoseUntilStream();
    }

    /// <summary>
    /// 원격에서 첫 자세 패킷이 오기 전 구간을 메운다 — 진입 시점의 월드 자세를 붙든다.
    /// 안 붙들면 키네마틱 뼈가 이미 움직이기 시작한 루트를 계층으로 따라가 몸이 통째로 뜬다.
    /// </summary>
    private void TickHoldPoseUntilStream()
    {
        if (m_streamer == null || HasMoveAuthority || m_state != RagdollState.Ragdoll)
            return;

        // ⚠ !IsStreamDriven으로 묻지 않는다 — 그것이 거짓인 경우가 "아직 안 왔다"와 "정착까지 다
        // 받고 끝났다" 둘인데, 후자에서 메우면 정착 자세가 진입 시점 자세로 덮인다.
        if (!m_streamer.IsAwaitingFirstPose)
            return;

        m_rig.RestoreCapturedPose();
    }

    /// <summary>
    /// 루트를 시체 밑으로 끌고 간다 — 서버 전용. 루트는 이름표·콜라이더가 매달린 자리이자
    /// NetworkTransform이 복제하는 유일한 값이라, 시체를 대표하지 못하면 원격이 위치를 알 수 없다.
    /// </summary>
    private void TickRootFollow()
    {
        if (!HasMoveAuthority || m_rig.Hips == null)
            return;

        // ⚠ 루트를 옮기기 전에 뼈를 잡아 두고 옮긴 뒤 되돌린다 — 안 감싸면 진입 프레임에 몸 전체가
        // 골반 높이(약 0.9m)만큼 떠서 한 프레임 그려진다.
        //
        // ⚠ <b>이 대입은 "렌더 전용"이 아니다</b> — 다음 스텝 직전에 PhysX로 flush되고 동적 바디에는
        // 텔레포트로 먹는다. 그래서 이 함수는 <c>FixedUpdate</c>에서만 돈다 (#759 — 위 주석 참고).
        m_rig.CapturePose();

        Vector3 target = m_rig.Hips.position;

        // 몸이 바닥에 있으면 루트 높이는 지면이 준다 — 골반 높이를 쓰는 것은 공중에 있는 동안만이다.
        // 정착은 이제 아무것도 옮기지 않으므로, 루트가 지면에 있는 것은 <b>여기가 유일한 보장</b>이다.
        if (TryGroundUnder(target, out Vector3 ground) && target.y - ground.y <= RagdollGround.k_groundedHipsHeight)
            target.y = ground.y;

        transform.position = target;

        m_rig.RestoreCapturedPose();
    }

    // ---- 진입 / 이탈 ----

    // 래그돌이어야 하는지를 폴링한다 — 읽는 값이 전부 동기화 값이라 전 피어가 같은 답을 얻는다.
    // 이벤트가 아닌 이유와 에피소드 리셋의 근거는 docs/npc-ragdoll.md §3.
    private void PollRagdollTriggers()
    {
        bool wants = WantsRagdoll();

        if (!m_polledOnce)
        {
            m_polledOnce = true;
            m_skipThisEpisode = wants;
        }

        if (!wants)
        {
            // ⚠ 에피소드가 끝나면 반드시 내린다 — 안 내리면 이후 모든 기절 래그돌을 영구히 건너뛴다.
            m_skipThisEpisode = false;
            ExitRagdoll();
            return;
        }

        if (!m_skipThisEpisode && m_state == RagdollState.Animated)
            EnterRagdoll(Vector3.zero); // 힘없이 무너진다. 폭발은 임펄스를 따로 준다
    }

    /// <summary>
    /// 지금 이 몸이 래그돌이어야 하는가 — 진입과 이탈을 같은 식 하나로 답한다.
    /// ⚠ 진입 조건과 유지 조건을 갈라 묻는다 — 누워 있어야 하는 이유가 진입 이유보다 오래 간다.
    /// </summary>
    private bool WantsRagdoll()
    {
        // 사망은 영구다 — 시체는 일어나지 않으므로 이 분기가 곧 "이탈 없음"이다.
        if (m_owner.Death.IsDead)
            return true;

        // 이미 래그돌이면 묻는 것은 하나다 — 아직 바닥에 있어야 하는가. IsProne이 답을 통째로 든다
        // (기절해 누움 · 줄에 눕혀짐 · 일어나기 대기가 전부 참이고, 기상 모션이 나가는 순간 거짓).
        if (IsRagdollActive)
            return m_driver == null || m_driver.IsProne;

        // 아직 애니메이터가 쥐고 있다 — 새로 태울 이유가 있는지 묻는다.
        // ⚠ IsStunned가 아니라 HasStunOverlay다 — 넉백 착지 KO는 에이전트 소유권이 정면으로 부딪힌다.
        if (!m_owner.Stun.HasStunOverlay)
            return false;

        // 기상 모션이 이미 나간 뒤라면 태우지 않는다 — 일어나는 몸을 다시 눕힌다.
        return m_driver == null || m_driver.IsProne;
    }

    /// <summary>
    /// 래그돌 진입 — <b>멱등</b>. 이미 물리 중이면 임펄스만 누적한다.
    /// ⚠ <b>정착한 시체도 임펄스를 받는다</b> — 다시 날리지 않으려면 호출부가 걸러야 한다 (#768).
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
            return; // 도달 불가 — 상태는 Animated/Ragdoll 둘뿐이다. 정착은 m_settled가 따로 든다

        // ⚠ 애니메이터를 끄기 전에 — 안에서 강제 평가를 한다.
        SnapToAnimatorPoseIfBlending();

        StopAnimator(); // 상태를 바꾸기 전에 — Animated일 때만 도는 멱등 함수다

        m_state = RagdollState.Ragdoll;
        m_settled = false;
        m_settle.Reset();

        // 블렌드 중에 다시 쓰러지면 섞던 것을 버린다 — 안 버리면 무너지는 몸을 애니메이터 자세로
        // 도로 끌어당긴다.
        m_blending = false;

        // 말단 뼈(손·발·손가락)만 바인드로 못박는다 — 전 피어가 각자 부른다. 스트림이 안 싣는 뼈라
        // 그냥 두면 피어마다 애니메이터가 마지막에 놓은 손발 모양이 남는다.
        // ⚠ 몸 모양을 만드는 체인 뼈는 여기서 손대지 않는다 — 그쪽은 스트림이 싣는다(그쪽 주석의 사고).
        m_rig.BindPose.RestoreUnstreamedRotations();

        ReleaseAgentForRagdoll();

        // ⚠ <b>물리에 넘기기 전</b>이어야 한다 — 뼈가 아직 키네마틱일 때만 벽과 싸우지 않고
        // 자세를 고칠 수 있다. 여기서 접으면 애초에 박힌 채로 출발하지 않는다 (#980 §3-ⓐ).
        TryFoldArmsBeforePhysics();

        ReleaseBonesToPhysics();
        m_rig.ApplyImpulse(impulse);

        m_streamer?.BeginStreaming(); // 권위가 아니면 스스로 무동작이다

        BeginWallFix(); // #980 — 이 회차 동안 벽에 박힌 팔을 감시한다
    }

    /// <summary>
    /// 기상 블렌드 중이면 <b>애니메이터를 한 번 강제로 평가해</b> 그 자세를 물리에 넘긴다.
    /// 블렌드 중이 아니면 무동작.
    ///
    /// <b>블렌드 중간 자세는 물리에 넘기면 안 된다.</b> 블렌드는 <b>로컬 회전</b>을 섞으므로
    /// (<see cref="RagdollPoseBlend"/>) 중간 경로가 양 끝점 어느 쪽도 아닌 자세다 — 회전 보간은
    /// 팔다리 <b>위치</b>를 보존하지 않는다. 실측(2026-08-19)으로 블렌드 24프레임 중
    /// <b>13~17프레임(55~70%)에서 발끝이 바닥 19.7cm 아래로 내려간다.</b> 끝점 둘은 관통 0인데
    /// 사이만 파고드는 봉우리다.
    ///
    /// 그 프레임에 몸을 물리에 넘기면 <b>정강이 캡슐이 박힌 채로 출발</b>하고, 겹침 탈출 상한
    /// (0.5m/s, 중력을 빼면 실질 0.3m/s = 스텝당 6mm)으로는 빠져나오지 못한다. 발·발끝에는
    /// 애초에 콜라이더가 없어 그쪽은 영영 안 나온다.
    ///
    /// ⚠ <b>"블렌드를 끝낸다"로는 안 된다.</b> 이 함수가 도는 <c>Update</c> 시점에는 애니메이터가
    /// 아직 이번 프레임을 평가하지 않아 뼈에 <b>지난 프레임의 섞인 자세</b>가 남아 있다
    /// (평가는 Update와 LateUpdate 사이다). <see cref="RagdollPoseBlend.Tick"/>은 "지금 뼈에 있는
    /// 값"을 목표로 삼으므로 t=1로 끝내면 그 섞인 자세를 그대로 확정할 뿐이다. 클립 자세를
    /// <b>직접 받아야</b> 한다.
    ///
    /// ⚠ <b>정착 자세로 되감지 않는다.</b> 물리가 만든 자세라 관통이 없는 것은 맞지만, 기상이
    /// 이미 <c>ServerReattachToNavMesh</c>로 루트를 워프했을 수 있어 그때의 <b>로컬</b> 자세를
    /// 되돌리면 옮겨진 루트 기준으로 몸이 엉뚱한 곳에 놓인다. 클립 자세는 루트 기준으로 authoring된
    /// 것이라 루트가 어디 있든 맞는다.
    ///
    /// <b>전 피어가 부른다</b> — 원격도 이 자세를 첫 패킷 전까지 붙들므로
    /// (<see cref="TickHoldPoseUntilStream"/>) 같은 자세여야 한다.
    /// </summary>
    private void SnapToAnimatorPoseIfBlending()
    {
        if (!m_blending || m_animator == null || !m_animator.enabled)
            return;

        m_animator.Update(0f); // 시간을 진행시키지 않고 지금 클립 자세만 뼈에 쓴다
    }

    /// <summary>
    /// 애니메이터에게 몸을 돌려준다 — 기절에서 깨어나는 유일한 문. 이미 <c>Animated</c>면 무동작.
    /// 뼈·애니메이터는 전 피어가 되돌리고, NavMesh 재부착은 권위 피어만 한다.
    /// </summary>
    private void ExitRagdoll()
    {
        if (m_state == RagdollState.Animated)
            return;

        // 아무것도 보내지 않고 끊는다 — 기상에는 종착 자세가 없다(보내면 원격에서 기상 블렌드와
        // 래그돌 자세가 같은 프레임을 두고 싸운다). 전 피어가 각자 부르므로 RPC가 필요 없다.
        m_streamer?.StopStreaming();

        EndWallFix(); // #980 — 감시를 닫고, 접다 만 팔을 물리로 돌려준다

        // 키네마틱이 먼저다 — 동적인 채로 포즈를 쓰면 다음 물리 스텝이 PhysX 결과로 덮는다.
        m_rig.SetKinematic(true);

        // ⚠ 블렌드 출발점은 지금 이 래그돌 자세다 — 아래 두 줄보다 반드시 먼저 잡는다.
        bool blending = m_blendSeconds > 0f && m_blend != null && m_blend.IsValid;
        if (blending)
            m_blend.Begin();

        // ⚠ 뼈 길이를 되돌린다 — 애니메이터는 회전만 쓰므로 물리가 늘려 놓은 localPosition을 고쳐
        // 주지 않는다. 안 되돌리면 기절할 때마다 누적되다 사지가 늘어나며 바닥을 뚫는다.
        m_rig.BindPose.RestoreAll();

        if (m_animator != null)
            m_animator.enabled = true;

        m_rig.Skins.SetAlwaysVisible(false);

        m_state = RagdollState.Animated;
        m_blending = blending;
        m_settled = false;
        m_settle.Reset();

        if (HasMoveAuthority)
            ServerReattachToNavMesh();
    }

    // 뼈를 물리로 놓아준다 — 단, 원격에서는 놓아주지 않는다. 원격은 자세를 받아 입히기만 하므로
    // 물리가 아예 돌지 않고, 그래서 위반될 관절이 원리적으로 없다.
    private void ReleaseBonesToPhysics()
    {
        if (!HasMoveAuthority)
        {
            m_rig.SetKinematic(true);
            m_rig.CapturePose(); // 첫 패킷이 오기 전까지 붙들 자세 (TickHoldPoseUntilStream)
            return;
        }

        m_rig.SetKinematic(false);
    }

    // 애니메이터를 떼어낸다 — ⚠ 뼈를 물리에 넘기기 전에 반드시. 켜 둔 채 넘기면 다음 프레임에
    // 대기 포즈가 시체를 덮어써 죽은 몸이 서 있게 된다. 이미 꺼져 있으면 무동작.
    private void StopAnimator()
    {
        if (m_state != RagdollState.Animated)
            return;

        if (m_animator != null)
            m_animator.enabled = false;

        m_rig.Skins.SetAlwaysVisible(true);
    }

    // ---- 정착 / 기상 ----
    //
    // <b>정착은 상태 전이가 아니다.</b> 뼈는 처음부터 끝까지 동적으로 남고, 여기서 하는 일은
    // "물리가 잠들었다"를 적어 두고 스트림을 끊는 것뿐이다. 그래서 정착은 <b>화면을 바꾸지 않는다</b>
    // — 그것이 사양이다.

    /// <summary>
    /// 물리가 잠들었다 — 서버 전용. 스트림을 끊고 마지막 자세를 한 번 더 보낸다.
    ///
    /// <b>몸을 건드리지 않는다.</b> 키네마틱 전환도, 뼈 길이 복원도, 지면 재정렬도 없다 — 잠든
    /// 몸은 이미 물리가 놓은 자리에 있고 원격은 이미 그 자세를 그리고 있다. 옛 구조가 이 자리에서
    /// 하던 일들(<c>SetKinematic</c>·뼈 길이 복원·최저뼈 재정렬)은 전부
    /// <b>키네마틱 얼림이 만든 문제를 되받는 것</b>이었고, 얼림이 없으니 함께 사라졌다.
    /// 근거는 docs/npc-ragdoll.md §4.
    /// </summary>
    private void ServerSettleInPlace()
    {
        if (!HasMoveAuthority || m_settled)
            return;

        StopAnimator(); // 무너지지 않은 몸을 그대로 재우는 경로(유치장 배치)가 있다

        // 그 경로는 EnterRagdoll을 안 지나므로 말단을 여기서 한 번 더 못박는다. 이미 못박혀 있으면
        // 무동작이다. ⚠ 말단은 리지드바디가 없어 물리가 덮지 않으므로 이 대입은 남는다.
        m_rig.BindPose.RestoreUnstreamedRotations();

        m_settled = true;

        // 스트림을 끊고 마지막 자세를 한 번 더 보낸다 — 이것이 원격의 종착 상태다.
        // ⚠ <b>좌표계는 바뀌지 않는다 — 스트리밍과 같은 월드다.</b> 그래서 원격은 이 패킷을 받아도
        // 화면이 변하지 않는다. 옛 구조는 여기서 로컬로 갈아타 몸을 루트에 매달았고, 그 전환이
        // 정착 순간의 점프였다.
        m_streamer?.EndStreaming();
    }

    // 잠든 몸이 다시 움직이기 시작했다 — 서버 전용. 밟힘·폭발·밧줄 어느 쪽이든 여기로 모인다.
    // 깨우는 쪽은 물리가 이미 했으므로(<c>WakeUp</c>) 여기서는 스트림만 되살린다.
    private void ServerResumeFromSleep()
    {
        m_settled = false;
        m_settle.Reset();
        m_streamer?.ResumeStreaming();
    }

    /// <summary>
    /// 이 시체의 뼈가 <paramref name="others"/>와 충돌하지 않게 한다 — 치인 차와의 접촉을 끊는다.
    /// 근거·되돌리지 않는 이유는 <see cref="PlayerRagdoll.IgnoreCollisionWith"/>와 같다.
    /// </summary>
    public void IgnoreCollisionWith(Collider[] others, bool ignore)
    {
        if (others == null || m_rig == null || !m_rig.IsValid)
            return;

        for (int i = 0; i < others.Length; i++)
            m_rig.IgnoreCollisionWith(others[i], ignore);
    }

    /// <summary>
    /// 잠든 시체를 깨운다 — <b>멱등</b>. 밧줄을 묶는 쪽이 부른다.
    ///
    /// 관절 장력만으로는 잠든 몸이 안 깨어날 수 있어 명시적으로 깨운다. 예전의 <c>Unfreeze</c>와
    /// 달리 <b>상태를 바꾸지 않는다</b> — 뼈는 애초에 키네마틱이 된 적이 없으므로 되돌릴 것이 없다.
    /// </summary>
    public void WakeCorpse()
    {
        if (m_state != RagdollState.Ragdoll || !HasMoveAuthority)
            return;

        m_rig.WakeAll();
        ServerResumeFromSleep();
    }

    // 원격이 정착 자세를 받았다 — 자세는 스트리머가 이미 입혔으므로 여기서는 배선만 맞춘다.
    // StopAnimator가 먼저인 것은 도착 순서 때문이다(사망 폴링보다 이 패킷이 먼저 온 피어가 있다).
    //
    // ⚠ <b>뼈를 건드리지 않는다.</b> 원격의 뼈는 진입 때부터 키네마틱이고 스트리머가 정착 뒤에도
    // 매 프레임 자세를 못박으므로, 여기서 얼릴 것도 되돌릴 것도 없다.
    private void HandleSettledPoseReceived()
    {
        StopAnimator();

        // 권위 쪽 ServerSettleInPlace와 짝 — 이 피어가 EnterRagdoll을 안 지났어도 말단을 맞춘다.
        m_rig.BindPose.RestoreUnstreamedRotations();

        // 도착 순서가 뒤집힌 피어를 위한 보정 — 사망 폴링이 아직 안 왔으면 상태가 Animated다.
        m_state = RagdollState.Ragdoll;
        m_settled = true;
    }

    // ---- 밧줄 파사드 ----
    //
    // 실물은 RagdollRope가 쥔다. 파사드를 두는 이유는 호출부(NpcRopeDrag)가 "래그돌인 대상에게
    // 밧줄을 묶는다"를 표현하기 때문이다 — 직접 찾게 하면 배치 지식이 호출부로 샌다.

    /// <summary>관절 밧줄의 길이(m) — 리그가 아직 안 잡혔으면 0.</summary>
    public float RopeLength => m_rope != null ? m_rope.Length : 0f;

    /// <summary>
    /// 시체에 밧줄을 묶는다. 잠들어 있으면 먼저 깨운다 — 관절 장력만으로는 안 깨어날 수 있다.
    ///
    /// ⚠ <b>원격에는 아무것도 할 것이 없다.</b> 예전에는 여기서 전 피어가 <c>Unfreeze</c>를 불러
    /// 원격을 <c>Frozen</c>에서 꺼내야 했고, 그래서 권위 가드가 그 뒤에 있었다. 지금 원격의 몸은
    /// 스트림이 쥐고 있을 뿐이고 새 스냅샷이 오면 그대로 따라가므로 꺼낼 상태가 없다.
    /// </summary>
    /// <param name="carrier">밧줄을 쥔 쪽. 보통 운반자의 손 앵커.</param>
    public void BeginRopePull(Transform carrier)
    {
        if (!HasMoveAuthority)
            return;

        WakeCorpse();
        m_rope?.Attach(carrier);
    }

    /// <summary>이 사람이 쥔 가닥만 푼다 — 줄다리기에서 한 명이 손을 뗄 때. <b>멱등</b>.</summary>
    public void EndRopePull(Transform carrier)
    {
        m_rope?.Detach(carrier);
    }

    /// <summary>걸린 밧줄을 전부 푼다 — 내려놓기·줄 끊김·운반자 소실. <b>멱등</b>.
    /// 재우지 않는다 — 놓은 몸은 마저 무너져야 하고, 다 무너지면 물리가 알아서 잠든다.</summary>
    public void EndRopePull()
    {
        m_rope?.Detach();
    }

    // ---- 배치 (유치장 수감 / 퇴장) ----

    /// <summary>
    /// 시체를 통째로 옮긴다 — 서버 전용. 부르는 곳은 <see cref="NpcCustody.SendCorpseToJail"/> 하나다.
    ///
    /// <b>루트와 뼈를 같은 델타로 따로 옮긴다.</b> 뼈는 동적이라 루트를 따라오지 않으므로 저절로
    /// 딸려오는 것이 없다 — 대신 <see cref="RagdollRig.TranslateBy"/>가 전 뼈를 <b>한 델타로</b>
    /// 옮기므로 관절 위반이 0이고, 도착지에서 솔버가 메울 것이 없다.
    ///
    /// ⚠ 예전에는 얼린 뼈가 루트의 키네마틱 자식이라 루트 한 줄로 왔고, 그래서 <b>도착지에서
    /// 녹이면 시체가 발사됐다</b>(실측 264·417 m/s — docs/npc-ragdoll.md §5). 그 사고의 원인은
    /// 얼린 동안 벌어진 관절 위반이었고, 여기서는 위반이 생기지 않으므로 녹인 채로 끝낸다.
    /// <b>도착지에서 다시 무너져 잠드는 것이 사양이다</b> — 배치점이 어긋나도 몸이 알아서 눕는다.
    /// </summary>
    /// <param name="position">시체가 놓일 지면 지점 — 루트(발밑) 기준이다.</param>
    public void ServerPlaceCorpse(Vector3 position)
    {
        if (m_rig == null || !m_rig.IsValid)
        {
            transform.position = position;
            return;
        }

        // ⚠ 줄을 먼저 끊는다 — 묶인 채 수백 m 옮기면 관절이 그만큼 위반되고 솔버가 그것을 메우며
        // 시체를 발사한다(실측 237 m/s). 호출부가 무엇을 놓쳤든 상관없게 여기서 한 번 더 보장한다.
        EndRopePull();

        // ⚠ 아직 애니메이터가 쥐고 있으면 먼저 물리로 넘긴다 — <b>뼈가 키네마틱인 채로 아래
        // <see cref="RagdollRig.TranslateBy"/>를 부르면 두 번 옮겨진다.</b> 키네마틱 뼈는 루트의
        // 자식으로 딸려오므로 루트 대입만으로 이미 델타가 실리고, 거기에 한 번 더 얹히기 때문이다.
        //
        // 사망 폴링보다 배치가 먼저 도착한 프레임에서만 지나는 길이다(옛 구조에서 이 자리가
        // <c>if (!IsFrozen) ServerFreezeInPlace()</c>였던 것과 같은 이유).
        if (m_state != RagdollState.Ragdoll)
            EnterRagdoll(Vector3.zero);

        Vector3 delta = position - transform.position;

        transform.position = position;

        // ⚠ <b>두 대입이 서로 더해지지 않는 것은 <c>Physics.autoSyncTransforms = 0</c>이기 때문이다.</b>
        // 위의 루트 대입은 다음 물리 스텝까지 물리 포즈에 닿지 않으므로, 여기서 읽는 <c>rb.position</c>은
        // 아직 옮기기 전 값이고 델타가 한 번만 실린다. 그 설정을 켜면 뼈가 <b>두 배로</b> 날아간다.
        m_rig.TranslateBy(delta);

        ServerTeleportNetTransforms();

        // ⚠ <b>자세를 다시 쏜다 — 안 쏘면 원격의 몸이 죽은 자리에 남는다.</b>
        //
        // 원격은 정착 뒤에도 마지막 스냅샷의 <b>월드</b> 골반으로 매 프레임 몸을 못박는다
        // (<see cref="RagdollPoseStreamer.EndStreaming"/>). 그래서 루트만 옮기면 이름표와
        // 콜라이더만 유치장으로 가고 몸은 그대로다.
        //
        // 보간 없이 나가야 한다 — 평범한 스냅샷으로 보내면 원격이 출발지와 도착지 사이를
        // 보간하며 시체가 맵을 가로질러 날아간다(실측 573.94m을 21프레임).
        m_streamer?.SendTeleportPose();

        // 옮긴 몸은 깨어난 것으로 본다 — 도착지에서 다시 무너져 잠드는 과정이 원격에도 흘러야 한다.
        // 이미 잠들어 있었다면 다음 Update가 곧바로 다시 재우고 종착 패킷을 한 번 더 보낸다.
        m_rig.WakeAll();
        ServerResumeFromSleep();
    }

    /// <summary>래그돌인 몸을 통째로 옮긴다 — 수단은 <see cref="ServerPlaceCorpse"/>와 같고, 산 채로
    /// 래그돌인 신병(유치장 배치)이 쓴다. 서버 전용. (#866)</summary>
    /// <returns>옮겼으면 참 — 애니메이터가 쥔 몸이면 거짓(그쪽은 뼈가 루트를 따라온다).</returns>
    public bool ServerPlaceRagdollBody(Vector3 position)
    {
        if (!HasMoveAuthority || m_state != RagdollState.Ragdoll)
            return false;

        ServerPlaceCorpse(position);
        return true;
    }

    // 순간이동을 보간 없이 원격에 보낸다 — 안 쓰면 원격이 이 거리를 여러 프레임에 걸쳐 보간한다
    // (실측 573.94m을 21프레임). 보내는 것은 루트 하나뿐이고 뼈는 계층으로 딸려 온다.
    private void ServerTeleportNetTransforms()
    {
        if (m_owner == null || !m_owner.IsSpawned)
            return; // 오프라인 Play에서는 위의 대입이 곧 이동의 전부다

        if (m_rootNetTransform != null)
            m_rootNetTransform.Teleport(transform.position, transform.rotation, transform.localScale);
    }

    // ---- NavMeshAgent ----

    /// <summary>
    /// 에이전트에게서 몸을 넘겨받는다 — 눕기 전에 반드시.
    /// ⚠ 기절은 <b>끄지 않고 손만 뗀다</b>(통째로 끄면 그 사이 상태 전이가 예외로 깨진다).
    /// 사망은 반대로 통째로 끈다 — 시체는 NavMesh로 돌아가지 않는다.
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

        m_agent.updatePosition = false;
        m_agent.updateRotation = false;
    }

    /// <summary>
    /// 깨어난 몸을 NavMesh에 다시 붙인다 — 서버 전용. 규칙은 "뗀 쪽이 되돌린다"라
    /// <see cref="ReleaseAgentForRagdoll"/>의 짝이 여기다.
    /// </summary>
    private void ServerReattachToNavMesh()
    {
        if (m_agent == null)
            return;

        // ⚠ 플래그는 무조건 되돌린다 — 아래 가드보다 먼저다. 떼어 둔 채 넘기면 그 NPC는 영영 걷지 못한다.
        m_agent.updatePosition = true;
        m_agent.updateRotation = true;

        // 위치는 다른 구간이 쥐고 있으면 손대지 않는다 — 그쪽이 끝날 때 자기 자리에서 붙인다.
        if (m_owner.Rope.IsRoped || m_owner.Knockback.IsKnockedBack)
            return;

        // ⚠ 샘플에 실패해도 켠다 — 굳은 몸 정리가 enabled == false인 구간을 건너뛴다.
        m_agent.enabled = true;

        // 기준 마스크로 착지점을 찾는다 — 현재 통행 마스크는 배회 중 도로가 빠져 있어 차도 위에
        // 쓰러진 몸이 깨어날 자리를 못 찾는다. 못 가는 영역(Jail)에 Warp되면 경로가 안 잡혀 고착된다.
        if (NavMesh.SamplePosition(
                transform.position,
                out NavMeshHit ground,
                k_navMeshSampleDistance,
                m_owner.BaseAreaMask
            ))
        {
            m_agent.Warp(ground.position);
        }
        else
        {
            // 붙일 자리가 없어도 에이전트의 내부 위치는 몸에 맞춘다 (#913) — 안 맞추면 에이전트가
            // 쓰러지기 전 자리를 그대로 쥐고 있다가 updatePosition으로 몸을 거기로 끌어다 놓는다.
            m_agent.Warp(transform.position);
        }

        if (!m_agent.isOnNavMesh)
        {
            Debug.LogWarning(
                "NpcRagdoll: 기절에서 깨어난 자리를 NavMesh에 붙이지 못했다 — 행방불명 처리 대기: "
                    + $"{name} @{transform.position.ToString("F1")}",
                this
            );
        }
    }

    // ---- 지면 탐색 ----

    private bool HasGroundUnderHips() => TryGroundUnder(m_rig.Hips.position, out _);

    // 골반 밑 지면 탐색 — 정착 자격 판정과 정착 정렬이 <b>같은 것</b>을 써야 한다(다르면 그 차이가
    // 얼리는 순간 낙차로 남는다). 탐색 거리를 짧게 잡을 것 — 근거는 m_groundProbeDistance 툴팁.
    private bool TryGroundUnder(Vector3 hipsPosition, out Vector3 point) =>
        RagdollGround.TryGroundUnder(hipsPosition, m_groundProbeDistance, m_groundMask, out point);

}
