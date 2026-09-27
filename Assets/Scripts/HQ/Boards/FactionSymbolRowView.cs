using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 세력 문양 게시판의 한 줄 — 세력 이름과 문양 이미지를 표시한다.
/// </summary>
public class FactionSymbolRowView : MonoBehaviour
{
    [SerializeField]
    private TMP_Text m_factionText;

    [SerializeField]
    private Image m_symbolImage;

    public void Bind(OfficialRecords.Faction faction, Sprite symbol)
    {
        if (m_factionText != null)
            m_factionText.text = OfficialRecords.FactionName(faction);

        if (m_symbolImage != null)
        {
            m_symbolImage.sprite = symbol;
            m_symbolImage.enabled = symbol != null;
        }
    }
}
