using TMPro;
using UnityEngine;

/// <summary>
/// 인명부 한 줄 — 정본 이름/종족/세력. CitizenDirectoryView가 Bind로 채운다. 표시 전용.
/// </summary>
public class DirectoryEntryView : MonoBehaviour
{
    [SerializeField]
    private TMP_Text m_nameText;

    [SerializeField]
    private TMP_Text m_typeText;

    [SerializeField]
    private TMP_Text m_factionText;

    public void Bind(in DirectoryEntry entry)
    {
        if (m_nameText != null)
            m_nameText.text = entry.Name.ToString();
        if (m_typeText != null)
            m_typeText.text = OfficialRecords.TypeName(entry.Type);
        if (m_factionText != null)
            m_factionText.text = OfficialRecords.FactionName(entry.Faction);
    }
}
