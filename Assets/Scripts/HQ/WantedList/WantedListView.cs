using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// 본부 모니터의 수배 리스트 — WantedListManager 리스트를 구독해 한 페이지씩 행을 그린다.
/// 페이지 넘김은 버튼과 자동 순환 둘 다 지원한다.
/// </summary>
public class WantedListView : MonoBehaviour
{
    private WantedListManager Manager => App.Game.WantedList;

    [Header("UI 참조")]
    [SerializeField] private RectTransform m_entryContainer;
    [SerializeField] private WantedEntryView m_entryPrefab;

    [Header("페이지")]
    [SerializeField] private TMP_Text m_pageLabel;

    [Tooltip("한 페이지에 그릴 수배 수 — 컨테이너 GridLayoutGroup이 판에 담는 칸 수와 맞출 것")]
    [Min(1)]
    [SerializeField] private int m_entriesPerPage = 6;

    [Tooltip("페이지가 둘 이상일 때 자동으로 넘어가는 간격(초) — 버튼을 누르면 그 시점부터 다시 잰다")]
    [Min(1f)]
    [SerializeField] private float m_autoPageSeconds = 8f;

    private float m_autoPageElapsed;
    private int m_pageCount = 1;

    private readonly List<WantedEntryView> m_rows = new List<WantedEntryView>();
    private int m_page;

    public bool HasMultiplePages => m_pageCount > 1;

    /// <summary>페이지를 넘긴다 — 표시 전용이라 로컬이다.</summary>
    public void ChangePage(int delta)
    {
        m_page += delta;
        m_autoPageElapsed = 0f;
        Rebuild();
    }

    private void Update()
    {
        if (m_pageCount <= 1)
            return;

        m_autoPageElapsed += Time.deltaTime;
        if (m_autoPageElapsed < m_autoPageSeconds)
            return;

        m_autoPageElapsed = 0f;
        m_page = (m_page + 1) % m_pageCount;
        Rebuild();
    }

    private AppearanceDatabase Database => App.Game.Appearance != null ? App.Game.Appearance.Database : null;

    private void OnEnable()
    {
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        if (Manager == null)
        {
            Debug.LogWarning("WantedListView: WantedListManager를 찾지 못해 표시할 수 없다", this);
            return;
        }

        Manager.Wanted.OnListChanged += HandleListChanged;
        Manager.OnListReady += Rebuild;

        if (Manager.IsSpawned)
            Rebuild();
    }

    private void OnDisable()
    {
        if (Manager != null)
        {
            Manager.Wanted.OnListChanged -= HandleListChanged;
            Manager.OnListReady -= Rebuild;
        }

        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
    }

    private void HandleLocaleChanged(Locale locale)
    {
        if (Manager != null && Manager.IsSpawned)
            Rebuild();
    }

    private void HandleListChanged(NetworkListEvent<WantedEntry> _) => Rebuild();

    private void Rebuild()
    {
        if (m_entryPrefab == null || m_entryContainer == null)
        {
            Debug.LogWarning("WantedListView: 행 프리팹/컨테이너가 지정되지 않았다", this);
            return;
        }

        NetworkList<WantedEntry> wanted = Manager.Wanted;

        int perPage = Mathf.Max(1, m_entriesPerPage);
        int pageCount = Mathf.Max(1, Mathf.CeilToInt(wanted.Count / (float)perPage));
        m_pageCount = pageCount;
        m_page = Mathf.Clamp(m_page, 0, pageCount - 1);

        int start = m_page * perPage;
        int visible = Mathf.Clamp(wanted.Count - start, 0, perPage);

        while (m_rows.Count < visible)
            m_rows.Add(Instantiate(m_entryPrefab, m_entryContainer));

        while (m_rows.Count > visible)
        {
            int last = m_rows.Count - 1;
            if (m_rows[last] != null)
                Destroy(m_rows[last].gameObject);
            m_rows.RemoveAt(last);
        }

        AppearanceDatabase database = Database;
        if (database == null && wanted.Count > 0)
            Debug.LogWarning("WantedListView: AppearanceDatabase를 찾지 못해 몽타주를 조립할 수 없다", this);

        for (int i = 0; i < visible; i++)
            m_rows[i].Bind(wanted[start + i], database);

        if (m_pageLabel != null)
        {
            m_pageLabel.gameObject.SetActive(pageCount > 1);
            m_pageLabel.text = $"{m_page + 1} / {pageCount}";
        }
    }
}
