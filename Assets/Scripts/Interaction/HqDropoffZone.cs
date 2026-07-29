using UnityEngine;

/// <summary>
/// 본부 인계 구역 — "어디까지가 인계 가능한 자리인가"만 아는 순수 지오메트리 부품이다. (#59 → #414)
/// 판정 트리거가 콜라이더 도달에서 인계 단말(<see cref="HqDropoffTerminal"/>)의 상호작용키(E)로
/// 옮겨졌으므로, 여기엔 트리거 콜백도 이벤트도 서버 권위 게이트도 없다 — 물어보면 답만 한다.
///
/// 콜라이더는 <b>Convex</b>여야 한다(Box 권장) — 포함 판정에 ClosestPoint를 쓰기 때문이다.
/// 트리거일 필요는 없지만, 플레이어·NPC를 막지 않으려면 Is Trigger로 두는 것이 편하다.
/// </summary>
[RequireComponent(typeof(Collider))]
public class HqDropoffZone : MonoBehaviour
{
    // 안쪽 점은 ClosestPoint가 자기 자신을 돌려주므로 원래는 0이면 되는데, 부동소수 오차를 감안한 여유(m).
    private const float k_containEpsilon = 0.01f;

    private Collider m_collider;

    private void Awake()
    {
        m_collider = GetComponent<Collider>();
    }

    /// <summary>
    /// 이 점이 인계 구역 안인가 — 인계 단말이 "끌고 온 NPC가 구역 안에 있는가"를 이걸로 판정한다. (#414)
    ///
    /// 트리거 진입/이탈 집합을 들고 있지 않은 이유: 수동 트리거로 바뀌면서 "지금 안에 있나"만 알면 되고,
    /// 집합을 들면 NPC 파괴·비활성·워프로 Exit이 누락된 유령 항목을 따로 걸러야 한다.
    ///
    /// <b>높이는 보지 않는다</b> — 끌려오는 NPC는 밧줄에 묶여 바닥에 누운 채라(#369) 발밑 좌표가
    /// 구역 박스 아래로 빠지기 쉽다. 구역은 게임플레이상 "바닥의 자리"라 평면 포함으로 판정하는 것이 맞다.
    /// </summary>
    public bool Contains(Vector3 point)
    {
        if (m_collider == null)
            m_collider = GetComponent<Collider>();

        Vector3 flat = new Vector3(point.x, m_collider.bounds.center.y, point.z);
        return (m_collider.ClosestPoint(flat) - flat).sqrMagnitude <= k_containEpsilon * k_containEpsilon;
    }
}
