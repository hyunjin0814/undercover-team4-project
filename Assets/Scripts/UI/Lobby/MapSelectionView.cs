using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// Shop 씬 맵 선택 콘솔에 다음 맵의 이름·항공뷰·NPC 수를 전원에게 표시한다.
/// </summary>
public class MapSelectionView : MonoBehaviour
{
    [SerializeField]
    private TMP_Text m_label;

    [Header("맵 정보 (#611)")]
    [Tooltip("항공뷰 이미지가 들어갈 자리. 비워 두면 이미지 없이 이름만 뜬다")]
    [SerializeField]
    private Image m_preview;

    [Tooltip("스폰 NPC 수를 찍을 라벨. 비워 두면 숫자를 표시하지 않는다")]
    [SerializeField]
    private TMP_Text m_npcCountLabel;

    private const string k_shopTable = "ShopTable";
    private const string k_nextKey = "Shop.MapSelect.Next";
    private const string k_npcCountKey = "Shop.MapSelect.NpcCount";
    private const string k_noMapKey = "Shop.MapSelect.NoMap";

    private MapSelection m_bound;

    private Vector2 m_previewSize;

    private void Awake()
    {
        if (m_preview != null)
            m_previewSize = m_preview.rectTransform.sizeDelta;
    }

    private void OnEnable() => LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

    private void OnDisable()
    {
        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;

        Bind(null);
    }

    private void HandleLocaleChanged(Locale locale) => Refresh();

    private void Update()
    {
        MapSelection current = App.Game.MapSelection;
        if (!ReferenceEquals(current, m_bound))
            Bind(current);
    }

    private void Bind(MapSelection target)
    {
        if (m_bound != null)
            m_bound.OnSelectionChanged -= Refresh;

        m_bound = target;

        if (m_bound != null)
            m_bound.OnSelectionChanged += Refresh;

        Refresh();
    }

    private void Refresh()
    {
        if (m_bound == null || !m_bound.IsSpawned)
        {
            SetText(m_label, string.Empty);
            SetText(m_npcCountLabel, string.Empty);
            SetPreview(null, false);
            return;
        }

        string name = m_bound.SelectedDisplayName ?? LocalizedStrings.Get(k_shopTable, k_noMapKey);
        SetText(m_label, LocalizedStrings.Get(k_shopTable, k_nextKey, name));
        SetText(m_npcCountLabel, LocalizedStrings.Get(k_shopTable, k_npcCountKey, m_bound.SelectedNpcCount));
        SetPreview(m_bound.SelectedPreview, m_bound.SelectedPreviewRotated);
    }

    private static void SetText(TMP_Text label, string text)
    {
        if (label != null)
            label.text = text;
    }

    private void SetPreview(Sprite sprite, bool rotated)
    {
        if (m_preview == null)
            return;

        m_preview.sprite = sprite;
        m_preview.enabled = sprite != null;

        m_preview.rectTransform.sizeDelta = rotated
            ? new Vector2(m_previewSize.y, m_previewSize.x)
            : m_previewSize;

        m_preview.rectTransform.localRotation = rotated
            ? Quaternion.Euler(0f, 0f, 90f)
            : Quaternion.identity;
    }
}
