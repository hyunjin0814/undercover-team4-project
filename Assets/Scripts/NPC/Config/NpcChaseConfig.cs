using UnityEngine;

/// <summary>
/// 오검거 추격(Chasing) 상태 튜닝 값. (#278, #259 — NpcController에서 분리)
/// </summary>
[CreateAssetMenu(fileName = "NpcChaseConfig", menuName = "Undercover/NPC/Chase Config")]
public class NpcChaseConfig : ScriptableObject
{
    [Tooltip("추격 최고 속도(m/s) — 플레이어 전력질주(8)보다 1 낮게: 직선에서는 벗어날 수 있되 코너·군중에서 따라잡힌다")]
    [SerializeField] private float m_maxSpeed = 7f;
    [Tooltip("타겟 확보 후 최고 속도까지 걸리는 가속 시간(초)")]
    [SerializeField] private float m_accelSeconds = 8f;
    [Tooltip("추격 가능 범위(m) — 타겟이 벗어나면 범위 안의 다른 플레이어로 갈아탄다. 아무도 없으면 배회하며 사냥 모드")]
    [SerializeField] private float m_range = 30f;
    [Tooltip("이 거리(m) 안으로 붙으면 포획 — 잡힌 플레이어가 오검거 페널티를 받는다")]
    [SerializeField] private float m_catchDistance = 1.3f;
    [Tooltip("격퇴(호루라기 예정 #250) 시 도주하는 시간(초)")]
    [SerializeField] private float m_repelFleeSeconds = 3f;
    [Tooltip("격퇴당한 뒤 이 시간(초) 동안은 격퇴한 플레이어를 다시 노리지 않는다")]
    [SerializeField] private float m_retargetCooldown = 5f;

    [Header("조향 — 추격 중에만 적용하고 벗어날 때 원복한다 (#568)")]
    [Tooltip("추격 중 선회 속도(도/초) — 선회 반경이 포획 거리(1.3m)보다 작아지게 둘 것")]
    [SerializeField] private float m_turnSpeed = 900f;
    [Tooltip("추격 중 가속도(m/s²) — 방향을 튼 뒤 속도를 되찾는 빠르기. 낮으면 꺾을 때마다 뒤처진다")]
    [SerializeField] private float m_acceleration = 24f;
    [Tooltip("표적이 이 시간(초) 뒤에 있을 위치를 조준한다. 크면 코너를 질러가고, 너무 크면 급반전에 속아 엉뚱한 데로 간다")]
    [SerializeField] private float m_maxLeadSeconds = 0.6f;

    [Tooltip("표적을 완전히 놓는 거리(m) — 반드시 추격 범위(Range)보다 넓게 둘 것")]
    [Min(1f)]
    [SerializeField] private float m_releaseDistance = 35f;

    [Tooltip("다른 후보가 현재 표적보다 이만큼(m) 더 가까우면 갈아탄다. 0에 가까우면 표적이 자주 바뀐다")]
    [Min(0f)]
    [SerializeField] private float m_switchAdvantage = 5f;

    [Header("납치 기습 — 납치 임무(#775)에서만 쓴다")]
    [Tooltip("표적 후방 부채꼴의 반각(도) — 이 안에 들어야 포획이 성립한다. 90이면 옆까지, 작을수록 정확히 뒤를 잡아야 한다")]
    [Range(10f, 90f)]
    [SerializeField] private float m_ambushRearHalfAngle = 75f;

    [Tooltip("표적 뒤 어느 거리(m)를 목적지로 삼는가 — 좌우 벌림까지 더한 값이 포획 거리(CatchDistance) 안이어야 닿는 순간 잡는다")]
    [Min(0.1f)]
    [SerializeField] private float m_ambushApproachDistance = 0.9f;

    [Tooltip("2인조가 겹치지 않게 좌우로 벌리는 거리(m) — 각자 지금 서 있는 쪽의 뒤를 노린다")]
    [Min(0f)]
    [SerializeField] private float m_ambushSideSpread = 0.6f;

    [Tooltip("표적 정면 이 각(도) 안에 있으면 '보고 있다'로 친다 — 이 동안에는 다가가지 않고 거리를 유지한다")]
    [Range(10f, 120f)]
    [SerializeField] private float m_ambushViewHalfAngle = 60f;

    [Tooltip("보이는 동안 표적 반대쪽으로 걸어가는 거리(m) — 매 재경로마다 다시 잡으므로 보이는 내내 멀어진다")]
    [Min(0.5f)]
    [SerializeField] private float m_ambushWalkAwayDistance = 6f;

    public float MaxSpeed => m_maxSpeed;
    public float AccelSeconds => m_accelSeconds;
    public float Range => m_range;
    public float CatchDistance => m_catchDistance;
    public float RepelFleeSeconds => m_repelFleeSeconds;
    public float RetargetCooldown => m_retargetCooldown;
    public float TurnSpeed => m_turnSpeed;
    public float Acceleration => m_acceleration;
    public float MaxLeadSeconds => m_maxLeadSeconds;

    public float ReleaseDistance => m_releaseDistance;

    public float AmbushRearHalfAngle => m_ambushRearHalfAngle;

    public float AmbushApproachDistance => m_ambushApproachDistance;

    public float AmbushSideSpread => m_ambushSideSpread;

    public float AmbushViewHalfAngle => m_ambushViewHalfAngle;

    public float AmbushWalkAwayDistance => m_ambushWalkAwayDistance;

    public float SwitchAdvantage => m_switchAdvantage;
}
