using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// 본부 소모품 재고 게시판 — 상점에서 사서 아직 안 쓴 소모품의 남은 수를 표시한다.
/// ShopPurchases.Tallies를 원본으로 읽는다.
/// </summary>
public class HqStockBoard : MonoBehaviour
{
    [Header("데이터")]
    [Tooltip("소모품 목록과 품목 id의 출처 — ShopPurchases와 같은 에셋을 잡을 것")]
    [SerializeField]
    private ShopCatalog m_catalog;

    [Header("목록")]
    [SerializeField]
    private RectTransform m_rowContainer;

    [SerializeField]
    private HqStockRowView m_rowPrefab;

    [Header("문구")]
    [Tooltip("게시판 제목 — 예: HqTable/Hq.Stock.Title. 비우면 제목을 건드리지 않는다")]
    [SerializeField]
    private LocalizedString m_title;

    [SerializeField]
    private TMP_Text m_titleText;

    private readonly List<HqStockRowView> m_rows = new List<HqStockRowView>();

    private readonly List<int> m_consumables = new List<int>();

    private ShopPurchases m_bound;

    private void OnEnable()
    {
        if (!m_title.IsEmpty)
            m_title.StringChanged += HandleTitleChanged;

        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        CollectConsumables();

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

    private void CollectConsumables()
    {
        m_consumables.Clear();

        if (m_catalog == null)
        {
            Debug.LogWarning("HqStockBoard: 카탈로그가 배선되지 않아 재고를 띄울 수 없다", this);
            return;
        }

        for (int i = 0; i < m_catalog.Count; i++)
        {
            ShopCatalog.Entry entry = m_catalog.Get(i);
            if (entry != null && entry.IsValid && entry.IsConsumable)
                m_consumables.Add(i);
        }
    }

    private void Rebuild()
    {
        if (m_rowPrefab == null || m_rowContainer == null)
            return;

        while (m_rows.Count < m_consumables.Count)
            m_rows.Add(Instantiate(m_rowPrefab, m_rowContainer));

        while (m_rows.Count > m_consumables.Count)
        {
            int last = m_rows.Count - 1;
            if (m_rows[last] != null)
                Destroy(m_rows[last].gameObject);
            m_rows.RemoveAt(last);
        }

        for (int i = 0; i < m_consumables.Count; i++)
        {
            int catalogIndex = m_consumables[i];
            m_rows[i].Bind(m_catalog.Get(catalogIndex), Remaining(catalogIndex));
        }
    }

    private int Remaining(int catalogIndex)
    {
        if (m_bound == null || !m_bound.IsSpawned)
            return 0;

        NetworkList<PurchaseTally> tallies = m_bound.Tallies;
        for (int i = 0; i < tallies.Count; i++)
        {
            PurchaseTally tally = tallies[i];
            if (tally.CatalogIndex == catalogIndex)
                return tally.Remaining;
        }

        return 0;
    }
}
