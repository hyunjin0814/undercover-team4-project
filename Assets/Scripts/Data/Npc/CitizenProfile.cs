using UnityEngine;

/// <summary>
/// 시민 한 명의 정본 신원(이름·타입·세력)과 스캔 표시값을 담는 런타임 프로필.
/// </summary>
[CreateAssetMenu(fileName = "CitizenProfile", menuName = "Scriptable Objects/CitizenProfile")]
public class CitizenProfile : ScriptableObject
{
    [Header("실제 데이터")]
    [SerializeField] private string m_citizenName;
    [SerializeField] private OfficialRecords.CitizenType m_citizenType;
    [SerializeField] private OfficialRecords.Faction m_faction;

    public string CitizenName => m_citizenName;
    public OfficialRecords.CitizenType CitizenType => m_citizenType;
    public OfficialRecords.Faction Faction => m_faction;

    [Header("스캔으로 확인할 결과")]
    public string m_nameView;

    public OfficialRecords.CitizenType m_typeView;
    public OfficialRecords.Faction m_factionView;
    public Sprite m_symbolView;
    public int m_symbolIndexView;

    /// <summary>런타임 생성용 초기화 — 실제 데이터를 채우고 표시값을 정본과 동일하게 세팅한다. (이슈 #38)</summary>
    public void Initialize(string citizenName, OfficialRecords.CitizenType citizenType,
        OfficialRecords.Faction faction, int symbolIndex, OfficialRecords officialRecords)
    {
        m_citizenName = citizenName;
        m_citizenType = citizenType;
        m_faction = faction;

        m_nameView = citizenName;
        m_typeView = citizenType;
        m_factionView = faction;
        m_symbolIndexView = symbolIndex;
        m_symbolView = officialRecords != null ? officialRecords.GetFactionSymbol(faction, symbolIndex) : null;
    }
}
