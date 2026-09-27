using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 로비에서 드래그앤드롭으로 감정표현 휠 8칸을 구성하는 패널.
/// 구성은 계정별 PlayerPrefs에 로컬 저장한다.
/// </summary>
public class EmoteLoadoutPanel : PanelBase
{
    [Tooltip("감정표현 목록")]
    [SerializeField]
    private EmoteCatalog m_catalog;

    [Tooltip("카탈로그 항목 버튼을 담을 부모")]
    [SerializeField]
    private RectTransform m_catalogContent;

    [Tooltip("카탈로그 항목 프리팹 — EmoteWheelSlotView")]
    [SerializeField]
    private GameObject m_catalogEntryPrefab;

    [Tooltip("휠 8칸 미리보기 — 인덱스 = 슬롯 번호")]
    [SerializeField]
    private EmoteWheelSlotView[] m_slotViews = new EmoteWheelSlotView[EmoteLoadout.k_slotCount];

    [SerializeField]
    private Button m_closeButton;

    private EmoteLoadout m_loadout;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    protected override void Awake()
    {
        base.Awake();

        m_loadout = new EmoteLoadout(App.Net.Auth != null ? App.Net.Auth.PlayerId : null);
        m_loadout.Load();
        BuildCatalogList();
        RefreshSlots();

        for (int slot = 0; slot < m_slotViews.Length; slot++)
        {
            if (m_slotViews[slot] != null)
                EmoteDragHandle.Attach(this, m_slotViews[slot].gameObject, EmoteDragHandle.EKind.Slot, slot);
        }

        if (m_closeButton != null)
            m_closeButton.onClick.AddListener(ClosePanel);
    }

    public override void ClosePanel()
    {
        m_loadout.Save();
        base.ClosePanel();
    }

    private void BuildCatalogList()
    {
        if (m_catalog == null || m_catalogContent == null || m_catalogEntryPrefab == null)
            return;

        for (int index = 0; index < m_catalog.Count; index++)
        {
            EmoteDefinition definition = m_catalog.Get(index);
            if (definition == null)
                continue;

            GameObject entry = Instantiate(m_catalogEntryPrefab, m_catalogContent);

            EmoteWheelSlotView view = entry.GetComponent<EmoteWheelSlotView>();
            if (view != null)
                view.Bind(definition);

            EmoteDragHandle.Attach(this, entry, EmoteDragHandle.EKind.Catalog, index);
        }
    }

    /// <summary>끌기 시작할 때 커서에 붙일 그림 — 빈 칸이면 null이라 드래그가 시작되지 않는다.</summary>
    internal Sprite ResolveIcon(EmoteDragHandle handle)
    {
        if (handle == null || m_catalog == null)
            return null;

        EmoteDefinition definition =
            handle.Kind == EmoteDragHandle.EKind.Catalog
                ? m_catalog.Get(handle.Index)
                : m_catalog.Get(m_catalog.IndexOf(m_loadout.GetSlot(handle.Index)));

        return definition != null ? definition.Icon : null;
    }

    /// <summary>손잡이 위에 떨어뜨렸다 — 카탈로그에서 왔으면 넣고, 칸에서 왔으면 자리를 바꾼다.</summary>
    internal void HandleDrop(EmoteDragHandle from, EmoteDragHandle to)
    {
        if (from == null || to == null || m_catalog == null)
            return;

        if (to.Kind == EmoteDragHandle.EKind.Catalog)
        {
            HandleDropOutside(from);
            return;
        }

        if (from.Kind == EmoteDragHandle.EKind.Catalog)
        {
            EmoteDefinition definition = m_catalog.Get(from.Index);
            SetSlot(to.Index, definition != null ? definition.Id : null);
            return;
        }

        string moved = m_loadout.GetSlot(from.Index);
        SetSlot(from.Index, m_loadout.GetSlot(to.Index));
        SetSlot(to.Index, moved);
    }

    /// <summary>허공에 떨어뜨렸다 — 칸에서 끌어낸 것이면 비운다. 카탈로그에서 끌어낸 것은 무시.</summary>
    internal void HandleDropOutside(EmoteDragHandle from)
    {
        if (from == null || from.Kind != EmoteDragHandle.EKind.Slot)
            return;

        SetSlot(from.Index, null);
    }

    private void SetSlot(int slot, string emoteId)
    {
        m_loadout.SetSlot(slot, emoteId);

        m_loadout.Save(flush: false);

        RefreshSlots();
    }

    private void RefreshSlots()
    {
        for (int slot = 0; slot < m_slotViews.Length; slot++)
        {
            if (m_slotViews[slot] == null)
                continue;

            EmoteDefinition definition = null;
            if (m_catalog != null)
                definition = m_catalog.Get(m_catalog.IndexOf(m_loadout.GetSlot(slot)));

            m_slotViews[slot].Bind(definition);
        }
    }
}
