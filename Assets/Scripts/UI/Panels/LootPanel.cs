using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 약탈 창 — 기능 정지된 동료의 소지품과 개인 자금을 보여 주고 칸 클릭으로 가져가기를 요청한다.
/// 열려 있는 동안 입력을 정지하며, 권한은 매 요청마다 서버가 검증한다.
/// </summary>
public class LootPanel : PanelBase
{
    public override bool CanCloseWithESC => true;

    public override bool IsStackable => true;

    [Header("슬롯")]
    [Tooltip("소지 5칸(GDD 10-1)에 맞춘 칸 뷰. 부착 순서대로 채운다")]
    [SerializeField]
    private LootSlotView[] m_slotViews;

    [Tooltip("개인 자금 칸 — 누르면 전액 가져간다. 없어도 소지품 약탈은 동작한다")]
    [SerializeField]
    private LootFundsView m_fundsView;

    [Header("문구")]
    [Tooltip("창 제목 — 예: HudTable/Hud.Loot.Title. 비우면 제목을 건드리지 않는다")]
    [SerializeField]
    private LocalizedString m_title;

    [SerializeField]
    private TMP_Text m_titleText;

    [Header("자동 닫기")]
    [Tooltip(
        "이 거리(m)를 넘게 벌어지면 창을 닫는다. 조준 사거리(3m)보다 넉넉하게 둘 것 — 경계에서 "
        + "창이 깜빡이지 않게 하려는 여유이고, 실제 거부는 서버가 한다"
    )]
    [SerializeField]
    private float m_keepOpenRange = 4.5f;

    private PlayerLooter m_looter;
    private PlayerLootable m_victim;
    private PlayerInputHandler m_input;
    private PlayerIncapacitation m_looterIncapacitation;
    private PlayerLoadout m_watched;

    private int m_openedFrame = -1;

    private readonly List<ItemBase> m_lootable = new List<ItemBase>();

    private ItemBase[] m_lastShown;

    protected override void Awake()
    {
        base.Awake();

        if (m_slotViews == null)
            m_slotViews = new LootSlotView[0];

        m_lastShown = new ItemBase[m_slotViews.Length];

        foreach (LootSlotView slot in m_slotViews)
        {
            if (slot != null)
                slot.Setup(this);
        }

        if (m_fundsView != null)
            m_fundsView.Setup(this);

        if (!m_title.IsEmpty)
            m_title.StringChanged += HandleTitleChanged;
    }

    protected override void OnDestroy()
    {
        if (!m_title.IsEmpty)
            m_title.StringChanged -= HandleTitleChanged;

        base.OnDestroy();
    }

    private void HandleTitleChanged(string localizedTitle)
    {
        if (m_titleText != null)
            m_titleText.text = localizedTitle;
    }

    /// <summary>서버 확인 후 약탈 창을 연다(아무것도 옮기지 않는다).</summary>
    public void Open(PlayerLooter looter, PlayerLootable victim, int availableFunds)
    {
        if (IsOpened || looter == null || victim == null)
            return;

        m_looter = looter;
        m_victim = victim;
        m_input = looter.GetComponent<PlayerInputHandler>();
        m_looterIncapacitation = looter.GetComponent<PlayerIncapacitation>();

        m_watched = victim.Loadout;
        if (m_watched != null)
            m_watched.OnHeldItemsChangedAnyPeer += HandleVictimItemsChanged;

        m_openedFrame = Time.frameCount;

        RefreshSlots(force: true);

        if (m_fundsView != null)
            m_fundsView.Bind(availableFunds);

        SetBlocked(true);
        OpenPanel();
    }

    /// <summary>서버가 알려 준 자금 이전 결과로 자금 칸을 갱신한다(대상이 같을 때만).</summary>
    internal void SetFunds(PlayerLootable victim, int amount)
    {
        if (!IsOpened || victim == null || victim != m_victim)
            return;

        if (m_fundsView != null)
            m_fundsView.Bind(amount);
    }

    /// <summary>창을 닫고 정지시킨 입력과 커서를 되돌린다.</summary>
    public override void ClosePanel()
    {
        if (!IsOpened)
            return;

        base.ClosePanel();

        SetBlocked(false);

        if (m_watched != null)
            m_watched.OnHeldItemsChangedAnyPeer -= HandleVictimItemsChanged;
        m_watched = null;

        m_looter = null;
        m_victim = null;
        m_input = null;
        m_looterIncapacitation = null;

        ClearSlots();
    }

    /// <summary>창이 열린 채 비활성·파괴될 때 입력 정지와 커서 해제를 되돌린다.</summary>
    private void OnDisable()
    {
        ClosePanel();
        SetBlocked(false);
    }

    protected override PlayerInputHandler BlockTarget => m_input;

    private void Update()
    {
        if (!IsOpened)
            return;

        if (!CanKeepOpen())
        {
            ClosePanel();
            return;
        }

        if (Time.frameCount != m_openedFrame && m_input.WasLootPressedThisFrame())
            ClosePanel();
    }

    private void HandleVictimItemsChanged() => RefreshSlots(force: false);

    private bool CanKeepOpen()
    {
        if (m_looter == null || m_input == null)
            return false;

        if (m_looterIncapacitation != null && m_looterIncapacitation.IsIncapacitated)
            return false;

        if (m_victim == null || !m_victim.CanBeLooted)
            return false;

        Vector3 delta = m_victim.transform.position - m_looter.transform.position;
        return delta.sqrMagnitude <= m_keepOpenRange * m_keepOpenRange;
    }

    private void RefreshSlots(bool force)
    {
        m_lootable.Clear();
        if (m_victim != null && m_victim.Loadout != null)
            m_victim.Loadout.CollectDetachableItems(m_lootable);

        if (!force && !HasChanged())
            return;

        for (int i = 0; i < m_slotViews.Length; i++)
        {
            ItemBase item = i < m_lootable.Count ? m_lootable[i] : null;
            m_lastShown[i] = item;

            if (m_slotViews[i] != null)
                m_slotViews[i].Bind(item);
        }
    }

    private bool HasChanged()
    {
        for (int i = 0; i < m_slotViews.Length; i++)
        {
            ItemBase item = i < m_lootable.Count ? m_lootable[i] : null;
            if (m_lastShown[i] != item)
                return true;
        }

        return false;
    }

    private void ClearSlots()
    {
        for (int i = 0; i < m_slotViews.Length; i++)
        {
            m_lastShown[i] = null;
            if (m_slotViews[i] != null)
                m_slotViews[i].Bind(null);
        }

        if (m_fundsView != null)
            m_fundsView.Bind(0);
    }

    /// <summary>아이템 가져가기를 서버에 요청한다.</summary>
    internal void RequestTake(ItemBase item)
    {
        if (m_looter == null || m_victim == null || item == null)
            return;

        m_looter.RequestTakeItem(m_victim, item);
    }

    /// <summary>개인 자금 전액 가져가기를 서버에 요청한다.</summary>
    internal void RequestTakeFunds()
    {
        if (m_looter == null || m_victim == null)
            return;

        m_looter.RequestTakeFunds(m_victim);
    }
}
