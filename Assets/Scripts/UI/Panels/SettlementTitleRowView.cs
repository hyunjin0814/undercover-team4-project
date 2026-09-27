using TMPro;
using UnityEngine;

/// <summary>
/// 정산 로스터 한 줄 — 이름 + 칭호(없으면 빈 텍스트). TeamStatusRowView와 같은 재사용 관례.
/// </summary>
public class SettlementTitleRowView : MonoBehaviour
{
    [SerializeField]
    private TextMeshProUGUI m_nameText;

    [SerializeField]
    private TextMeshProUGUI m_titleText;

    public void SetName(string displayName)
    {
        if (m_nameText != null)
            m_nameText.text = displayName;
    }

    public void SetTitle(string titleText)
    {
        if (m_titleText != null)
            m_titleText.text = titleText;
    }
}
