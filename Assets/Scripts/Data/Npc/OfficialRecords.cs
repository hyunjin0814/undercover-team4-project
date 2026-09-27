using UnityEngine;

/// <summary>
/// 시민 타입·세력 정의와 세력 심볼·이름 풀을 담은 공식 기록 SO.
/// 타입·세력의 표시 문구를 현재 언어로 조회한다.
/// </summary>
[CreateAssetMenu(fileName = "OfficialRecord", menuName = "Scriptable Objects/OfficialRecord")]
public class OfficialRecords : ScriptableObject
{
    [LocalizedEnum(k_table, "Npc.CitizenType.")]
    public enum CitizenType
    {
        Human,
        Android,
    }

    [LocalizedEnum(k_table, "Npc.Faction.")]
    public enum Faction
    {
        None,
        FactionA,
        FactionB,
    }

    private const string k_table = "NpcTable";

    /// <summary>시민 타입의 표시 문구를 현재 언어로 돌려준다.</summary>
    public static string TypeName(CitizenType type) =>
        LocalizedStrings.Get(k_table, "Npc.CitizenType." + type);

    /// <summary>세력의 표시 문구. 갱신 책임은 <see cref="TypeName"/>과 같다.</summary>
    public static string FactionName(Faction faction) =>
        LocalizedStrings.Get(k_table, "Npc.Faction." + faction);

    [System.Serializable]
    public struct FactionSymbolSet
    {
        public Faction faction;
        public Sprite[] variants;
    }

    [SerializeField]
    private FactionSymbolSet[] m_factionSymbolSets;

    public Sprite[] GetVariants(Faction faction)
    {
        foreach (var set in m_factionSymbolSets)
            if (set.faction == faction)
                return set.variants;

        return null;
    }

    public int GetVariantsCount(Faction faction)
    {
        Sprite[] variants = GetVariants(faction);
        return variants != null ? variants.Length : 0;
    }

    [Tooltip("시민 이름 풀 — 비워 두면 이름이 배정되지 않는다 (#752)")]
    [SerializeField] private CitizenNameCatalog m_citizenNames;

    public CitizenNameCatalog CitizenNames => m_citizenNames;

    public Sprite GetFactionSymbol(Faction faction, int index)
    {
        Sprite[] variants = GetVariants(faction);
        if (variants == null || index < 0 || index >= variants.Length)
            return null;

        return variants[index];
    }
}
