using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// 이번 세션의 세력별 진짜 문양을 나열해 표시한다.
/// </summary>
public class FactionSymbolBoardView : HqPanelView
{
    [Header("목록")]
    [SerializeField]
    private OfficialRecords m_officialRecords;

    [SerializeField]
    private RectTransform m_rowContainer;

    [SerializeField]
    private FactionSymbolRowView m_rowPrefab;

    private readonly List<FactionSymbolRowView> m_rows = new();
    private readonly List<OfficialRecords.Faction> m_shown = new();

    private FactionSymbolManager m_manager;

    protected override void OnOpened()
    {
        m_manager = App.Game.FactionSymbol;
        if (m_manager != null)
            m_manager.OnRealIndicesChanged += Rebuild;

        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        Rebuild();
    }

    protected override void OnClosed()
    {
        if (m_manager != null)
            m_manager.OnRealIndicesChanged -= Rebuild;
        m_manager = null;

        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
    }

    private void HandleLocaleChanged(Locale locale) => Rebuild();

    private void Rebuild()
    {
        if (m_officialRecords == null || m_rowPrefab == null || m_rowContainer == null)
            return;

        m_shown.Clear();
        foreach (OfficialRecords.Faction faction in Enum.GetValues(typeof(OfficialRecords.Faction)))
        {
            if (m_officialRecords.GetVariantsCount(faction) > 0)
                m_shown.Add(faction);
        }

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
            OfficialRecords.Faction faction = m_shown[i];
            int real = m_manager != null ? m_manager.RealIndex(faction) : 0;
            m_rows[i].Bind(faction, m_officialRecords.GetFactionSymbol(faction, real));
        }
    }
}
