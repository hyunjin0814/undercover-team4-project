using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// NPC 래그돌 — 죽거나 기절한 몸의 애니메이터를 끄고 뼈를 물리에 넘기는 표현 계층.
/// 권위 피어만 물리를 굴리고 원격은 RagdollPoseStreamer로 받은 자세를 입힌다. 설계 근거는 docs/npc-ragdoll.md.
/// </summary>
public partial class NpcRagdoll : MonoBehaviour
{
    private const float k_navMeshSampleDistance = 0.75f;

    private enum RagdollState
    {
        Animated,
        Ragdoll,
    }

    [Header("정착 판정")]
    [Tooltip("물리가 스스로 잠들지 않는 몸을 강제로 재우기까지의 시간(초) — 끼어 떠는 경우의 안전장치")]
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
    private RagdollRig m_rig;
    private RagdollRope m_rope;
    private Animator m_animator;
    private NavMeshAgent m_agent;
    private NpcAnimationDriver m_driver;
    private Unity.Netcode.Components.NetworkTransform m_rootNetTransform;
    private RagdollPoseStreamer m_streamer;

    private RagdollPoseBlend m_blend;
    private bool m_blending;

    private RagdollState m_state = RagdollState.Animated;
    private readonly RagdollSettlePolicy m_settle = new RagdollSettlePolicy();

    private System.Func<bool> m_hasGroundUnderHips;

    private bool m_settled;

    private bool m_skipThisEpisode;
    private bool m_polledOnce;

    public bool IsRagdollActive => m_state != RagdollState.Animated;

    private bool HasMoveAuthority => !m_owner.IsSpawned || m_owner.IsServer;

    private void Awake()
    {
        m_hasGroundUnderHips = HasGroundUnderHips;

        m_owner = GetComponent<NpcController>();
        m_agent = GetComponent<NavMeshAgent>();
        m_driver = GetComponent<NpcAnimationDriver>();
        m_rootNetTransform = GetComponent<Unity.Netcode.Components.NetworkTransform>();

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

        m_rig.EnsureCollected();
        m_rope = m_rig.GetComponent<RagdollRope>();
        SetupWallFix();
        m_animator = GetComponentInChildren<Animator>(true);

        m_blend = new RagdollPoseBlend(m_rig.BoneRoot);

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

    /// <summary>물리 스텝마다 루트가 래그돌 몸을 따라가게 한다. 정착한 몸은 건너뛴다.</summary>
    private void FixedUpdate()
    {
        if (m_state != RagdollState.Ragdoll || !HasMoveAuthority)
            return;

        if (!m_settled && !IsWallFixBusy)
            TickRootFollow();

        TickWallFix();
    }

    private void Update()
    {
        PollRagdollTriggers();

        if (m_state != RagdollState.Ragdoll)
            return;

        if (!HasMoveAuthority)
            return;

        if (m_settled)
        {
            if (!m_rig.AllAsleep)
                ServerResumeFromSleep();

            return;
        }

        bool carried = m_rope != null && m_rope.IsBeingCarried;

        switch (m_settle.Tick(m_rig.AllAsleep, carried, m_settleTimeoutSeconds, m_hasGroundUnderHips))
        {
            case ERagdollSettleStep.Settle:
                ServerSettleInPlace();
                break;

            case ERagdollSettleStep.ForceSleepThenSettle:
                m_rig.SleepAll();
                ServerSettleInPlace();
                break;
        }
    }

    private void LateUpdate()
    {
        if (m_blending && m_blend.Tick(m_blendSeconds))
            m_blending = false;

        TickHoldPoseUntilStream();
    }

    /// <summary>원격에서 첫 자세 패킷이 오기 전까지 진입 시점의 월드 자세를 붙든다.</summary>
    private void TickHoldPoseUntilStream()
    {
        if (m_streamer == null || HasMoveAuthority || m_state != RagdollState.Ragdoll)
            return;

        if (!m_streamer.IsAwaitingFirstPose)
            return;

        m_rig.RestoreCapturedPose();
    }

    /// <summary>루트를 시체 골반 밑으로 끌고 간다. 서버 전용.</summary>
    private void TickRootFollow()
    {
        if (!HasMoveAuthority || m_rig.Hips == null)
            return;

        m_rig.CapturePose();

        Vector3 target = m_rig.Hips.position;

        if (TryGroundUnder(target, out Vector3 ground) && target.y - ground.y <= RagdollGround.k_groundedHipsHeight)
            target.y = ground.y;

        transform.position = target;

        m_rig.RestoreCapturedPose();
    }

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
            m_skipThisEpisode = false;
            ExitRagdoll();
            return;
        }

        if (!m_skipThisEpisode && m_state == RagdollState.Animated)
            EnterRagdoll(Vector3.zero);
    }

    /// <summary>지금 이 몸이 래그돌이어야 하는지(진입·유지 조건) 판정한다.</summary>
    private bool WantsRagdoll()
    {
        if (m_owner.Death.IsDead)
            return true;

        if (IsRagdollActive)
            return m_driver == null || m_driver.IsProne;

        if (!m_owner.Stun.HasStunOverlay)
            return false;

        return m_driver == null || m_driver.IsProne;
    }

    /// <summary>래그돌에 진입한다(멱등). 이미 물리 중이면 임펄스만 더한다.</summary>
    public void EnterRagdoll(Vector3 impulse)
    {
        if (m_rig == null || !m_rig.IsValid)
            return;

        if (m_state == RagdollState.Ragdoll)
        {
            m_rig.ApplyImpulse(impulse);
            return;
        }

        if (m_state != RagdollState.Animated)
            return;

        SnapToAnimatorPoseIfBlending();

        StopAnimator();

        m_state = RagdollState.Ragdoll;
        m_settled = false;
        m_settle.Reset();

        m_blending = false;

        m_rig.BindPose.RestoreUnstreamedRotations();

        ReleaseAgentForRagdoll();

        TryFoldArmsBeforePhysics();

        ReleaseBonesToPhysics();
        m_rig.ApplyImpulse(impulse);

        m_streamer?.BeginStreaming();

        BeginWallFix();
    }

    /// <summary>기상 블렌드 중이면 애니메이터를 강제 평가해 클립 자세를 물리에 넘긴다.</summary>
    private void SnapToAnimatorPoseIfBlending()
    {
        if (!m_blending || m_animator == null || !m_animator.enabled)
            return;

        m_animator.Update(0f);
    }

    /// <summary>애니메이터에 몸을 돌려준다. 권위 피어는 NavMesh 재부착도 한다.</summary>
    private void ExitRagdoll()
    {
        if (m_state == RagdollState.Animated)
            return;

        m_streamer?.StopStreaming();

        EndWallFix();

        m_rig.SetKinematic(true);

        bool blending = m_blendSeconds > 0f && m_blend != null && m_blend.IsValid;
        if (blending)
            m_blend.Begin();

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

    private void ReleaseBonesToPhysics()
    {
        if (!HasMoveAuthority)
        {
            m_rig.SetKinematic(true);
            m_rig.CapturePose();
            return;
        }

        m_rig.SetKinematic(false);
    }

    private void StopAnimator()
    {
        if (m_state != RagdollState.Animated)
            return;

        if (m_animator != null)
            m_animator.enabled = false;

        m_rig.Skins.SetAlwaysVisible(true);
    }

    /// <summary>물리가 잠들면 스트림을 끊고 마지막 자세를 보낸다(몸은 건드리지 않는다). 서버 전용.</summary>
    private void ServerSettleInPlace()
    {
        if (!HasMoveAuthority || m_settled)
            return;

        StopAnimator();

        m_rig.BindPose.RestoreUnstreamedRotations();

        m_settled = true;

        m_streamer?.EndStreaming();
    }

    private void ServerResumeFromSleep()
    {
        m_settled = false;
        m_settle.Reset();
        m_streamer?.ResumeStreaming();
    }

    /// <summary>이 시체의 뼈와 others의 충돌을 켜고 끈다(치인 차 접촉 차단).</summary>
    public void IgnoreCollisionWith(Collider[] others, bool ignore)
    {
        if (others == null || m_rig == null || !m_rig.IsValid)
            return;

        for (int i = 0; i < others.Length; i++)
            m_rig.IgnoreCollisionWith(others[i], ignore);
    }

    /// <summary>잠든 시체를 깨운다(멱등).</summary>
    public void WakeCorpse()
    {
        if (m_state != RagdollState.Ragdoll || !HasMoveAuthority)
            return;

        m_rig.WakeAll();
        ServerResumeFromSleep();
    }

    private void HandleSettledPoseReceived()
    {
        StopAnimator();

        m_rig.BindPose.RestoreUnstreamedRotations();

        m_state = RagdollState.Ragdoll;
        m_settled = true;
    }

    public float RopeLength => m_rope != null ? m_rope.Length : 0f;

    /// <summary>시체에 밧줄을 묶고, 잠들어 있으면 먼저 깨운다.</summary>
    public void BeginRopePull(Transform carrier)
    {
        if (!HasMoveAuthority)
            return;

        WakeCorpse();
        m_rope?.Attach(carrier);
    }

    /// <summary>이 사람이 쥔 가닥만 푼다 — 줄다리기에서 한 명이 손을 뗄 때. 멱등.</summary>
    public void EndRopePull(Transform carrier)
    {
        m_rope?.Detach(carrier);
    }

    /// <summary>걸린 밧줄을 전부 푼다(멱등).</summary>
    public void EndRopePull()
    {
        m_rope?.Detach();
    }

    /// <summary>시체의 루트와 뼈를 같은 델타로 옮긴다. 서버 전용.</summary>
    public void ServerPlaceCorpse(Vector3 position)
    {
        if (m_rig == null || !m_rig.IsValid)
        {
            transform.position = position;
            return;
        }

        EndRopePull();

        if (m_state != RagdollState.Ragdoll)
            EnterRagdoll(Vector3.zero);

        Vector3 delta = position - transform.position;

        transform.position = position;

        m_rig.TranslateBy(delta);

        ServerTeleportNetTransforms();

        m_streamer?.SendTeleportPose();

        m_rig.WakeAll();
        ServerResumeFromSleep();
    }

    /// <summary>래그돌 상태인 산 몸을 통째로 옮긴다. 서버 전용.</summary>
    public bool ServerPlaceRagdollBody(Vector3 position)
    {
        if (!HasMoveAuthority || m_state != RagdollState.Ragdoll)
            return false;

        ServerPlaceCorpse(position);
        return true;
    }

    private void ServerTeleportNetTransforms()
    {
        if (m_owner == null || !m_owner.IsSpawned)
            return;

        if (m_rootNetTransform != null)
            m_rootNetTransform.Teleport(transform.position, transform.rotation, transform.localScale);
    }

    /// <summary>래그돌 진입 전 NavMeshAgent에게서 몸을 넘겨받는다(기절은 정지, 사망은 비활성).</summary>
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

    /// <summary>깨어난 몸을 NavMesh에 다시 붙인다. 서버 전용.</summary>
    private void ServerReattachToNavMesh()
    {
        if (m_agent == null)
            return;

        m_agent.updatePosition = true;
        m_agent.updateRotation = true;

        if (m_owner.Rope.IsRoped || m_owner.Knockback.IsKnockedBack)
            return;

        m_agent.enabled = true;

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

    private bool HasGroundUnderHips() => TryGroundUnder(m_rig.Hips.position, out _);

    private bool TryGroundUnder(Vector3 hipsPosition, out Vector3 point) =>
        RagdollGround.TryGroundUnder(hipsPosition, m_groundProbeDistance, m_groundMask, out point);
}
