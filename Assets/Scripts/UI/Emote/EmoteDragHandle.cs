using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 감정표현 휠 구성용 드래그 손잡이 — 카탈로그 항목과 휠 칸에 런타임으로 붙고, 판정은 EmoteLoadoutPanel에 넘긴다.
/// </summary>
public class EmoteDragHandle
    : MonoBehaviour,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler,
        IDropHandler
{
    public enum EKind
    {
        Catalog,
        Slot,
    }

    private const float k_ghostAlpha = 0.75f;
    private const string k_dropAreaName = "~DropArea";

    private static EmoteDragHandle s_dragging;

    private EmoteLoadoutPanel m_panel;
    private EKind m_kind;
    private int m_index;
    private RectTransform m_ghost;
    private bool m_handled;

    public EKind Kind => m_kind;

    public int Index => m_index;

    /// <summary>손잡이를 붙이거나 이미 붙은 것을 다시 배선한다 — 패널이 목록을 만들 때 부른다.</summary>
    public static void Attach(EmoteLoadoutPanel panel, GameObject target, EKind kind, int index)
    {
        if (target == null)
            return;

        EmoteDragHandle handle = target.GetComponent<EmoteDragHandle>();
        if (handle == null)
            handle = target.AddComponent<EmoteDragHandle>();

        handle.m_panel = panel;
        handle.m_kind = kind;
        handle.m_index = index;
        handle.EnsureDropArea();
    }

    private void EnsureDropArea()
    {
        if (transform.Find(k_dropAreaName) != null)
            return;

        var area = new GameObject(k_dropAreaName, typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)area.transform;
        rect.SetParent(transform, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.SetAsFirstSibling();

        Image image = area.GetComponent<Image>();
        image.color = new Color(0f, 0f, 0f, 0f);
        image.raycastTarget = true;
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        Sprite sprite = m_panel != null ? m_panel.ResolveIcon(this) : null;
        if (sprite == null)
            return;

        s_dragging = this;
        m_handled = false;
        CreateGhost(sprite);
        MoveGhost(eventData);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (s_dragging == this)
            MoveGhost(eventData);
    }

    public void OnDrop(PointerEventData eventData)
    {
        if (s_dragging == null)
            return;

        if (s_dragging == this)
        {
            m_handled = true;
            return;
        }

        s_dragging.m_handled = true;
        if (m_panel != null)
            m_panel.HandleDrop(s_dragging, this);
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        if (s_dragging != this)
            return;

        DestroyGhost();
        s_dragging = null;

        if (!m_handled && m_panel != null)
            m_panel.HandleDropOutside(this);

        m_handled = false;
    }

    private void OnDisable()
    {
        if (s_dragging == this)
            s_dragging = null;

        DestroyGhost();
    }

    private void CreateGhost(Sprite sprite)
    {
        Canvas canvas = GetComponentInParent<Canvas>();
        if (canvas == null)
            return;

        var ghost = new GameObject("~EmoteDragGhost", typeof(RectTransform), typeof(Image));
        m_ghost = (RectTransform)ghost.transform;
        m_ghost.SetParent(canvas.rootCanvas.transform, false);
        m_ghost.sizeDelta = ((RectTransform)transform).rect.size;
        m_ghost.SetAsLastSibling();

        Image image = ghost.GetComponent<Image>();
        image.sprite = sprite;
        image.raycastTarget = false;
        image.color = new Color(1f, 1f, 1f, k_ghostAlpha);
    }

    private void MoveGhost(PointerEventData eventData)
    {
        if (m_ghost == null)
            return;

        var parent = m_ghost.parent as RectTransform;
        Canvas canvas = m_ghost.GetComponentInParent<Canvas>();
        if (parent == null || canvas == null)
            return;

        Camera camera =
            canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        if (
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                parent,
                eventData.position,
                camera,
                out Vector2 local
            )
        )
            m_ghost.localPosition = local;
    }

    private void DestroyGhost()
    {
        if (m_ghost == null)
            return;

        Destroy(m_ghost.gameObject);
        m_ghost = null;
    }
}
