using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// 주문창에 세션 누적 팀 구매 내역을 표시한다(ShopPurchases.Tallies의 Bought).
/// </summary>
public class ShopPurchaseHistoryView : MonoBehaviour
{
    private const string k_shopTable = "ShopTable";
    private const string k_emptyKey = "Shop.History.Empty";

    [Header("데이터")]
    [Tooltip("품목 id가 이 카탈로그의 인덱스다 — ShopLineup·ShopPurchases와 같은 에셋을 잡을 것")]
    [SerializeField]
    private ShopCatalog m_catalog;

    [Header("목록")]
    [SerializeField]
    private RectTransform m_rowContainer;

    [SerializeField]
    private ShopPurchaseRowView m_rowPrefab;

    [Header("문구")]
    [Tooltip("목록 제목 — 예: ShopTable/Shop.History.Title. 비우면 제목을 건드리지 않는다")]
    [SerializeField]
    private LocalizedString m_title;

    [SerializeField]
    private TMP_Text m_titleText;

    [Tooltip("아직 산 것이 없을 때 띄우는 줄. 비워 두면 표시하지 않는다")]
    [SerializeField]
    private TMP_Text m_emptyText;

    private readonly List<ShopPurchaseRowView> m_rows = new List<ShopPurchaseRowView>();

    private readonly List<PurchaseTally> m_shown = new List<PurchaseTally>();

    private ShopPurchases m_bound;

    private void OnEnable()
    {
        if (!m_title.IsEmpty)
            m_title.StringChanged += HandleTitleChanged;

        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        if (!Bind())
            Rebuild();
    }

    private void OnDisable()
    {
        if (!m_title.IsEmpty)
            m_title.StringChanged -= HandleTitleChanged;

        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;

        Unbind();
    }

    private void Update()
    {
        if (m_bound == null)
            Bind();
    }

    private bool Bind()
    {
        ShopPurchases purchases = App.Game.ShopPurchases;
        if (purchases == null || !purchases.IsSpawned)
            return false;

        m_bound = purchases;
        m_bound.Tallies.OnListChanged += HandleTalliesChanged;
        Rebuild();
        return true;
    }

    private void Unbind()
    {
        if (m_bound != null)
            m_bound.Tallies.OnListChanged -= HandleTalliesChanged;

        m_bound = null;
    }

    private void HandleTalliesChanged(NetworkListEvent<PurchaseTally> _) => Rebuild();

    private void HandleLocaleChanged(Locale locale) => Rebuild();

    private void HandleTitleChanged(string localizedTitle)
    {
        if (m_titleText != null)
            m_titleText.text = localizedTitle;
    }

    private void Rebuild()
    {
        if (m_rowPrefab == null || m_rowContainer == null)
            return;

        CollectShown();

        while (m_rows.Count < m_shown.Count)
            m_rows.Add(Instantiate(m_rowPrefab, m_rowContainer));

        while (m_rows.Count > m_shown.Count)
        {
            int last = m_rows.Count - 1;
            if (m_rows[last] != null)
                Destroy(m_rows[last].gameObject);
            m_rows.RemoveAt(last);
        }

        for (int i = 0; i < m_shown.Count; i++)
        {
            PurchaseTally tally = m_shown[i];
            m_rows[i].Bind(m_catalog.Get(tally.CatalogIndex), tally.Bought, tally.Remaining);
        }

        if (m_emptyText != null)
        {
            m_emptyText.text = LocalizedStrings.Get(k_shopTable, k_emptyKey);
            m_emptyText.enabled = m_shown.Count == 0;
        }
    }

    private void CollectShown()
    {
        m_shown.Clear();

        if (m_catalog == null || m_bound == null || !m_bound.IsSpawned)
            return;

        NetworkList<PurchaseTally> tallies = m_bound.Tallies;
        for (int i = 0; i < tallies.Count; i++)
        {
            PurchaseTally tally = tallies[i];

            if (m_catalog.Get(tally.CatalogIndex) != null)
                m_shown.Add(tally);
        }
    }
}
