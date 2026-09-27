using TMPro;
using UnityEngine;

/// <summary>
/// 수배 리스트의 한 줄 — 검거 대상 이름과 몽타주 포트레이트를 표시한다(글 몽타주는 비교용 토글).
/// </summary>
public class WantedEntryView : MonoBehaviour
{
    [Header("몽타주")]
    [SerializeField]
    private MontagePortraitView m_portrait;

    [Header("텍스트 참조")]
    [SerializeField]
    private TMP_Text m_nameText;

    [Tooltip("현상금 표시 (#395). 비워 두면 표시하지 않는다")]
    [SerializeField]
    private TMP_Text m_bountyText;

    [Tooltip("옛 글 방식 몽타주(#497)를 함께 띄운다 — 그림 가독성을 글과 견줄 때만 켠다 (appearance-montage.md §7)")]
    [SerializeField]
    private bool m_showMontageText;

    [SerializeField]
    private TMP_Text m_montageText;

    [Tooltip("수배 조건 표시 (생사 불문/생포 필수, #766). 비워 두면 표시하지 않는다")]
    [SerializeField]
    private TMP_Text m_conditionText;

    private const string k_commonTable = "CommonTable";
    private const string k_moneyKey = "Common.Unit.Money";
    private const string k_hqTable = "HqTable";
    private const string k_conditionPrefix = "Hq.Wanted.Condition.";
    private const string k_missingKey = "Hq.Wanted.Missing";

    public void Bind(in WantedEntry entry, AppearanceDatabase appearanceDatabase)
    {
        if (m_nameText != null)
            m_nameText.text = entry.Name.ToString();

        if (m_portrait != null)
            m_portrait.Bind(entry.Appearance, entry.RevealedAxes, appearanceDatabase);

        if (m_montageText != null)
        {
            m_montageText.gameObject.SetActive(m_showMontageText);
            if (m_showMontageText)
                m_montageText.text = appearanceDatabase != null
                    ? appearanceDatabase.BuildMontageText(entry.Appearance, entry.RevealedAxes)
                    : string.Empty;
        }

        if (m_bountyText != null)
            m_bountyText.text = LocalizedStrings.Get(k_commonTable, k_moneyKey, entry.Bounty);

        if (m_conditionText != null)
            m_conditionText.text = entry.Missing
                ? LocalizedStrings.Get(k_hqTable, k_missingKey)
                : LocalizedStrings.Get(k_hqTable, k_conditionPrefix + entry.Condition);
    }
}
