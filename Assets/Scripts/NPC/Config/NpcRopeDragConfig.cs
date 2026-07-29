using UnityEngine;

/// <summary>
/// 밧줄 끌기 장력 튜닝 값. (#269/#369 — #390에서 PlayerEscorter에서 분리)
/// 장력 계산이 끄는 플레이어가 아니라 끌리는 NPC로 옮겨오면서, 값도 다른 상태 config들과 같은 자리에 둔다.
/// 여러 명이 한 대상을 함께 끄는 줄다리기(#390)가 들어오면 앵커만 늘어나고 이 값들은 그대로 쓰인다.
/// </summary>
[CreateAssetMenu(fileName = "NpcRopeDragConfig", menuName = "Undercover/NPC/Rope Drag Config")]
public class NpcRopeDragConfig : ScriptableObject
{
    [Tooltip("밧줄 길이(m) — 이 거리를 넘어야 NPC가 끌려온다. 안쪽이면 밧줄이 늘어져 당기지 않는다")]
    [SerializeField] private float m_ropeLength = 1.6f;

    [Tooltip("끌리는 몸이 목표 위치를 따라잡는 데 걸리는 시간(초) — 클수록 늦게, 크게 휘며 따라온다")]
    [SerializeField] private float m_dragSmoothTime = 0.14f;

    [Tooltip("몸이 밧줄 방향으로 도는 민감도(1/초) — 클수록 즉각 방향을 맞춘다")]
    [SerializeField] private float m_dragTurnSharpness = 6f;

    [Tooltip("끌리며 좌우로 흔들리는 최대 각(도) — 0이면 흔들리지 않는다")]
    [SerializeField] private float m_dragSwayAngle = 7f;

    [Tooltip("흔들림 주기 — 끌린 거리 1m당 위상(라디안)")]
    [SerializeField] private float m_dragSwayFrequency = 1.6f;

    [Tooltip("여러 명을 함께 끌 때 좌우로 벌리는 간격(m) — 0이면 한 점에 겹쳐 한 덩어리로 뭉친다. 몸통 폭보다 넉넉해야 한다")]
    [SerializeField] private float m_dragSpacing = 0.85f;

    public float RopeLength => m_ropeLength;
    public float DragSmoothTime => m_dragSmoothTime;
    public float DragTurnSharpness => m_dragTurnSharpness;
    public float DragSwayAngle => m_dragSwayAngle;
    public float DragSwayFrequency => m_dragSwayFrequency;
    public float DragSpacing => m_dragSpacing;
}
