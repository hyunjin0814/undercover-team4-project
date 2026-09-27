using UnityEngine;

/// <summary>
/// 연행(Escorted) 상태 튜닝 SO. 오검거 호송도 부스트 값을 공유한다.
/// </summary>
[CreateAssetMenu(fileName = "NpcEscortConfig", menuName = "Undercover/NPC/Escort Config")]
public class NpcEscortConfig : ScriptableObject
{
    [Tooltip("연행 중 플레이어와 유지하는 추종 거리(m)")]
    [SerializeField] private float m_followDistance = 1.2f;
    [Tooltip("이 거리(m)보다 뒤처지면 속도를 올려 따라잡는다")]
    [SerializeField] private float m_boostDistance = 4f;
    [SerializeField] private float m_boostMultiplier = 1.5f;
    [Tooltip("이 거리(m)를 넘으면 연행이 풀리고 그 자리에서 체포 상태로 멈춘다")]
    [SerializeField] private float m_breakDistance = 8f;

    public float FollowDistance => m_followDistance;
    public float BoostDistance => m_boostDistance;
    public float BoostMultiplier => m_boostMultiplier;
    public float BreakDistance => m_breakDistance;
}
