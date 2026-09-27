using System;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 검거 반응 도메인 부품 — 도주·저항·스윙 판정 진입점과 위협 참조를 관리한다.
/// 전이는 서버 권위이며, NpcController와 같은 GameObject에 둔다.
/// </summary>
public class NpcReaction : NetworkBehaviour
{
    private NpcController m_owner;

    public Transform ThreatTarget { get; internal set; }

    public bool IsSprinter { get; private set; }

    public bool IsRelentless { get; private set; }

    public float ThreatSearchRadius =>
        m_owner.ResistConfig.AttackRange * m_owner.ResistConfig.ThreatSearchRadiusMultiplier;

    public event Action<int> OnAttackSwing;

    public event Action OnAttackHit;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
    }

    /// <summary>스캔·피격 트리거에 대한 반응을 판정한다. 순응형의 피격 반응은 한 번 뽑으면 확정된다. 서버(또는 오프라인) 전용.</summary>
    public void ServerReactTo(ReactionTrigger trigger, Transform threat)
    {
        if (IsSpawned && !IsServer)
            return;

        if (trigger == ReactionTrigger.Damage
            && m_owner.CurrentState == NpcState.Attack
            && !m_owner.Stun.IsStunned
            && TryRetargetTo(threat))
            return;

        bool allowed =
            trigger == ReactionTrigger.Damage
                ? NpcStateRules.CanReactToDamage(m_owner.CurrentState)
                : NpcStateRules.CanStartReaction(m_owner.CurrentState);
        if (!allowed)
            return;

        if (m_owner.Stun.IsStunned)
            return;

        CitizenIdentity identity = GetComponent<CitizenIdentity>();
        if (identity == null)
            return;

        switch (ResolveReaction(identity, trigger))
        {
            case ReactionType.Flee:
                StartFlee(threat);
                return;

            case ReactionType.Resist:
                StartResist(threat);
                return;
        }
    }

    private float m_retargetLockUntil;

    /// <summary>저항 중 표적을 때린 사람으로 갈아탄다 — 갈아탔으면 참. 서버(또는 오프라인) 전용.</summary>
    private bool TryRetargetTo(Transform attacker)
    {
        if (attacker == null || attacker == ThreatTarget)
            return false;

        if (Time.time < m_retargetLockUntil)
            return false;

        m_retargetLockUntil = Time.time + m_owner.ResistConfig.AttackInterval;
        ThreatTarget = attacker;
        Debug.Log($"저항 표적 교체(피격) — {m_owner.name} → {attacker.name}");
        return true;
    }

    private static ReactionType ResolveReaction(CitizenIdentity identity, ReactionTrigger trigger)
    {
        ReactionType assigned = identity.Reaction;
        if (assigned != ReactionType.Compliant)
            return assigned;

        if (trigger != ReactionTrigger.Damage)
            return ReactionType.Compliant;

        ReactionType rolled = Random.value < 0.5f ? ReactionType.Flee : ReactionType.Resist;
        identity.AssignReaction(rolled);
        return rolled;
    }

    /// <summary>도주 시작 — threat(플레이어) 반대 방향으로 달아난다. 반응 판정은 ServerReactTo가 한다.</summary>
    public void StartFlee(Transform threat)
    {
        if (IsSpawned && !IsServer)
            return;

        IsSprinter = false;
        IsRelentless = false;
        ThreatTarget = threat;
        m_owner.StateMachine.ChangeState(NpcState.Run);
    }

    /// <summary>위협 없이 도심을 계속 뛰어다니는 질주 상태를 시작한다(공연음란범 전용).</summary>
    public void StartSprint()
    {
        if (IsSpawned && !IsServer)
            return;

        IsSprinter = true;
        IsRelentless = false;
        ThreatTarget = null;
        m_owner.StateMachine.ChangeState(NpcState.Sprinting);
    }

    /// <summary>무력화에서 풀려난 뒤 원래 행동(도주·질주·저항)으로 복귀시킨다.</summary>
    public void ResumeReaction(Transform threat)
    {
        if (IsSprinter)
            StartSprint();
        else if (IsRelentless)
            StartResist(threat, relentless: true);
        else
            StartFlee(threat);
    }

    /// <summary>위협 참조 정리 — 반응(도주·저항)이 끝나는 지점에서 호출한다.</summary>
    public void ClearThreat() => ThreatTarget = null;

    /// <summary>저항 시작 — 그 자리에서 버틴다. 반응 판정은 ServerReactTo가 한다.</summary>
    public void StartResist(Transform subduer = null, bool relentless = false)
    {
        if (IsSpawned && !IsServer)
            return;

        IsSprinter = false;
        IsRelentless = relentless;
        ThreatTarget = subduer;
        m_owner.StateMachine.ChangeState(NpcState.Attack);
    }

    /// <summary>공격 스윙 1회를 전 피어에 알린다(애니메이션용).</summary>
    public void RaiseAttackSwing(int variant)
    {
        OnAttackSwing?.Invoke(variant);
        if (IsSpawned && IsServer)
            PlayAttackSwingClientRpc(variant);
    }

    [ClientRpc]
    private void PlayAttackSwingClientRpc(int variant)
    {
        if (IsServer)
            return;
        OnAttackSwing?.Invoke(variant);
    }

    /// <summary>공격 명중을 전 피어에 알린다(타격음용).</summary>
    public void RaiseAttackHit()
    {
        OnAttackHit?.Invoke();
        if (IsSpawned && IsServer)
            PlayAttackHitClientRpc();
    }

    [ClientRpc]
    private void PlayAttackHitClientRpc()
    {
        if (IsServer)
            return;
        OnAttackHit?.Invoke();
    }
}
