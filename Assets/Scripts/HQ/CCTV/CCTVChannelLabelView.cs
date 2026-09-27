using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// CCTV 모니터의 채널·위치·상태 라벨을 현재 언어로 표시한다.
/// </summary>
public class CCTVChannelLabelView : MonoBehaviour
{
    [SerializeField]
    private CCTVSwitcher m_switcher;

    [SerializeField]
    private TMP_Text m_label;

    private const string k_table = "HqTable";

    private const string k_separator = " · ";

    private void OnEnable()
    {
        if (m_switcher != null)
            m_switcher.OnDisplayChanged += Refresh;
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
        Refresh();
    }

    private void OnDisable()
    {
        if (m_switcher != null)
            m_switcher.OnDisplayChanged -= Refresh;

        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
    }

    private void HandleLocaleChanged(Locale locale) => Refresh();

    private void Refresh()
    {
        if (m_label == null)
            return;

        if (m_switcher == null || !m_switcher.IsSpawned)
        {
            m_label.text = string.Empty;
            return;
        }

        int channel = m_switcher.CurrentIndex + 1;

        if (m_switcher.ChannelCount == 0)
        {
            m_label.text = LocalizedStrings.Get(k_table, "Hq.Cctv.NoChannel");
            return;
        }

        if (m_switcher.IsExternallyJammed)
        {
            m_label.text = LocalizedStrings.Get(k_table, "Hq.Cctv.NoSignal");
            return;
        }

        if (!m_switcher.IsPowered)
        {
            m_label.text = LocalizedStrings.Get(k_table, "Hq.Cctv.PowerOff", channel);
            return;
        }

        string location = m_switcher.CurrentLocationLabel;
        bool ir = m_switcher.IsInfrared;
        string text = string.IsNullOrEmpty(location)
            ? LocalizedStrings.Get(k_table, ir ? "Hq.Cctv.ChannelIr" : "Hq.Cctv.Channel", channel)
            : LocalizedStrings.Get(
                k_table,
                ir ? "Hq.Cctv.ChannelWithLocationIr" : "Hq.Cctv.ChannelWithLocation",
                channel,
                location
            );

        if (m_switcher.PendingEntry >= 0)
            text +=
                k_separator
                + LocalizedStrings.Get(k_table, "Hq.Cctv.Entry", m_switcher.PendingEntry);

        m_label.text = text;
    }
}
