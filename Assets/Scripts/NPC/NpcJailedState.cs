using UnityEngine;

/// <summary>
/// 수감(Jailed) 상태 — 인계존 판정에서 진범·경범죄로 확정된 NPC가 유치장까지 걸어가 그 자리에 멈춰 선다. (GDD 7-2, #228)
/// 이송("걸어가는 중")과 정지("도착해 멈춤")를 한 상태 안에서 처리한다 —
/// 연행(NpcEscortedState)이 추종과 근접 정지를 한 상태로 다루는 것과 같은 구조다 (#97).
/// 멈춰 선 뒤에는 스스로 다른 상태로 전이하지 않는 최종 상태다 (탈출 이벤트는 별도 이슈).
///
/// 정산 수용 인원 계상(JailZone.Admit)은 판정 시점에 CustodyRouter가 이미 끝내므로(#340) 이 상태는
/// 계상에 관여하지 않는다 — 셀까지 걸어가 멈추는 연출만 담당한다.
/// </summary>
public class NpcJailedState : NpcStateBase
{
    // 수용 지점에 이만큼(m) 다가오면 도착으로 본다 — NavMesh 경로의 끝점 오차와 발 위치 차이를 흡수한다
    private const float k_arriveDistance = 0.5f;

    private bool m_stopped; // 도착해 멈춤 — 정지 처리를 한 번만 하기 위한 래치

    public NpcJailedState(NpcController owner) : base(owner) { }

    public override void Enter()
    {
        m_stopped = false;
        m_owner.Agent.isStopped = false;
        m_owner.Agent.stoppingDistance = 0f;

        // 유치장 내부는 시민이 못 들어가는 별도 NavMesh 영역(Jail)이다 — 수감 대상만 이 순간 통행을 얻는다 (#415).
        // SetDestination보다 반드시 먼저 켜야 셀까지의 경로가 잡힌다 — 목적지인 셀 지점이 Jail 영역 안이라
        // 통행 없이 경로를 요청하면 그대로 실패한다.
        m_owner.SetJailAccess(true);

        // 유치장이 없는 테스트 씬 — 그 자리에서 멈춘 것으로 처리한다 (멍하니 걷는 자세로 남지 않게)
        if (m_owner.JailCell == null)
        {
            StopAtCell();
            return;
        }

        // 경로를 못 잡으면(수용 지점이 NavMesh 밖 등) 영원히 걷는 자세로 남는다 — 그 자리에서 멈춤 처리
        if (!m_owner.Agent.SetDestination(m_owner.JailCell.position))
        {
            Debug.LogWarning($"NpcJailedState: 유치장 경로 실패 — 그 자리에서 정지 처리: {m_owner.name}", m_owner);
            StopAtCell();
        }
    }

    public override void Tick()
    {
        if (m_stopped)
            return; // 도착해 멈춤 — 최종 상태

        if (m_owner.Agent.pathPending)
            return;

        if (m_owner.Agent.remainingDistance > k_arriveDistance)
            return;

        StopAtCell();
    }

    public override void Exit()
    {
        // 탈출(별도 이슈) 등으로 풀려날 경우를 대비해 이동을 복구한다
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = false;
            m_owner.Agent.ResetPath();
        }
    }

    // 셀 도착 — 그 자리에 세운다. 정산 계상은 판정 시점에 이미 끝났으므로(#340) 여기서는 이동만 멈춘다.
    private void StopAtCell()
    {
        m_stopped = true;

        m_owner.Agent.isStopped = true;
        m_owner.Agent.velocity = Vector3.zero; // 감속 관성까지 끊어 수용 지점을 지나쳐 밀리지 않게
        if (m_owner.Agent.isOnNavMesh)
            m_owner.Agent.ResetPath();
    }
}
