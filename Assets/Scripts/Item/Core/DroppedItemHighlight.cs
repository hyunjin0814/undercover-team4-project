using EPOOutline;
using UnityEngine;

/// <summary>
/// 바닥에 떨어진 아이템의 상시 윤곽선을 로컬로 켜고 끈다.
/// 조준 피드백과 같은 Outlinable을 공유하며, 조준이 풀리면 Restore로 기본색을 되돌린다.
/// </summary>
public class DroppedItemHighlight : MonoBehaviour
{
    private static readonly Color k_restingColor = new Color(0.25f, 0.85f, 1f, 0.9f);

    private Outlinable m_outlinable;
    private bool m_grounded;

    /// <summary>Outlinable을 만들고 자식 렌더러를 윤곽선 대상으로 수집한다.</summary>
    public void Initialize()
    {
        m_outlinable = GetComponent<Outlinable>();
        if (m_outlinable == null)
        {
            m_outlinable = gameObject.AddComponent<Outlinable>();
            InteractionFeedback.AddOutlineTargets(m_outlinable, gameObject);
        }

        Apply();
    }

    /// <summary>들림/놓임 전환 — 바닥에 있을 때만 하이라이트를 켠다. WorldItemPickup이 호출.</summary>
    public void SetGrounded(bool grounded)
    {
        m_grounded = grounded;
        Apply();
    }

    /// <summary>조준이 풀렸을 때 기본 윤곽선 색으로 되돌린다. 들려 있으면 끈 채로 둔다.</summary>
    public void Restore() => Apply();

    private void Apply()
    {
        if (m_outlinable == null)
            return;

        m_outlinable.OutlineParameters.Color = k_restingColor;

        InteractionFeedback.ApplyJailOutlineLayer(m_outlinable, transform.position);

        m_outlinable.enabled = m_grounded;
    }
}
