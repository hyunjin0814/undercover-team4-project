using System;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 장비 주문창 — 이번 라운드 진열 칸 그리드와 팀 잔액, 구매 내역 탭을 표시한다.
/// 주문은 ShopLineup에 칸 번호로 요청하고 서버가 판정한다.
/// </summary>
public class ShopBrowserPanel : PanelBase
{
    private const string k_commonTable = "CommonTable";
    private const string k_shopTable = "ShopTable";

    private const string k_replyPrefix = "Shop.Reply.";
    private const string k_moneyKey = "Common.Unit.Money";

    public override bool CanCloseWithESC => true;

    public override bool IsStackable => true;

    [Header("목록")]
    [Tooltip("이번 라운드 진열 칸을 들고 있는 홀더 — 같은 씬이라 인스펙터로 잡는다")]
    [SerializeField]
    private ShopLineup m_lineup;

    [Tooltip("그리드 칸. 부착 순서대로 진열 칸에 대응한다")]
    [SerializeField]
    private ShopOrderSlotView[] m_slotViews;

    [Header("탭")]
    [Tooltip("진열 탭이 켜는 것 — 그리드 루트")]
    [SerializeField]
    private GameObject m_catalogView;

    [Tooltip("구매 내역 탭이 켜는 것 — ShopPurchaseHistoryView 루트")]
    [SerializeField]
    private GameObject m_historyView;

    [SerializeField]
    private Button m_catalogTab;

    [SerializeField]
    private Button m_historyTab;

    [Tooltip("선택된 탭 배경 / 글자색")]
    [SerializeField]
    private Color m_tabOnColor = new Color(0.106f, 0.208f, 0.286f, 1f);

    [SerializeField]
    private Color m_tabOnTextColor = new Color(0.541f, 0.902f, 1f, 1f);

    [Tooltip("선택되지 않은 탭 배경 / 글자색")]
    [SerializeField]
    private Color m_tabOffColor = new Color(0.043f, 0.098f, 0.141f, 1f);

    [SerializeField]
    private Color m_tabOffTextColor = new Color(0.443f, 0.514f, 0.573f, 1f);

    [Header("문구")]
    [Tooltip("창 제목 — 예: ShopTable/Shop.Order.Title. 비우면 제목을 건드리지 않는다")]
    [SerializeField]
    private LocalizedString m_title;

    [SerializeField]
    private TMP_Text m_titleText;

    [Tooltip("우상단 팀 잔액")]
    [SerializeField]
    private TMP_Text m_balanceText;

    [Tooltip("주문 결과(성공·자금 부족 등) 알림줄")]
    [SerializeField]
    private TMP_Text m_noticeText;

    [Tooltip("알림 표시 유지 시간(초)")]
    [SerializeField]
    private float m_noticeSeconds = 2.5f;

    [Header("열기 연출")]
    [Tooltip("창이 뜨는 시간(초). 0이면 바로 뜬다")]
    [SerializeField]
    private float m_openSeconds = 0.12f;

    [Tooltip("시작 크기 배율 — 이 값에서 1로 커지며 뜬다")]
    [SerializeField]
    private float m_openFromScale = 0.94f;

    private PlayerInputHandler m_input;
    private TeamFund m_teamFund;

    private ShopLineup m_bound;

    private int m_noticeVersion;

    private RectTransform m_rootRect;
    private CanvasGroup m_rootGroup;

    private int m_openVersion;

    private bool m_historyShown;

    protected override void Awake()
    {
        base.Awake();

        m_rootRect = m_panelRoot.transform as RectTransform;
        m_rootGroup = m_panelRoot.GetComponent<CanvasGroup>();
        if (m_rootGroup == null)
            m_rootGroup = m_panelRoot.AddComponent<CanvasGroup>();

        if (m_slotViews == null)
            m_slotViews = new ShopOrderSlotView[0];

        foreach (ShopOrderSlotView slot in m_slotViews)
        {
            if (slot != null)
                slot.Setup(this);
        }

        if (!m_title.IsEmpty)
            m_title.StringChanged += HandleTitleChanged;

        if (m_catalogTab != null)
            m_catalogTab.onClick.AddListener(HandleCatalogTabClicked);
        if (m_historyTab != null)
            m_historyTab.onClick.AddListener(HandleHistoryTabClicked);

        SwapTabViews(false);
    }

    protected override void OnDestroy()
    {
        if (!m_title.IsEmpty)
            m_title.StringChanged -= HandleTitleChanged;

        if (m_catalogTab != null)
            m_catalogTab.onClick.RemoveListener(HandleCatalogTabClicked);
        if (m_historyTab != null)
            m_historyTab.onClick.RemoveListener(HandleHistoryTabClicked);

        base.OnDestroy();
    }

    private void HandleTitleChanged(string localizedTitle)
    {
        if (m_titleText != null)
            m_titleText.text = localizedTitle;
    }

    /// <summary>주문창을 연다 — 카탈로그 아이템이 오너 클라에서 부른다.</summary>
    public void Open(PlayerInteractor holder)
    {
        if (IsOpened || holder == null)
            return;

        m_input = holder.GetComponent<PlayerInputHandler>();

        BindLineup();
        BindFund();
        ClearNotice();
        ShowTab(false);

        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        SetBlocked(true);
        OpenPanel();
    }

    /// <summary>연출을 붙이려고 가로챈다 — 창이 뚝 나타나지 않게 아주 짧게 키우며 띄운다.</summary>
    public override void OpenPanel()
    {
        base.OpenPanel();

        PlayOpenAsync(++m_openVersion).Forget();
    }

    /// <summary>닫기 — ESC 스택·씬 정리가 모두 여기로 모인다. 정지시킨 입력과 커서를 반드시 되돌린다.</summary>
    public override void ClosePanel()
    {
        if (!IsOpened)
            return;

        base.ClosePanel();

        ResetOpenVisual();
        SetBlocked(false);

        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;

        UnbindLineup();
        UnbindFund();

        m_input = null;
        ClearNotice();
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

        if (m_input == null)
        {
            ClosePanel();
            return;
        }

        if (m_teamFund == null)
            BindFund();
    }

    private void BindLineup()
    {
        UnbindLineup();

        if (m_lineup == null)
        {
            Debug.LogWarning("주문창: ShopLineup이 배선되지 않아 목록을 그릴 수 없다", this);
            RefreshSlots();
            return;
        }

        m_bound = m_lineup;
        m_bound.OnChanged += RefreshSlots;
        m_bound.OnPurchaseReply += ShowNotice;

        RefreshSlots();
    }

    private void UnbindLineup()
    {
        if (m_bound != null)
        {
            m_bound.OnChanged -= RefreshSlots;
            m_bound.OnPurchaseReply -= ShowNotice;
        }

        m_bound = null;
    }

    private void RefreshSlots()
    {
        int slotCount = m_lineup != null ? m_lineup.SlotCount : 0;

        for (int i = 0; i < m_slotViews.Length; i++)
        {
            if (m_slotViews[i] == null)
                continue;

            bool filled = i < slotCount;
            m_slotViews[i].Bind(
                i,
                filled ? m_lineup.GetEntry(i) : null,
                filled ? m_lineup.GetStatus(i) : EShopSlotStatus.Available
            );
        }
    }

    private void HandleLocaleChanged(Locale locale)
    {
        RefreshSlots();
        RefreshTabLabels();

        if (m_teamFund != null)
            RefreshBalance(m_teamFund.Balance);
    }

    private void HandleCatalogTabClicked() => ShowTab(false);

    private void HandleHistoryTabClicked() => ShowTab(true);

    private void ShowTab(bool history)
    {
        SwapTabViews(history);
        RefreshTabLabels();
    }

    private void SwapTabViews(bool history)
    {
        m_historyShown = history;

        if (m_catalogView != null)
            m_catalogView.SetActive(!history);
        if (m_historyView != null)
            m_historyView.SetActive(history);
    }

    private void RefreshTabLabels()
    {
        ApplyTabLook(m_catalogTab, !m_historyShown, "Shop.Tab.Catalog");
        ApplyTabLook(m_historyTab, m_historyShown, "Shop.History.Title");
    }

    private void ApplyTabLook(Button tab, bool selected, string labelKey)
    {
        if (tab == null)
            return;

        Image background = tab.GetComponent<Image>();
        if (background != null)
            background.color = selected ? m_tabOnColor : m_tabOffColor;

        TMP_Text label = tab.GetComponentInChildren<TMP_Text>();
        if (label != null)
        {
            label.text = LocalizedStrings.Get(k_shopTable, labelKey);
            label.color = selected ? m_tabOnTextColor : m_tabOffTextColor;
        }
    }

    private void BindFund()
    {
        TeamFund fund = App.Game.TeamFund;
        if (fund == null || !fund.IsSpawned)
            return;

        m_teamFund = fund;
        m_teamFund.Fund.OnValueChanged += HandleFundChanged;
        RefreshBalance(m_teamFund.Balance);
    }

    private void UnbindFund()
    {
        if (m_teamFund != null)
            m_teamFund.Fund.OnValueChanged -= HandleFundChanged;

        m_teamFund = null;
    }

    private void HandleFundChanged(int previous, int current) => RefreshBalance(current);

    private void RefreshBalance(int balance)
    {
        if (m_balanceText != null)
            m_balanceText.text = LocalizedStrings.Get(k_commonTable, k_moneyKey, balance);
    }

    /// <summary>해당 칸의 주문을 서버에 요청한다.</summary>
    internal void RequestOrder(int slot)
    {
        if (m_lineup == null)
            return;

        m_lineup.RequestPurchase(slot);
    }

    private void ShowNotice(EShopReply reply)
    {
        if (m_noticeText == null)
            return;

        m_noticeText.text = LocalizedStrings.Get(k_shopTable, k_replyPrefix + reply);
        HideNoticeAfterAsync(++m_noticeVersion).Forget();
    }

    private async UniTaskVoid HideNoticeAfterAsync(int version)
    {
        await UniTask.Delay(
            TimeSpan.FromSeconds(m_noticeSeconds),
            cancellationToken: this.GetCancellationTokenOnDestroy()
        );

        if (version != m_noticeVersion)
            return;

        ClearNotice();
    }

    private void ClearNotice()
    {
        m_noticeVersion++;

        if (m_noticeText != null)
            m_noticeText.text = string.Empty;
    }

    private async UniTaskVoid PlayOpenAsync(int version)
    {
        if (m_openSeconds <= 0f)
        {
            ResetOpenVisual();
            return;
        }

        float elapsed = 0f;
        while (elapsed < m_openSeconds)
        {
            if (version != m_openVersion)
                return;

            ApplyOpenProgress(elapsed / m_openSeconds);

            elapsed += Time.unscaledDeltaTime;
            await UniTask.Yield(PlayerLoopTiming.Update, this.GetCancellationTokenOnDestroy());
        }

        if (version == m_openVersion)
            ApplyOpenProgress(1f);
    }

    private void ApplyOpenProgress(float progress)
    {
        float eased = 1f - (1f - progress) * (1f - progress);

        if (m_rootGroup != null)
            m_rootGroup.alpha = eased;

        if (m_rootRect != null)
            m_rootRect.localScale = Vector3.one * Mathf.LerpUnclamped(m_openFromScale, 1f, eased);
    }

    private void ResetOpenVisual()
    {
        m_openVersion++;
        ApplyOpenProgress(1f);
    }
}
