using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NPC 특수 임무(오검거 페널티·납치·소매치기)가 공유하는 수용 → 추격 → 수렴 → 호송 파이프라인의 데이터·API 허브.
/// 행동은 상태 클래스가 하고, 여기서는 목표·대상·대형을 들고 결과를 이벤트로 중계한다. 서버 권위.
/// </summary>
public class NpcDutyAgent : NetworkBehaviour
{
    private NpcController m_owner;

    public Transform DetentionSpot { get; private set; }

    public Vector3 DetentionSlotOffset { get; private set; }

    public Transform ChaseTarget { get; private set; }

    private readonly NetworkVariable<NpcDutyKind> m_duty = new NetworkVariable<NpcDutyKind>();
    private NpcDutyKind m_dutyLocal;

    public NpcDutyKind Duty => IsSpawned ? m_duty.Value : m_dutyLocal;

    public bool IsAbductionDuty => Duty == NpcDutyKind.Abduction;

    public bool IsPickpocketDuty => Duty == NpcDutyKind.Pickpocket;

    public bool IsUndercoverDuty => Duty is NpcDutyKind.Abduction or NpcDutyKind.Pickpocket;

    public event Action OnPenaltyDutyChanged;

    public Transform PenaltyConvergeTarget { get; private set; }

    public Transform ChaseRepelBy { get; private set; }

    public float ChaseRepelUntil { get; private set; }

    public NpcController PenaltyEscortLeader { get; private set; }

    public Vector3 PenaltyEscortOffset { get; private set; }

    public Transform PenaltyEscortGoal { get; private set; }

    public event Action<NpcController, Transform> OnPenaltyCaught;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
    }

    public override void OnNetworkSpawn()
    {
        m_duty.OnValueChanged += HandleDutyChanged;
    }

    public override void OnNetworkDespawn()
    {
        m_duty.OnValueChanged -= HandleDutyChanged;
    }

    private void HandleDutyChanged(NpcDutyKind previous, NpcDutyKind current)
    {
        OnPenaltyDutyChanged?.Invoke();
    }

    /// <summary>포획 통보 — NpcChaseState 전용.</summary>
    public void NotifyPenaltyCaught(Transform caught) => OnPenaltyCaught?.Invoke(m_owner, caught);

    /// <summary>오검거당한 시민을 원한 구역으로 보내 수용한다.</summary>
    public void SendToDetention(Transform spot, Vector3 slotOffset)
    {
        if (IsSpawned && !IsServer)
            return;

        m_owner.Custody.ClearEscortTarget();
        DetentionSpot = spot;
        DetentionSlotOffset = slotOffset;
        m_owner.StateMachine.ChangeState(NpcState.Detained);
    }

    /// <summary>target을 초기 표적으로 추격을 시작한다. duty로 임무 종류를 정한다.</summary>
    public void StartPenaltyChase(Transform target, NpcDutyKind duty = NpcDutyKind.WrongfulArrest)
    {
        if (IsSpawned && !IsServer)
            return;

        DetentionSpot = null;
        ChaseTarget = target;
        SetDuty(duty);
        m_owner.StateMachine.ChangeState(NpcState.Chasing);
    }

    private void SetDuty(NpcDutyKind value)
    {
        if (m_dutyLocal == value && (!IsSpawned || m_duty.Value == value))
            return;

        m_dutyLocal = value;
        if (IsSpawned)
            m_duty.Value = value;
        else
            OnPenaltyDutyChanged?.Invoke();
    }

    /// <summary>현재 임무가 kind일 때만 임무 표식을 내린다(참조 정리·전이 없음).</summary>
    public void ClearDuty(NpcDutyKind kind)
    {
        if (IsSpawned && !IsServer)
            return;
        if (Duty != kind)
            return;

        SetDuty(NpcDutyKind.None);
    }

    /// <summary>추격 타겟 교체 — 범위 이탈 재타겟(NpcChaseState)·수렴 지시(매니저)가 호출한다.</summary>
    public void SetChaseTarget(Transform target)
    {
        if (IsSpawned && !IsServer)
            return;

        ChaseTarget = target;
    }

    /// <summary>수렴 개시 — 포획된 플레이어에게 모인다. 추격 중이 아니었어도(막 수용된 NPC 등) 추격 상태로 끌어와 모은다.</summary>
    public void StartPenaltyConverge(Transform caught)
    {
        if (IsSpawned && !IsServer)
            return;

        PenaltyConvergeTarget = caught;
        if (m_owner.StateMachine.CurrentState != NpcState.Chasing)
            m_owner.StateMachine.ChangeState(NpcState.Chasing);
    }

    /// <summary>by에게서 잠시 도주시키고 재추격 쿨다운을 건다. 수렴 중에는 무시한다.</summary>
    public void ApplyChaseRepel(Transform by)
    {
        if (IsSpawned && !IsServer)
            return;
        if (PenaltyConvergeTarget != null)
            return;

        ChaseRepelBy = by;
        ChaseRepelUntil = Time.time + m_owner.ChaseConfig.RepelFleeSeconds;
    }

    /// <summary>호송 시작 — goal(광장)으로 이동. leader가 null이면 자신이 선두, 아니면 선두 기준 offset 위치를 따라간다.</summary>
    public void StartPenaltyEscort(Transform goal, NpcController leader, Vector3 offset)
    {
        if (IsSpawned && !IsServer)
            return;

        PenaltyEscortGoal = goal;
        PenaltyEscortLeader = leader;
        PenaltyEscortOffset = offset;
        m_owner.StateMachine.ChangeState(NpcState.PenaltyEscorting);
    }

    /// <summary>임무 참조와 격퇴 잔여값을 정리하고 배회(Idle)로 복귀시킨다.</summary>
    public void EndPenaltyDuty()
    {
        if (IsSpawned && !IsServer)
            return;

        DetentionSpot = null;
        ChaseTarget = null;
        SetDuty(NpcDutyKind.None);
        PenaltyConvergeTarget = null;
        ChaseRepelBy = null;
        ChaseRepelUntil = 0f;
        PenaltyEscortLeader = null;
        PenaltyEscortGoal = null;
        m_owner.StateMachine.ChangeState(NpcState.Idle);
    }
}

public enum NpcDutyKind
{
    None,

    WrongfulArrest,

    Abduction,

    Pickpocket,
}
