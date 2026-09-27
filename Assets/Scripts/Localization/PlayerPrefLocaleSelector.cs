using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// PlayerPrefs에 저장된 언어를 시작 시 복원하는 로케일 셀렉터. 언어 변경 시 Save를 호출해야 한다.
/// </summary>
[System.Serializable]
public class PlayerPrefLocaleSelector : IStartupLocaleSelector
{
    private const string k_prefKey = "selected-locale";

    public Locale GetStartupLocale(ILocalesProvider availableLocales)
    {
        string code = PlayerPrefs.GetString(k_prefKey, string.Empty);
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }

        return availableLocales.GetLocale(code);
    }

    public static void Save(Locale locale)
    {
        PlayerPrefs.SetString(k_prefKey, locale.Identifier.Code);
        PlayerPrefs.Save();
    }
}