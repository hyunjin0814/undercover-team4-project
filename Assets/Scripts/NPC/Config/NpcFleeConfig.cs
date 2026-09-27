using UnityEngine;

/// <summary>
/// 도주(Run) 상태 튜닝 SO. 저항·추격도 일부 값을 공유한다.
/// </summary>
[CreateAssetMenu(fileName = "NpcFleeConfig", menuName = "Undercover/NPC/Flee Config")]
public class NpcFleeConfig : ScriptableObject
{
    [Tooltip("도주 시 기본 이동 속도에 곱하는 배율")]
    [SerializeField] private float m_speedMultiplier = 1.5f;
    [Tooltip("도주 목적지를 한 번에 이만큼(m) 앞으로 잡는다 — 먼 지점 샘플이 막힌 방향의 폴백 거리")]
    [SerializeField] private float m_stepDistance = 10f;
    [Tooltip("도주 지점을 이만큼(m) 멀리 잡고 도착할 때까지 쭉 뛴다 — 지형에 막힌 방향은 StepDistance로 줄여 잡는다")]
    [SerializeField] private float m_farPointDistance = 20f;
    [Tooltip("추적자와 이 거리(m) 이상 벌어지면 도주 성공 — 배회로 복귀한다")]
    [SerializeField] private float m_escapeDistance = 25f;
    [Tooltip("도주 경로가 플레이어에게 이 거리(m)보다 가까이 스치면 그 방향은 버린다 — 체포 사거리(PlayerInteractor.Range, 3m) + 여유 마진")]
    [SerializeField] private float m_clearanceRadius = 4f;
    [Tooltip("도주 진입 후 이 시간(초) 안에는 포위됐어도 저항으로 되돌아가지 않는다 — 저항↔도주 왕복 방지 (#213)")]
    [SerializeField] private float m_resistCooldown = 2f;

    public float SpeedMultiplier => m_speedMultiplier;
    public float StepDistance => m_stepDistance;
    public float FarPointDistance => m_farPointDistance;
    public float EscapeDistance => m_escapeDistance;
    public float ClearanceRadius => m_clearanceRadius;
    public float ResistCooldown => m_resistCooldown;
}
