using UnityEngine;

/// <summary>
/// NPC 모션 결정 지점 — 부품들의 모션 제안 중 하나를 우선순위로 골라 Animator State(int)에 쓴다.
/// 기준 상태 매핑·누움 판정·중재를 맡으며, 동기화 이벤트를 따라 모든 피어에서 같은 모션을 낸다.
/// </summary>
[RequireComponent(typeof(NpcController))]
[RequireComponent(typeof(NpcLocomotionMotion))]
[RequireComponent(typeof(NpcOneShotMotion))]
public class NpcAnimationDriver : MonoBehaviour
{
    [SerializeField] private Animator m_animator;

    private NpcController m_controller;
    private NpcDutyAgent m_penalty;
    private NpcReaction m_reaction;
    private NpcLocomotionMotion m_locomotion;
    private NpcOneShotMotion m_oneShot;
    private INpcMotionSource[] m_sources;
    private NpcState m_baseState;

    private bool m_ropeBoundMotion;
    private bool m_ropeProneMotion;

    public bool IsProne { get; private set; }

    public event System.Action<bool> OnProneChanged;

    public NpcState BaseState => m_baseState;

    public bool SuppressLocomotion => IsRopeProne || m_controller.Stun.IsStunned;

    public bool CanLeaveStandUp =>
        !IsRopeProne && m_baseState != NpcState.Stunned && !m_controller.StandUp.IsStandingUp;

    private bool IsRopeBound => m_controller.Rope.IsRoped || m_controller.Rope.IsTethered;

    private bool IsRopeProne =>
        (IsRopeBound || m_controller.StandUp.IsStandingUp) && !m_oneShot.IsStandingUp;

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();
        m_penalty = GetComponent<NpcDutyAgent>();
        m_reaction = GetComponent<NpcReaction>();
        m_locomotion = GetComponent<NpcLocomotionMotion>();
        m_oneShot = GetComponent<NpcOneShotMotion>();

        if (m_animator == null)
            m_animator = GetComponentInChildren<Animator>();

        m_sources = new INpcMotionSource[] { m_locomotion, m_oneShot };
        System.Array.Sort(m_sources, (a, b) => b.Priority.CompareTo(a.Priority));
    }

    private void Start()
    {
        m_controller.OnStateChanged += HandleStateChanged;
        m_penalty.OnPenaltyDutyChanged += HandlePenaltyDutyChanged;
        m_reaction.OnAttackSwing += HandleAttackSwing;
        m_controller.OnStandUp += HandleStandUp;
        m_controller.Stun.OnStunnedChanged += HandleStunnedChanged;
        HandleStateChanged(m_controller.CurrentState);
    }

    private void OnDestroy()
    {
        if (m_controller != null)
        {
            m_controller.OnStateChanged -= HandleStateChanged;
            m_controller.OnStandUp -= HandleStandUp;
            m_controller.Stun.OnStunnedChanged -= HandleStunnedChanged;
        }

        if (m_penalty != null)
            m_penalty.OnPenaltyDutyChanged -= HandlePenaltyDutyChanged;

        if (m_reaction != null)
            m_reaction.OnAttackSwing -= HandleAttackSwing;
    }

    private void Update()
    {
        if (m_animator == null || Time.deltaTime <= 0f)
            return;

        TrackRopeEdges();
        m_oneShot.Tick();
        m_locomotion.Tick(Time.deltaTime);
        Apply(ResolveMotion());
    }

    /// <summary>우선순위 순으로 물어보고 처음 응답한 것이 이긴다. 아무도 없으면 기준 상태 모션.</summary>
    private int ResolveMotion()
    {
        for (int i = 0; i < m_sources.Length; i++)
        {
            if (m_sources[i].TryGetMotion(out int motion))
                return motion;
        }

        return AnimatorBaseState(m_baseState);
    }

    private void Apply(int motion)
    {
        if (motion == m_animator.GetInteger(NpcAnimStates.s_stateHash))
            return;

        m_animator.SetInteger(NpcAnimStates.s_stateHash, motion);
    }

    /// <summary>스윙 클립 index 지정 — <see cref="NpcOneShotMotion"/>이 스윙 직전에 부른다.</summary>
    public void SetSwingVariant(int variant)
    {
        if (m_animator != null)
            m_animator.SetFloat(NpcAnimStates.s_swingVariantHash, variant);
    }

    private void TrackRopeEdges()
    {
        bool bound = IsRopeBound;
        if (m_ropeBoundMotion != bound)
        {
            m_ropeBoundMotion = bound;
            if (bound)
                m_oneShot.CancelStandUp();
            RefreshProne();
            m_locomotion.ResetSpeedTracking();
        }

        bool prone = IsRopeProne;
        if (m_ropeProneMotion != prone)
        {
            m_ropeProneMotion = prone;
            RefreshProne();
            m_locomotion.ResetSpeedTracking();
        }
    }

    /// <summary>누움을 다시 판정해 바뀌었으면 알린다 — 기준 상태·기상·묶임을 건드린 직후에.</summary>
    public void RefreshProne()
    {
        bool prone =
            m_baseState == NpcState.Dead
            || IsRopeProne
            || (m_baseState == NpcState.Stunned && !m_oneShot.IsStandingUp);
        if (prone == IsProne)
            return;

        IsProne = prone;
        OnProneChanged?.Invoke(prone);
    }

    private int AnimatorBaseState(NpcState state)
    {
        if (IsRopeProne)
            return (int)NpcState.Stunned;

        return state switch
        {
            NpcState.Attack => m_locomotion.IsResistMoving ? (int)NpcState.Run : (int)NpcState.Idle,
            NpcState.Dead => (int)NpcState.Stunned,
            NpcState.Intruding => (int)NpcState.Walk,
            NpcState.Jailed => (int)NpcState.Escorted,
            NpcState.Detained => (int)NpcState.Walk,
            NpcState.Chasing => (int)NpcState.Run,
            NpcState.PenaltyEscorting => (int)NpcState.Walk,
            NpcState.Sprinting => (int)NpcState.Run,
            NpcState.Smuggling => (int)NpcState.Walk,
            _ => (int)state,
        };
    }

    private void HandleAttackSwing(int variant) => m_oneShot.PlaySwing(variant);

    private void HandleStandUp() => m_oneShot.PlayStandUp();

    private void HandleStunnedChanged(bool stunned) =>
        HandleStateChanged(stunned ? NpcState.Stunned : m_controller.CurrentState);

    private void HandlePenaltyDutyChanged() => HandleStateChanged(m_controller.CurrentState);

    private void HandleStateChanged(NpcState state)
    {
        if (m_controller.Stun.IsStunned)
            state = NpcState.Stunned;

        m_baseState = state;
        m_oneShot.OnBaseStateChanged(state);
        m_locomotion.OnBaseStateChanged(state);
        RefreshProne();

        NpcPenaltyMark.SetVisible(
            m_controller,
            NpcLocomotionMotion.IsPenaltyLocomotion(state) && !m_penalty.IsUndercoverDuty
        );

        if (m_animator != null)
            Apply(ResolveMotion());
    }
}
