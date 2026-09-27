using UnityEngine;

/// <summary>
/// 기절(Stunned) 상태 튜닝 SO — 무력화 지속 시간과 기상 모션 구간.
/// </summary>
[CreateAssetMenu(fileName = "NpcStunConfig", menuName = "Undercover/NPC/Stun Config")]
public class NpcStunConfig : ScriptableObject
{
    [Tooltip("기절 지속 시간(초) — 테이저 무력화와 넉백 착지 KO가 쓴다 (GDD 8-3의 기준값)")]
    [SerializeField] private float m_stunSeconds = 2.67f;
    [Tooltip("체력 0으로 쓰러진 기절 시간(초) — 밧줄로 검거하거나 확인사살할 수 있는 창이다")]
    [SerializeField] private float m_knockdownStunSeconds = 30f;
    [Tooltip("기절이 끝난 뒤 덧붙는 기상 모션 길이(초). 클립 길이에 맞추며, 이 구간엔 밧줄이 걸리지 않는다")]
    [SerializeField] private float m_standUpSeconds = 1.17f;

    public float StunSeconds => m_stunSeconds;

    public float KnockdownStunSeconds => m_knockdownStunSeconds;
    public float StandUpSeconds => m_standUpSeconds;
}
