using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Localization.Settings;

/// <summary>
/// 시민 이름 풀(한글·영문) SO. 서버가 호스트 언어 목록을 골라 라운드 시작에 섞어 쓴다.
/// </summary>
[CreateAssetMenu(fileName = "CitizenNames", menuName = "Scriptable Objects/CitizenNameCatalog")]
public class CitizenNameCatalog : ScriptableObject
{
    [Tooltip("한국어 로케일에서 쓸 이름")]
    [SerializeField] private string[] m_korean;

    [Tooltip("그 밖의 로케일에서 쓸 이름")]
    [SerializeField] private string[] m_english;

    /// <summary>지금 언어에 맞는 목록. 그쪽이 비어 있으면 다른 쪽으로 폴백하고, 둘 다 비면 빈 배열이다.</summary>
    public string[] Resolve()
    {
        bool korean = IsKoreanLocale();
        string[] first = korean ? m_korean : m_english;
        string[] second = korean ? m_english : m_korean;

        if (first != null && first.Length > 0)
            return first;

        if (second != null && second.Length > 0)
            return second;

        return Array.Empty<string>();
    }

    private static bool IsKoreanLocale()
    {
        if (!LocalizationSettings.HasSettings)
            return false;

        var locale = LocalizationSettings.SelectedLocale;
        return locale != null && locale.Identifier.Code.StartsWith("ko");
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        WarnDuplicates(m_korean, nameof(m_korean));
        WarnDuplicates(m_english, nameof(m_english));
    }

    private void WarnDuplicates(string[] names, string listName)
    {
        if (names == null)
            return;

        var seen = new HashSet<string>();
        for (int i = 0; i < names.Length; i++)
        {
            if (!string.IsNullOrEmpty(names[i]) && !seen.Add(names[i]))
                Debug.LogWarning($"[{name}] {listName}에 중복된 이름이 있다: {names[i]}", this);
        }
    }
#endif
}
