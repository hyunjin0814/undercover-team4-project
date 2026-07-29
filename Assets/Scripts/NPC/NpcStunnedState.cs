using UnityEngine;

/// <summary>
/// 기절(Stunned) 상태 — 테이저 및 체력 0 도달의 연결고리. (GDD 7-4/8-3, #76/#366)
/// 지속 시간 동안 완전 무방비로 멈추며, 이 동안 수갑을 채우면 반응 없이 즉시 연행된다.
/// 시간이 지나면 일어나(#269 StandUp 모션) 스스로 도주한다 — 무력화가 풀린 대상은 그대로 서 있지
/// 않는다(#269 확정). #366 결정 5로 배회 복귀에 잠시 바뀌었다가 원복됐다.
/// 체력 회복은 Exit()에서 처리한다 — 시간 경과뿐 아니라 수갑 채포 등 이 상태를 벗어나는
/// 모든 경로를 덮어야 "HP 0인 채로 무적이 되는" 문제를 막을 수 있기 때문이다 (#366, Exit() 참고).
/// 진입은 NpcController.EnterStunned() — 테이저와 체력 0 도달(NpcController.SetHp)이 호출한다.
/// </summary>
public class NpcStunnedState : NpcStateBase
{
    private float m_timer;

    // 일어나는 모션을 이미 시작했는가 — 기절 시간의 마지막 구간에서 한 번만 발행한다 (#269)
    private bool m_standingUp;

    private readonly NpcStunConfig m_config;

    public NpcStunnedState(NpcController owner, NpcStunConfig config) : base(owner)
    {
        m_config = config;
    }

    public override void Enter()
    {
        m_timer = 0f;
        m_standingUp = false;
        // isStopped도 NavMesh 위에 있어야 부를 수 있다 — 넉백으로 NavMesh 밖에 떨어진 채
        // 이 상태로 들어오면 여기서 에러가 났다 (#423)
        if (m_owner.Agent.isOnNavMesh)
        {
            m_owner.Agent.isStopped = true;
            m_owner.Agent.ResetPath();
        }
    }

    public override void Tick()
    {
        // 밧줄로 묶이면 커스터디(Escorted)로 넘어가므로 여기서 끌기를 볼 일이 없다 —
        // 기절 타이머 정지 분기는 그 전이와 함께 제거됐다. (#369)
        m_timer += Time.deltaTime;

        // 기절 시간의 마지막 구간을 일어나는 모션에 쓴다 — 총 무력화 시간(StunSeconds)은 그대로 두고
        // "누워 있다 → 일어난다 → 배회"가 이어지게 한다. 이 구간에도 상태는 Stunned라 움직이지 않는다.
        float standUpAt = Mathf.Max(0f, m_config.StunSeconds - m_config.StandUpSeconds);
        if (!m_standingUp && m_timer >= standUpAt)
        {
            m_standingUp = true;
            m_owner.RaiseStandUp(); // 전 피어에 일어나는 모션 재생을 알린다
        }

        // 깨어나면 배회가 아니라 도주다 (#269 확정 — #366 결정 5의 배회 복귀에서 원복).
        // 위협은 기절시킨 상대(테이저 사수)이거나 밧줄로 끌고 다닌 플레이어다. 타격으로 기절한
        // 경우엔 null인데(SubdueHitRpc가 요청자를 싣지 않는다) NpcFleeState가 EscapeDistance 안
        // 추격자를 스캔해 폴백하므로, 때린 플레이어가 옆에 있으면 그쪽에서 도망친다.
        // 주변에 아무도 없으면 도주 상태가 스스로 배회로 돌려보낸다 — 아무도 없는 곳에 두고 온
        // NPC가 혼자 전력 질주하지 않는다.
        // 체력 회복은 여기서 하지 않는다 — Exit()으로 옮겼다. 이유는 Exit() 주석 참고 (#366).
        if (m_timer >= m_config.StunSeconds)
            m_owner.StartFlee(m_owner.ThreatTarget);
    }

    public override void Exit()
    {
        m_owner.Agent.isStopped = false;

        // 체력 회복을 Tick()이 아니라 여기(Exit)에서 하는 이유 (#366) —
        // Stunned를 벗어나는 경로가 "시간이 다 돼 Idle로 깨어난다"만이 아니다. 기절한 NPC에
        // 수갑을 채우면 NpcStateRules.IsCapturable이 Stunned를 막지 않으므로(의도된 동작)
        // Stunned → Captured로 곧장 전이하는데, 이 경로는 Tick()의 "시간 다 됨" 분기를 거치지
        // 않는다. 회복을 그 분기에만 두면 이런 NPC는 HP 0인 채로 Captured에 남고, 이후
        // NpcController.ReleaseFromCustody()(오검거 석방)나 NpcCapturedState의 인계 방치
        // 타이머(Escape())가 Idle/Run으로 돌려보내도 둘 다 HP를 회복하지 않는다. 그러면
        // SetHp의 "0에 도달하는 순간"에만 걸리는 엣지 트리거가 이미 0인 값에는 다시 걸리지
        // 않아, 그 NPC는 라운드 내내 몇 대를 맞아도 두 번 다시 기절하지 않는 무적이 된다.
        // Exit()은 상태 머신이 Stunned를 빠져나가는 모든 경로에서 호출되므로 수갑 경로까지
        // 함께 덮인다.
        //
        // Enter()가 아니라 Exit()에 두는 이유: 기절해 있는 동안에는 HP가 0으로 유지돼야
        // "기절 중 추가 타격이 기절 타이머를 리셋하지 않는다"는 성질이 성립하기 때문이다.
        m_owner.ServerRestoreHp();
    }
}
