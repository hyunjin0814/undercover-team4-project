using System;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// 치장 아이템 표시 이름을 CosmeticsTable에서 프리팹 이름 키로 찾는다. 없으면 키를 그대로 돌려준다.
/// </summary>
public static class CosmeticNames
{
    private const string k_table = "CosmeticsTable";
    private const string k_noneKey = "Cosmetic.None";

    public static event Action OnLanguageChanged;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Hook()
    {
        OnLanguageChanged = null;
        LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
    }

    private static void HandleLocaleChanged(Locale _) => OnLanguageChanged?.Invoke();

    /// <summary>그 프리팹의 표시 이름 — null이면 "안 씀"이다.</summary>
    public static string Of(GameObject prefab)
    {
        string name = Lookup(prefab == null ? k_noneKey : "Cosmetic." + prefab.name);
        if (!string.IsNullOrEmpty(name))
            return name;

        return prefab == null ? "-" : Prettify(prefab.name);
    }

    private static string Lookup(string key)
    {
        if (LocalizationSettings.SelectedLocaleAsync.IsDone
            && LocalizationSettings.SelectedLocaleAsync.Result == null)
            return null;

        try
        {
            return LocalizationSettings.StringDatabase.GetLocalizedString(k_table, key);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[{nameof(CosmeticNames)}] '{key}'를 읽지 못했다: {ex.Message}");
            return null;
        }
    }

    private static string Prettify(string name)
    {
        string[] tokens = name.Split('_');
        var kept = new System.Collections.Generic.List<string>(tokens.Length);
        foreach (string token in tokens)
            if (token != "Apo" && token != "Pol" && token != "Male" && token != "Female")
                kept.Add(token);

        return kept.Count == 0 ? name : string.Join(" ", kept);
    }
}
