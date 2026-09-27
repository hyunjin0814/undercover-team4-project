using UnityEngine;

/// <summary>
/// NPC 공용 튜닝 SO — 스폰 개체 편차, 넉백 비행 물리, 체력.
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
    [SerializeField] private LayerMask m_knockbackObstacleMask = 1;

    [Header("무게 (밧줄 끌기 속도 페널티) — #398")]
    [Tooltip("스폰 시 균등 추첨하는 개체 무게 후보(경량/표준/중량). 비어 있으면 전원 1.0")]
    [SerializeField] private float[] m_weightTiers = { 0.6f, 1f, 1.6f };

    [Header("체력 — #366/#916")]
    [Tooltip("NPC 최대 체력 — 0이 되면 쓰러진다(기절). 저항 제압 게이지(구 SubdueGaugeMax)를 대체한 값")]
    [SerializeField] private int m_maxHp = 100;

    [Tooltip("한 방의 초과 피해가 이 값 이상이면 기절 없이 즉사한다. 차량 피해 - 최대 체력보다 작게 둘 것")]
    [Min(1)]
    [SerializeField] private int m_lethalOverkillHp = 20;

    [Header("방치 회복 — #707")]
    [Tooltip("마지막 피해로부터 이 시간(초)이 지나야 회복이 시작된다. 그 전에 다시 맞으면 처음부터 다시 잰다")]
    [SerializeField] private float m_regenDelaySeconds = 20f;
    [Tooltip("회복 속도(초당 HP) — MaxHp까지 오른다. 팀 결정(2026-08-19, #707): 상한을 묶지 않고 반복 넉다운을 허용한다")]
    [SerializeField] private float m_regenHpPerSecond = 2f;

    public float SpawnSpeedMultiplierMin => m_spawnSpeedMultiplierMin;
    public float SpawnSpeedMultiplierMax => m_spawnSpeedMultiplierMax;
    public float KnockbackGravity => m_knockbackGravity;
    public float KnockbackMaxFlightSeconds => m_knockbackMaxFlightSeconds;
    public float KnockbackLandSampleDistance => m_knockbackLandSampleDistance;
    public LayerMask KnockbackObstacleMask => m_knockbackObstacleMask;

    /// <summary>스폰 시 개체 무게를 추첨한다. 서버(또는 오프라인) 전용.</summary>
    public float PickWeight() =>
        m_weightTiers.Length == 0 ? 1f : m_weightTiers[Random.Range(0, m_weightTiers.Length)];

    public int MaxHp => m_maxHp;

    public int LethalOverkillHp => m_lethalOverkillHp;

    public float RegenDelaySeconds => m_regenDelaySeconds;

    public float RegenHpPerSecond => m_regenHpPerSecond;
}
