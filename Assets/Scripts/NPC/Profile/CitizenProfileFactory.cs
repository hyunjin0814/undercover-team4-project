using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 시민 한 명분의 신원(이름·타입·세력·문양)을 만든다.
/// 이름 풀은 생성 시 섞어 라운드 안에서 중복되지 않게 한다.
/// </summary>
public sealed class CitizenProfileFactory
{
    private static readonly OfficialRecords.Faction[] s_assignableFactions =
        BuildAssignableFactions();

    private readonly OfficialRecords m_records;

    private readonly string[] m_shuffledNames;

    private int m_issuedCount;

    private readonly Dictionary<OfficialRecords.Faction, int> m_localRealIndices =
        new Dictionary<OfficialRecords.Faction, int>();

    public CitizenProfileFactory(OfficialRecords records)
    {
        m_records = records;
        m_shuffledNames = ShuffledPool(records != null ? records.CitizenNames : null);
    }

    /// <summary>다음 시민 프로필을 만든다 — 이름은 섞인 풀에서 순서대로, 타입·세력은 추첨한다.</summary>
    public CitizenProfile Create()
    {
        OfficialRecords.Faction faction = RandomFaction();

        CitizenProfile profile = ScriptableObject.CreateInstance<CitizenProfile>();
        profile.Initialize(
            NextName(),
            RandomEnum<OfficialRecords.CitizenType>(),
            faction,
            RealSymbolIndex(faction),
            m_records
        );
        return profile;
    }

    /// <summary>이번 세션에 이 세력의 진짜 문양 index. 세션 중이면 동기화 값, 오프라인이면 로컬 폴백.</summary>
    public int RealSymbolIndex(OfficialRecords.Faction faction)
    {
        FactionSymbolManager manager = App.Game.FactionSymbol;
        if (manager != null)
            return manager.RealIndex(faction);

        if (!m_localRealIndices.TryGetValue(faction, out int index))
        {
            int count = m_records != null ? m_records.GetVariantsCount(faction) : 0;
            index = count > 0 ? Random.Range(0, count) : 0;
            m_localRealIndices[faction] = index;
        }
        return index;
    }

    private static OfficialRecords.Faction RandomFaction() =>
        s_assignableFactions.Length > 0
            ? s_assignableFactions[Random.Range(0, s_assignableFactions.Length)]
            : OfficialRecords.Faction.None;

    private static OfficialRecords.Faction[] BuildAssignableFactions()
    {
        var all = (OfficialRecords.Faction[])Enum.GetValues(typeof(OfficialRecords.Faction));
        var list = new List<OfficialRecords.Faction>(all.Length);
        foreach (OfficialRecords.Faction faction in all)
            if (faction != OfficialRecords.Faction.None)
                list.Add(faction);
        return list.ToArray();
    }

    /// <summary>이름 풀을 피셔-예이츠로 섞은 사본. 뽑기 전에 한 번만 돌린다.</summary>
    private static string[] ShuffledPool(CitizenNameCatalog catalog)
    {
        string[] source = catalog != null ? catalog.Resolve() : Array.Empty<string>();
        if (source.Length == 0)
        {
            Debug.LogWarning(
                "[CitizenProfileFactory] 이름 풀이 비어 있다 — OfficialRecords의 이름 카탈로그 배선을 확인할 것 (#752)"
            );
            return source;
        }

        string[] shuffled = (string[])source.Clone();
        for (int i = shuffled.Length - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }
        return shuffled;
    }

    private string NextName()
    {
        int index = m_issuedCount++;
        int length = m_shuffledNames.Length;

        if (length == 0)
            return "Citizen " + (index + 1);

        return index < length
            ? m_shuffledNames[index]
            : $"{m_shuffledNames[index % length]} {index / length + 1}";
    }

    private static TEnum RandomEnum<TEnum>()
        where TEnum : Enum
    {
        Array values = Enum.GetValues(typeof(TEnum));
        return (TEnum)values.GetValue(Random.Range(0, values.Length));
    }
}
