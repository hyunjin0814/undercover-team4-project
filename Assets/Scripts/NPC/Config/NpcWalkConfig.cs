using UnityEngine;

/// <summary>
/// Walk(배회) 상태 튜닝 SO — 다음 배회 지점의 반경·최소거리.
/// </summary>
[CreateAssetMenu(fileName = "NpcWalkConfig", menuName = "Undercover/NPC/Walk Config")]
public class NpcWalkConfig : ScriptableObject
{
    [Header("배회 반경")]
    [SerializeField] private float m_wanderRadius = 10f;

    [Header("배회 지점 최소 거리")]
    [Tooltip("다음 배회 지점이 이 거리보다 가까우면 다시 뽑는다 — 한두 걸음 걷고 마는 어색한 이동 방지")]
    [SerializeField] private float m_minWanderDistance = 3f;

    public float WanderRadius => m_wanderRadius;
    public float MinWanderDistance => m_minWanderDistance;
}
