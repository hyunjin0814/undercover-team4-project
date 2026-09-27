using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// NPC 사망 도메인 부품 — 되돌아오지 않는 Dead 상태로 전이시키고 모든 링크를 끊는다.
/// 사망 여부는 코어의 상태 동기화 값을 읽으며, 시체는 에이전트를 끈 채 남는다.
/// </summary>
public class NpcDeath : NetworkBehaviour
{
    private NpcController m_owner;

    public bool IsDead => m_owner.CurrentState == NpcState.Dead;

    public event Action<NpcController, GameObject> OnDied;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
    }

    /// <summary>NPC를 사망 상태로 전이하고 링크를 정리한다(멱등). 서버(또는 오프라인) 전용.</summary>
    public void ServerEnterDead(GameObject killer)
    {
        if (m_owner.IsSpawned && !m_owner.IsServer)
            return;
        if (IsDead)
            return;

        if (m_owner.Knockback.IsKnockedBack)
            m_owner.Knockback.ServerAbortFlight();

        m_owner.Stun.ClearStunOverlay();

        m_owner.StandUp.CancelStandUp();

        m_owner.Rope.ServerClearDrag();

        m_owner.StateMachine.ChangeState(NpcState.Dead);

        NavMeshAgent agent = m_owner.Agent;
        if (agent.enabled)
        {
            if (agent.isOnNavMesh)
                agent.ResetPath();
            agent.enabled = false;
        }

        if (killer != null && killer.TryGetComponent(out PlayerKillCredit killCredit))
        {
            CitizenProfile profile = m_owner.GetComponent<CitizenIdentity>()?.Profile;
            string victimName = !string.IsNullOrEmpty(profile?.m_nameView) ? profile.m_nameView : "대상";
            killCredit.ServerCreditKill(victimName, friendlyFire: false, IsInnocentCivilian());
        }

        OnDied?.Invoke(m_owner, killer);
    }

    private bool IsInnocentCivilian()
    {
        if (m_owner.GetComponent<MisdemeanorOffender>() != null)
            return false;

        CitizenIdentity identity = m_owner.GetComponent<CitizenIdentity>();
        return identity != null && identity.Profile != null && !identity.IsCriminal;
    }
}
