using UnityEngine.Localization.Settings;

/// <summary>
/// 테이블 이름 + 키로 문자열을 즉시 조회하는 헬퍼 — 규약 기반 키나 전역 서식처럼 인스펙터로 고를 것이 없는 자리에 쓴다.
/// 언어 변경 시 다시 그리는 것은 호출부 책임이다.
/// </summary>
public static class LocalizedStrings
{
    /// <summary>키를 현재 언어로 해석한다. 설정이 아직 없으면 키를 그대로 돌려준다.</summary>
    public static string Get(string table, string key, params object[] args)
    {
        if (string.IsNullOrEmpty(table) || string.IsNullOrEmpty(key))
            return key;

        if (!LocalizationSettings.HasSettings)
            return key;

#if UNITY_EDITOR
        WarnIfMissing(table, key);
#endif

        string value =
            args != null && args.Length > 0
                ? LocalizationSettings.StringDatabase.GetLocalizedString(table, key, args)
                : LocalizationSettings.StringDatabase.GetLocalizedString(table, key);

        return string.IsNullOrEmpty(value) ? key : value;
    }

#if UNITY_EDITOR
    private static readonly System.Collections.Generic.HashSet<string> s_warned =
        new System.Collections.Generic.HashSet<string>();

    private static void WarnIfMissing(string table, string key)
    {
        string id = table + "/" + key;
        if (s_warned.Contains(id))
            return;

        var stringTable = LocalizationSettings.StringDatabase.GetTable(table);
        if (stringTable != null && stringTable.GetEntry(key) != null)
            return;

        s_warned.Add(id);
        UnityEngine.Debug.LogWarning(
            $"[LocalizedStrings] '{table}'에 키 '{key}'가 없다. "
                + "규약 기반 키라면 enum 값만 늘고 테이블 키가 빠진 것이다 — "
                + "Tools ▸ Localization ▸ 규약 키 검증으로 전체를 확인할 것."
        );
    }
#endif
}
