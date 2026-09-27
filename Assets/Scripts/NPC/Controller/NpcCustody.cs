using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 신병 도메인 부품 — 연행·판정 표식·수감 배치·감옥 퇴장·수갑 해제·반출 표식을 관리하고 FSM 전이를 건다.
/// 전이는 서버 권위이며, NpcController와 같은 GameObject에 둔다.
/// </summary>
public class NpcCustody : NetworkBehaviour
{
    private NpcController m_owner;
    private NpcRagdoll m_ragdoll;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
        m_ragdoll = GetComponent<NpcRagdoll>();
    }

    internal NpcController Owner => m_owner;

    public Transform EscortTarget => m_escortTarget;

    private Transform m_escortTarget;

    private readonly NetworkVariable<bool> m_hasEscortTargetSynced = new(false);

    public bool HasEscortTarget =>
        IsSpawned && !IsServer ? m_hasEscortTargetSynced.Value : m_escortTarget != null;

    private void SetEscortTarget(Transform target)
    {
        m_escortTarget = target;
        if (IsSpawned && IsServer)
            m_hasEscortTargetSynced.Value = target != null;
    }

    /// <summary>연행 시작 — 체포 성공 직후 호출. NPC가 target(플레이어)을 따라 이동한다.</summary>
    public void StartEscort(Transform target)
    {
        if (IsSpawned && !IsServer)
            return;

        SetEscortTarget(target);
        m_owner.StateMachine.ChangeState(NpcState.Escorted);
    }

    /// <summary>연행 중단 — 그 자리에서 체포(Captured) 상태로 멈춘다.</summary>
    public void StopEscort()
    {
        if (IsSpawned && !IsServer)
            return;

        ClearEscortTarget();
        m_owner.StateMachine.ChangeState(NpcState.Captured);
    }

    /// <summary>연행 참조만 끊는다. 상태 전이는 부르는 쪽이 한다.</summary>
    internal void ClearEscortTarget()
    {
        SetEscortTarget(null);
    }

    /// <summary>연행 참조를 from에서 to로 넘긴다(전이 없음). 현재 참조가 from일 때만 옮기고 true.</summary>
    internal bool HandOverEscortTarget(Transform from, Transform to)
    {
        if (IsSpawned && !IsServer)
            return false;
        if (to == null || m_escortTarget != from)
            return false;

        SetEscortTarget(to);
        return true;
    }

    public bool IsDelivered { get; private set; }

    /// <summary>인계 판정 완료로 표시 — ArrestJudge 전용. 서버(또는 오프라인)에서만 호출된다.</summary>
    public void MarkDelivered()
    {
        if (IsSpawned && !IsServer)
            return;

        IsDelivered = true;
    }

    /// <summary>인계 판정 완료 표시를 되돌려 재검거가 첫 인계로 잡히게 한다(탈옥 전용). 서버(또는 오프라인) 전용.</summary>
    public void ClearDelivered()
    {
        if (IsSpawned && !IsServer)
            return;

        IsDelivered = false;
    }

    public Transform JailSpot { get; private set; }

    /// <summary>진범·경범죄로 판정된 NPC를 감옥 안 배치 지점으로 순간이동시켜 수감한다.</summary>
    public void SendToJail(Transform spot)
    {
        if (IsSpawned && !IsServer)
            return;

        SetEscortTarget(null);

        m_owner.Stun.ExitStun(false);

        m_owner.Health.ServerRestoreFull();

        m_owner.SetGrantedAreas(NpcNavAreas.JailMask);

        JailSpot = spot;
        m_owner.StateMachine.ChangeState(NpcState.Jailed);
    }

    /// <summary>판정된 시체를 감옥 안 자리로 옮긴다(상태 전이 없음). 서버(또는 오프라인) 전용.</summary>
    public void SendCorpseToJail(Vector3 position) => ServerMoveCorpse(position);

    /// <summary>시체의 뼈 전체를 통째로 옮긴다. 서버(또는 오프라인) 전용.</summary>
    public void ServerMoveCorpse(Vector3 position)
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_ragdoll != null)
        {
            m_ragdoll.ServerPlaceCorpse(position);

            m_owner.Rope.ServerReattachCorpseRopes();
        }
        else
        {
            transform.position = position;
        }
    }

    /// <summary>오검거된 무고한 시민의 수갑을 풀고 배회(Idle)로 복귀시킨다. Captured 상태에서만 유효하다.</summary>
    public void ReleaseFromCustody()
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_owner.StateMachine.CurrentState != NpcState.Captured)
            return;

        JailSpot = null;
        m_owner.StateMachine.ChangeState(NpcState.Idle);
    }

    /// <summary>셀 통행을 반납하고 셀 밖 지점으로 순간이동시킨다. 워프 실패 시 제자리에 둔다. 서버(또는 오프라인) 전용.</summary>
    public void ServerExitJail(Vector3 exitPosition)
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_owner.Agent == null)
            return;

        m_owner.SetGrantedAreas(0);

        if (!m_owner.TryWarpNear(exitPosition))
        {
            Debug.LogWarning(
                $"NpcCustody: 셀 퇴장 지점으로 워프 실패 — 제자리에 둔다: {name}",
                this
            );
        }
    }

    public bool WasSecuredByPlayer =>
        IsSpawned && !IsServer ? m_securedByPlayerSynced.Value : m_securedByPlayer;

    private bool m_securedByPlayer;

    private readonly NetworkVariable<bool> m_securedByPlayerSynced = new(false);

    public bool IsSecuredByAnyone => HasEscortTarget || WasSecuredByPlayer;

    /// <summary>플레이어가 확보했다는 표식을 설정한다.</summary>
    public void SetSecuredByPlayer(bool value)
    {
        if (IsSpawned && !IsServer)
            return;

        m_securedByPlayer = value;
        if (IsSpawned && IsServer)
            m_securedByPlayerSynced.Value = value;
    }
}
