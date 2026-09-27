using UnityEngine;

/// <summary>
/// 기절(Stunned) 상태 튜닝 SO — 무력화 지속 시간과 기상 모션 구간.
/// </summary>
[CreateAssetMenu(fileName = "NpcStunConfig", menuName = "Undercover/NPC/Stun Config")]
public class NpcStunConfig : ScriptableObject
{
    [Tooltip("기절 지속 시간(초) — 테이저 무력화와 넉백 착지 KO가 쓴다 (GDD 8-3의 기준값)")]
    [SerializeField] private float m_stunSeconds = 2.67f;
    [Tooltip("체력 0으로 쓰러진 기절의 지속 시간(초) — 테이저와 분리된 값이다. 밧줄 묶기가 무력화된 대상만 대상이 되면서(#446) 이 시간이 곧 검거할 수 있는 유일한 창이고, 동시에 <b>확인사살로 죽일 수 있는 유일한 창</b>이기도 하다(#916) — 난이도의 핵심 손잡이. 마지막 StandUpSeconds는 일어나는 모션이라 실제로 누워 있는 시간은 그만큼 짧다(5초 → 약 4.4초)")]
    [SerializeField] private float m_knockdownStunSeconds = 30f;
    [Tooltip("일어나는 모션의 길이(초) — 기절이 <b>다 끝난 뒤에 덧붙는</b> 구간이다. 위 기절 시간이 곧 누워 있는 시간이고, " +
             "총 무력화 = 기절 + 이 값이다. 예전에는 기절 시간에서 잘라 썼는데 그러면 연출(클립 길이)이 난이도를 깎았다 (#269/#572). " +
             "Knockdown01-StandUp 클립 길이(1.17초) ÷ 재생속도 배율(빌더 k_standUpSpeed) — 배율을 2에서 1로 내리며 0.585→1.17이 됐다. " +
             "래그돌에서 넘어오는 순간 벌떡 서는 것이 어색해 플레이어와 같은 정상 속도로 맞춘 값이다. " +
             "⚠ 이 구간에는 밧줄이 걸리지 않는다(NpcStun.IsRising) — 일어나던 몸을 묶어 도로 눕히지 않기 위해서다")]
    [SerializeField] private float m_standUpSeconds = 1.17f;

    public float StunSeconds => m_stunSeconds;

    public float KnockdownStunSeconds => m_knockdownStunSeconds;
    public float StandUpSeconds => m_standUpSeconds;
}
