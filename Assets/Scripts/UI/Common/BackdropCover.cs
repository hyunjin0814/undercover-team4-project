using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 배경 이미지를 부모 사각형에 cover로 꽉 채우고, 넘치는 부분을 어느 쪽에서 버릴지 정렬로 정한다.
/// </summary>
[RequireComponent(typeof(RectTransform))]
[ExecuteAlways]
public class BackdropCover : MonoBehaviour
{
    [Tooltip("비우면 Image의 스프라이트에서 자동으로 읽는다 — 그림을 갈아도 값이 어긋나지 않는다")]
    [SerializeField]
    private Image m_source;

    [Tooltip("그림의 가로/세로. m_source가 있으면 그쪽이 우선한다")]
    [SerializeField]
    private float m_aspect = 4f / 3f;

    [Tooltip("세로로 남는 부분을 어디서 버릴지 — 0이면 위아래 균등, 1이면 그림 위쪽을 화면 위에 붙인다(위를 최대한 살림)")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_topBias = 0.78f;

    private RectTransform m_rect;
    private Vector2 m_lastParentSize = new Vector2(float.NaN, float.NaN);
    private float m_lastAspect = float.NaN;
    private float m_lastBias = float.NaN;

    private void OnEnable()
    {
        m_rect = (RectTransform)transform;
        Invalidate();
        Apply();
    }

#if UNITY_EDITOR
    private void OnValidate() => Invalidate();
#endif

    private void Invalidate() => m_lastParentSize = new Vector2(float.NaN, float.NaN);

    private void Update() => Apply();

    private void Apply()
    {
        if (m_rect == null)
            m_rect = (RectTransform)transform;

        var parent = m_rect.parent as RectTransform;
        if (parent == null)
            return;

        float aspect = ResolveAspect();
        if (aspect <= 0f)
            return;

        Vector2 parentSize = parent.rect.size;
        if (parentSize.x <= 0f || parentSize.y <= 0f)
            return;

        if (
            parentSize == m_lastParentSize
            && Mathf.Approximately(aspect, m_lastAspect)
            && Mathf.Approximately(m_topBias, m_lastBias)
        )
            return;

        m_lastParentSize = parentSize;
        m_lastAspect = aspect;
        m_lastBias = m_topBias;

        float byWidth = parentSize.x / aspect;
        Vector2 size =
            byWidth >= parentSize.y
                ? new Vector2(parentSize.x, byWidth)
                : new Vector2(parentSize.y * aspect, parentSize.y);

        float excessY = Mathf.Max(0f, size.y - parentSize.y);

        m_rect.anchorMin = m_rect.anchorMax = new Vector2(0.5f, 0.5f);
        m_rect.pivot = new Vector2(0.5f, 0.5f);
        m_rect.sizeDelta = size;
        m_rect.anchoredPosition = new Vector2(0f, -excessY * 0.5f * m_topBias);
    }

    private float ResolveAspect()
    {
        Sprite sprite = m_source != null ? m_source.sprite : null;
        if (sprite != null && sprite.rect.height > 0f)
            return sprite.rect.width / sprite.rect.height;

        return m_aspect;
    }
}
