using UnityEngine;

/// <summary>
/// 컨트롤러 레벨 공용 튜닝 값 — 특정 FSM 상태에 속하지 않는다. (#259 — NpcController에서 분리)
/// 스폰 시 개체 편차, 넉백 비행 물리(#232), 체력(#366)을 담는다.
/// </summary>
[CreateAssetMenu(fileName = "NpcCommonConfig", menuName = "Undercover/NPC/Common Config")]
public class NpcCommonConfig : ScriptableObject
{
    [Header("개체별 이동 속도 편차 (배율)")]
    [Tooltip("스폰 시 NavMeshAgent 속도에 이 범위의 랜덤 배율을 곱한다 — 군중이 전부 같은 속도로 걷는 것 방지")]
    [SerializeField] private float m_spawnSpeedMultiplierMin = 0.8f;
    [SerializeField] private float m_spawnSpeedMultiplierMax = 1.2f;

    [Header("넉백 (폭발 등 외력) — #232")]
    [Tooltip("날아가는 동안 받는 중력. 음수 — 클수록 낮고 빠르게 떨어진다")]
    [SerializeField] private float m_knockbackGravity = -18f;
    [Tooltip("안전장치: 이 시간(초)이 지나도 착지 판정이 안 나면 강제로 내려놓는다")]
    [SerializeField] private float m_knockbackMaxFlightSeconds = 3f;
    [Tooltip("착지 지점을 NavMesh 위로 되돌릴 때 허용하는 최대 탐색 거리(m)")]
    [SerializeField] private float m_knockbackLandSampleDistance = 4f;
    [Tooltip("날아가는 도중 벽으로 칠 콜라이더 — 여기에 걸리면 수평 이동이 멈춘다. NPC 자신의 레이어는 런타임에 자동으로 빠진다")]
    [SerializeField] private LayerMask m_knockbackObstacleMask = 1; // Default(환경)만 — 캐릭터 오판 방지 (#339)
    [Tooltip("착지에 실패해 NavMesh 밖에 남았을 때, 이 시간(초)이 지나면 강제로 NavMesh 위로 되돌린다 (#423)")]
    [SerializeField] private float m_knockbackStrandedRecoverySeconds = 2f;
    [Tooltip("강제 복귀 시 NavMesh 탐색 거리(m) — 건물 위에서 지상까지 닿아야 하므로 착지 탐색보다 넓게 잡는다")]
    [SerializeField] private float m_knockbackRecoverySampleDistance = 25f;

    [Header("체력 — #366")]
    [Tooltip("NPC 최대 체력 — 0이 되면 기절(Stunned)한다. 저항 제압 게이지(구 SubdueGaugeMax)를 대체한 값")]
    [SerializeField] private int m_maxHp = 100;
    [Tooltip("제압 홀드 성공 1회가 깎는 체력 — 기본값 기준 3회로 기절. NpcResistConfig에서 이관 (#366)")]
    [SerializeField] private int m_subdueHitPower = 34;

    public float SpawnSpeedMultiplierMin => m_spawnSpeedMultiplierMin;
    public float SpawnSpeedMultiplierMax => m_spawnSpeedMultiplierMax;
    public float KnockbackGravity => m_knockbackGravity;
    public float KnockbackMaxFlightSeconds => m_knockbackMaxFlightSeconds;
    public float KnockbackLandSampleDistance => m_knockbackLandSampleDistance;
    public LayerMask KnockbackObstacleMask => m_knockbackObstacleMask;
    public float KnockbackStrandedRecoverySeconds => m_knockbackStrandedRecoverySeconds;
    public float KnockbackRecoverySampleDistance => m_knockbackRecoverySampleDistance;

    /// <summary>NPC 최대 체력 — HUD가 비율 계산에, NpcController가 초기화·회복에 읽는다. (#366)</summary>
    public int MaxHp => m_maxHp;

    /// <summary>제압 홀드 1회가 깎는 체력. (#366 — 구 NpcResistConfig.SubdueHitPower)</summary>
    public int SubdueHitPower => m_subdueHitPower;
}
