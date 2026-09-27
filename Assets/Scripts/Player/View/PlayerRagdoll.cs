using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 래그돌 — 사망·비행 동안 애니메이터를 끄고 뼈를 물리에 넘긴 뒤, 정착하면 애니메이터로 되돌린다.
/// 캡슐 대리값·오너 권한 루트·기상 블렌드 등 플레이어 고유 처리를 맡는다. 설계 근거는 docs/player-ragdoll.md.
/// </summary>
public partial class PlayerRagdoll : MonoBehaviour
{
    private const float k_causeSyncGraceSeconds = 1f;

    private const float k_ropeReattachRange = 5f;

    private static readonly int s_groundStateHash = Animator.StringToHash("Knockdown_Ground");

    private enum RagdollState
    {
        Animated,
        Ragdoll,
        BlendingToAnimator,
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

    [Tooltip("루트 yaw를 몸이 누운 방향에 맞춘다(기상 모션 방향 정렬용)")]
    [SerializeField] private bool m_alignRootYawToBody = true;

    [Tooltip("루트 yaw 추종 감쇠율(1/초). 0이면 즉시 대입한다(이름표·아이템이 튈 수 있다)")]
    [SerializeField] private float m_rootYawFollowSpeed = 8f;

    [Tooltip("몸 방향 대비 루트 yaw 보정(도) — Knockdown_StandUp 클립이 어느 쪽을 머리로 보는지에 " +
             "맞춘다. 아래 m_logRevivalYaw로 실측해 넣는 값이다")]
    [SerializeField] private float m_rootYawOffset;

    [Header("애니메이터 복귀")]
    [Tooltip("정착 포즈 → 애니메이터 포즈 보간 시간(초)")]
    [SerializeField] private float m_blendSeconds = 0.4f;

    private RagdollRig m_rig;
    private bool m_lostBodyHidden;
    private readonly List<Renderer> m_hiddenLostBodyRenderers = new List<Renderer>();
    private RagdollRope m_rope;

    private readonly List<Transform> m_ropeCarriers = new List<Transform>();
    private RagdollPoseBlend m_blend;

    private RagdollPoseStreamer m_streamer;

    private bool m_holdPoseUntilStream;

    private Animator m_animator;
    private CharacterController m_controller;

    private bool m_beamedHold;
    private Vector3 m_beamedLastRoot;
    private PlayerIncapacitation m_incapacitation;
    private PlayerMovement m_movement;
    private NetworkObject m_netObject;

    private Transform m_root;

    private RagdollState m_state = RagdollState.Animated;

    private bool m_settled;
    private readonly RagdollSettlePolicy m_settle = new RagdollSettlePolicy();

    private System.Func<bool> m_hasGroundUnderHips;

    private bool m_skipThisEpisode;
    private bool m_polledOnce;

    private bool m_sawCauseThisEpisode;
    private float m_awaitingCauseSeconds;

    private bool m_hadMoveAuthority;

    private bool m_yawFollowDone;

    public bool IsRagdollActive => m_state != RagdollState.Animated;

    private bool IsBeamed => m_incapacitation != null && m_incapacitation.IsBeamed;

    private bool IsBodyLost => m_incapacitation != null && m_incapacitation.IsBodyLost;

    private bool IsBeingRevived => m_incapacitation != null && m_incapacitation.IsBeingRevived;

    private bool WantsRagdoll => m_incapacitation != null && m_incapacitation.IsRagdollCause;

    internal bool IsCapsuleFollowingBody => m_state == RagdollState.Ragdoll && !IsBeamed;

    internal bool IsSettled => m_settled;

    private bool HasMoveAuthority =>
        m_netObject == null || !m_netObject.IsSpawned || m_netObject.IsOwner;

    private void Awake()
    {
        m_hasGroundUnderHips = HasGroundUnderHips;

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

        m_rig.EnsureCollected();

        m_rope = m_rig.GetComponent<RagdollRope>();

        m_blend = new RagdollPoseBlend(m_rig.BoneRoot);

        m_animator = GetComponentInChildren<Animator>();
        m_controller = GetComponentInParent<CharacterController>();
        m_incapacitation = GetComponentInParent<PlayerIncapacitation>();
        m_movement = GetComponentInParent<PlayerMovement>();
        m_netObject = GetComponentInParent<NetworkObject>();

        m_root = m_controller != null ? m_controller.transform : transform;

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
            m_streamer.OnSettledPoseReceived += HandleSettledPoseReceived;
        }

        ReapplyCapsuleIgnore();
    }

    /// <summary>애니메이터를 끄고 리그를 물리에 넘긴다.</summary>
    private void EnterRagdollPose()
    {
        if (m_animator != null)
            m_animator.enabled = false;

        m_rig.Skins.SetAlwaysVisible(true);

        BeginEntryTrace();

        ReleaseBonesToPhysics();
    }

    /// <summary>리그를 애니메이터에 돌려줄 준비를 한다(뼈 길이 복원 포함). 애니메이터 활성화는 호출부가 한다.</summary>
    private void ExitRagdollPose()
    {
        m_rig.Skins.SetAlwaysVisible(false);
        m_rig.BindPose.RestoreAll();
    }

    /// <summary>뼈를 물리로 놓아준다. 스트림을 받는 피어는 키네마틱으로 두고 첫 패킷 전 자세를 잡아 둔다.</summary>
    private void ReleaseBonesToPhysics()
    {
        m_hadMoveAuthority = HasMoveAuthority;

        if (m_streamer != null && !HasMoveAuthority)
        {
            m_rig.SetKinematic(true);

            m_rig.CapturePose();
            m_holdPoseUntilStream = true;
            return;
        }

        m_rig.SetKinematic(false);

        if (m_settled)
            m_rig.SleepAll();
    }

    /// <summary>원격이 정착 자세를 받았을 때 정착 깃발만 세운다.</summary>
    private void HandleSettledPoseReceived()
    {
        if (m_state == RagdollState.Animated)
            return;

        m_holdPoseUntilStream = false;
        m_settled = true;
        DumpSettleTrace();
    }

    private void OnDestroy()
    {
        if (m_streamer != null)
            m_streamer.OnSettledPoseReceived -= HandleSettledPoseReceived;
    }

    /// <summary>이 몸의 뼈와 others의 충돌을 켜고 끈다(치인 차 몸통용). 전 피어가 각자 호출한다.</summary>
    public void IgnoreCollisionWith(Collider[] others, bool ignore)
    {
        if (others == null || m_rig == null || !m_rig.IsValid)
            return;

        for (int i = 0; i < others.Length; i++)
            m_rig.IgnoreCollisionWith(others[i], ignore);
    }

    internal void ReapplyCapsuleIgnore()
    {
        if (m_controller == null)
            return;

        m_rig.IgnoreCollisionWith(m_controller, true);
    }

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

    public float RopeLength => m_rope != null ? m_rope.Length : 0f;

    /// <summary>시체에 밧줄 한 가닥을 묶는다. 권위 피어만 묶는다.</summary>
    public void BeginRopePull(Transform carrier)
    {
        if (carrier != null && !m_ropeCarriers.Contains(carrier))
            m_ropeCarriers.Add(carrier);

        if (!HasMoveAuthority)
            return;

        m_rig.WakeAll();
        if (m_settled)
            ResumeFromSleep();

        m_rope?.Attach(carrier);
    }

    /// <summary>이 참가자의 밧줄 가닥만 풀고 재부착 대상에서도 뺀다(멱등).</summary>
    public void EndRopePull(Transform carrier)
    {
        m_ropeCarriers.Remove(carrier);
        m_rope?.Detach(carrier);
    }

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

    /// <summary>밧줄을 전부 풀고 재부착 대상도 모두 지운다.</summary>
    public void EndRopePull()
    {
        m_ropeCarriers.Clear();
        m_rope?.Detach();
    }

    /// <summary>순간이동으로 끊긴 줄을 참가자가 실제로 가까워지면 각자 다시 맨다. 권위 피어 전용.</summary>
    private void TickRopeReattach()
    {
        if (m_rope == null || m_ropeCarriers.Count == 0)
            return;

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

    /// <summary>순간이동한 루트와 같은 델타로 뼈를 옮긴다. 오너 전용이며 래그돌이 아니면 무동작.</summary>
    public void PlaceBodyBy(Vector3 delta)
    {
        if (!HasMoveAuthority || m_state != RagdollState.Ragdoll)
            return;
        if (m_rig == null || !m_rig.IsValid)
            return;

        m_rope?.Detach();

        m_rig.TranslateBy(delta);

        m_streamer?.SendTeleportPose();

        m_rig.WakeAll();
        if (m_settled)
            ResumeFromSleep();
    }

    /// <summary>래그돌에 진입한다(멱등). 이미 물리 중이면 임펄스만 더한다.</summary>
    public void EnterRagdoll(Vector3 impulse)
    {
        if (m_rig == null || !m_rig.IsValid)
            return;

        if (m_state == RagdollState.Ragdoll)
        {
            if (HasMoveAuthority && m_rig.AnyKinematic)
                ReleaseBonesToPhysics();

            m_rig.ApplyImpulse(impulse);
            return;
        }

        if (m_state != RagdollState.Animated
            && !WantsRagdoll)
        {
            return;
        }

        m_state = RagdollState.Ragdoll;
        m_settled = false;
        m_yawFollowDone = false;
        m_settle.Reset();

        m_movement?.ClearExternalVelocity();

        SetControllerEnabled(false);

        EnterRagdollPose();
        m_rig.ApplyImpulse(impulse);

        m_streamer?.BeginStreaming();
    }

    /// <summary>애니메이터로 되돌린다. blend면 기상 자세로 보간하고, 아니면 즉시 되돌린다.</summary>
    private void ExitToAnimator(bool blend)
    {
        if (m_state == RagdollState.Animated || m_rig == null || !m_rig.IsValid)
            return;

        ShowLostBody();

        m_settled = false;
        m_yawFollowDone = false;
        m_sawCauseThisEpisode = false;
        m_awaitingCauseSeconds = 0f;

        m_streamer?.StopStreaming();
        m_holdPoseUntilStream = false;

        EndRopePull();

        bool haveCorpseYaw = m_rig.TryGetBodyYaw(out float corpseYaw);

        m_rig.SetKinematic(true);

        bool blending =
            blend
            && m_animator != null
            && m_blend != null
            && m_blend.IsValid
            && m_state != RagdollState.BlendingToAnimator;

        if (blending)
            m_blend.Begin();

        ExitRagdollPose();

        SetControllerEnabled(true);
        m_movement?.ClearExternalVelocity();

        if (m_animator != null)
            m_animator.enabled = true;

        if (!blending)
        {
            m_state = RagdollState.Animated;
            return;
        }

        bool haveLiveBefore = TryLiveBodyYaw(out float liveBefore);

        m_animator.Play(s_groundStateHash, 0, 0f);
        m_animator.Update(0f);

        if (m_logRevivalYaw)
            LogRevivalYaw(haveCorpseYaw, corpseYaw, haveLiveBefore, liveBefore);

        m_state = RagdollState.BlendingToAnimator;
    }

    /// <summary>씬 진입 재배치를 위해 래그돌을 즉시 끝낸다.</summary>
    public void ExitForReposition()
    {
        ExitToAnimator(blend: false);
        m_skipThisEpisode = true;
    }

    /// <summary>물리 스텝마다 캡슐이 래그돌 몸을 따라가게 한다.</summary>
    private void FixedUpdate()
    {
        if (m_state != RagdollState.Ragdoll || !HasMoveAuthority)
        {
            ReleaseBeamedHold();
            return;
        }

        if (IsBeamed)
        {
            TickBeamedBodyFollow();
            return;
        }

        ReleaseBeamedHold();
        TickCapsuleFollow();
    }

    /// <summary>UFO 빔 흡입 중 뼈를 키네마틱으로 얼려 루트와 함께 끌어올린다. 권위 피어 전용.</summary>
    private void TickBeamedBodyFollow()
    {
        if (m_rig == null || !m_rig.IsValid || m_root == null)
            return;

        if (!m_beamedHold)
        {
            m_beamedHold = true;

            m_rope?.Detach();
            m_rig.SetKinematic(true);
            m_beamedLastRoot = m_root.position;
        }

        Vector3 delta = m_root.position - m_beamedLastRoot;
        m_beamedLastRoot = m_root.position;

        if (delta != Vector3.zero)
            m_rig.TranslateBy(delta);
    }

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
        SampleEntryBaseline();

        PollRagdollCause();

        TickAuthorityHandover();

        if (m_state != RagdollState.Ragdoll)
            return;

        if (IsBodyLost)
        {
            m_rig.SleepAll();
            HideLostBody();
            Settle();
            return;
        }

        if (!HasMoveAuthority)
            return;

        if (IsBeamed)
            return;

        TickRopeReattach();

        if (m_settled)
        {
            if (!m_rig.AllAsleep)
            {
                if (IsBeingRevived)
                    m_rig.SleepAll();
                else
                    ResumeFromSleep();
            }

            return;
        }

        bool carried = m_rope != null && m_rope.IsBeingCarried;

        switch (m_settle.Tick(m_rig.AllAsleep, carried, m_settleTimeoutSeconds, m_hasGroundUnderHips))
        {
            case ERagdollSettleStep.Settle:
                Settle();
                break;

            case ERagdollSettleStep.ForceSleepThenSettle:
                m_rig.SleepAll();
                Settle();
                break;
        }
    }

    private bool HasGroundUnderHips() => TryGroundUnder(m_rig.Hips.position, out _);

    private void PollRagdollCause()
    {
        if (m_incapacitation == null)
            return;

        bool wantsRagdoll = WantsRagdoll;

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
                ExitToAnimator(blend: true);
            }
            return;
        }

        if (!m_skipThisEpisode && m_state == RagdollState.Animated)
            EnterRagdoll(Vector3.zero);
    }

    private void LateUpdate()
    {
        if (m_state == RagdollState.BlendingToAnimator && m_blend.Tick(m_blendSeconds))
            m_state = RagdollState.Animated;

        TickHoldPoseUntilStream();

        TickSettleTrace();

        TickEntryTrace();
    }

    /// <summary>원격에서 첫 자세 패킷이 오기 전까지 진입 시점의 월드 자세를 붙든다.</summary>
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

    /// <summary>캡슐을 시체 골반 밑으로 끌고 간다. 권위 피어 전용.</summary>
    private void TickCapsuleFollow()
    {
        if (m_movement == null || m_rig == null || m_rig.Hips == null)
            return;

        Vector3 target = m_rig.Hips.position;

        bool haveGround = TryGroundUnder(m_rig.Hips.position, out Vector3 ground);
        bool bodyIsGrounded =
            m_settled
            || (haveGround && m_rig.Hips.position.y - ground.y <= RagdollGround.k_groundedHipsHeight);

        if (haveGround && bodyIsGrounded)
            target.y = ground.y - CapsuleBottomOffset;

        m_rig.CapturePose();

        m_root.position = target;

        if (!m_yawFollowDone)
            FollowBodyYaw();

        m_rig.RestoreCapturedPose();
    }

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

    private bool TryGetRootYaw(out float yaw)
    {
        if (!m_rig.TryGetBodyYaw(out yaw))
            return false;

        yaw += m_rootYawOffset;
        return true;
    }

    /// <summary>래그돌 도중 물리 권위가 바뀐 경우 뼈 상태를 새 권위에 맞춰 전환한다(안전망).</summary>
    private void TickAuthorityHandover()
    {
        bool authority = HasMoveAuthority;
        if (authority == m_hadMoveAuthority)
            return;

        m_hadMoveAuthority = authority;
        if (m_state != RagdollState.Ragdoll)
            return;

        ReleaseBonesToPhysics();

        m_streamer?.BeginStreaming();

        m_settled = false;
        m_settle.Reset();
    }

    private void ResumeFromSleep()
    {
        m_settled = false;
        m_settle.Reset();
        m_streamer?.ResumeStreaming();
    }

    /// <summary>정착 깃발을 세우고 자세 스트림을 끊는다.</summary>
    private void Settle()
    {
        if (m_settled)
            return;

        m_settled = true;
        m_yawFollowDone = true;
        DumpSettleTrace();

        m_streamer?.EndStreaming();

        if (m_incapacitation == null)
            return;

        if (m_incapacitation.IsLaunched)
        {
            m_incapacitation.RequestLaunchSettled();
            return;
        }

        m_incapacitation.RequestDeathSettled();
    }

    private float CapsuleBottomOffset =>
        m_controller == null ? 0f : m_controller.center.y - m_controller.height * 0.5f;

    private bool TryGroundUnder(Vector3 hipsPosition, out Vector3 point) =>
        RagdollGround.TryGroundUnder(hipsPosition, m_groundProbeDistance, m_groundMask, out point);
}
