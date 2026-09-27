using UnityEngine;

/// <summary>
/// NavMesh 경로 재탐색·주변 훑기 주기 튜닝 SO.
/// 재탐색 주기만 가장 가까운 플레이어와의 거리 티어에 따라 달라진다.
/// </summary>
[CreateAssetMenu(fileName = "NpcRepathConfig", menuName = "Undercover/NPC/Repath Config")]
public class NpcRepathConfig : ScriptableObject
{
    [Header("재탐색 거리 티어")]
    [Tooltip("가장 가까운 플레이어가 이 거리(m) 안이면 촘촘한 주기를 쓴다")]
    [SerializeField] private float m_nearDistance = 8f;
    [Tooltip("이 거리(m) 안이면 중간 주기 — 넘으면 성긴 주기")]
    [SerializeField] private float m_midDistance = 25f;

    [Header("재탐색 주기(초)")]
    [Tooltip("근거리 — 붙었을 때. 추격 접근전 정밀도가 여기서 나온다")]
    [SerializeField] private float m_nearInterval = 0.15f;
    [Tooltip("중거리 — 기존 상태 클래스들이 쓰던 기본값")]
    [SerializeField] private float m_midInterval = 0.2f;
    [Tooltip("원거리 — 화면 밖에 가까운 NPC. 여기서 대부분의 절감이 나온다")]
    [SerializeField] private float m_farInterval = 0.5f;

    [Tooltip("티어 판정(가장 가까운 플레이어 거리)을 다시 재는 주기(초) — 재탐색보다 성겨도 된다")]
    [SerializeField] private float m_tierSampleInterval = 0.5f;

    [Header("훑기·막힘 판정 주기(초) — 티어 없음")]
    [Tooltip("도주 중 추적자 훑기 — 도주 이탈 판정이 이 게이트를 함께 탄다. 늘리면 이탈이 늦어진다")]
    [SerializeField] private float m_threatScanInterval = 0.25f;
    [Tooltip("추격 표적 후보 훑기 — 범위 내 플레이어 전수 순회 주기")]
    [SerializeField] private float m_targetScanInterval = 0.5f;
    [Tooltip("도주 막힘(제자리 걸음) 판정 주기")]
    [SerializeField] private float m_stuckCheckInterval = 0.5f;

    public float NearDistance => m_nearDistance;
    public float MidDistance => m_midDistance;
    public float NearInterval => m_nearInterval;
    public float MidInterval => m_midInterval;
    public float FarInterval => m_farInterval;
    public float TierSampleInterval => m_tierSampleInterval;
    public float ThreatScanInterval => m_threatScanInterval;
    public float TargetScanInterval => m_targetScanInterval;
    public float StuckCheckInterval => m_stuckCheckInterval;
}
