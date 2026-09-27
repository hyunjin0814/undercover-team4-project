using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization.Tables;

/// <summary>
/// LocalizedEnum 어트리뷰트가 붙은 enum의 모든 값에 로컬라이즈 키가 있는지 검사한다.
/// </summary>
public static class LocalizedEnumValidator
{
    private const string k_menu = "Tools/Localization/규약 키 검증";

    [MenuItem(k_menu)]
    public static void Validate()
    {
        var problems = new List<string>();
        int checkedKeys = 0;
        int enums = 0;

        foreach (Type type in CollectAnnotatedEnums())
        {
            enums++;
            foreach (LocalizedEnumAttribute rule in type.GetCustomAttributes<LocalizedEnumAttribute>())
            {
                StringTableCollection collection = LocalizationEditorSettings.GetStringTableCollection(rule.Table);
                if (collection == null)
                {
                    problems.Add($"{type.Name}: 테이블 '{rule.Table}'을 찾을 수 없다 (접두 '{rule.KeyPrefix}')");
                    continue;
                }

                foreach (string valueName in Enum.GetNames(type))
                {
                    if (Array.IndexOf(rule.Except, valueName) >= 0)
                        continue;

                    checkedKeys++;
                    string key = rule.KeyPrefix + valueName;

                    if (collection.SharedData.GetEntry(key) == null)
                    {
                        problems.Add($"{type.Name}.{valueName}: '{rule.Table}'에 키 '{key}'가 없다");
                        continue;
                    }

                    foreach (StringTable table in collection.StringTables)
                    {
                        StringTableEntry entry = table.GetEntry(key);
                        if (entry == null || string.IsNullOrEmpty(entry.Value))
                            problems.Add($"{type.Name}.{valueName}: '{key}'의 {table.LocaleIdentifier.Code} 값이 비었다");
                    }
                }
            }
        }

        if (problems.Count == 0)
        {
            Debug.Log($"[규약 키 검증] 이상 없음 — enum {enums}개 · 키 {checkedKeys}개 확인");
            return;
        }

        var sb = new StringBuilder();
        sb.Append("[규약 키 검증] 문제 ").Append(problems.Count).Append("건 (enum ").Append(enums)
          .Append("개 · 키 ").Append(checkedKeys).Append("개 확인)");
        foreach (string p in problems)
            sb.Append('\n').Append("  · ").Append(p);

        Debug.LogError(sb.ToString());
    }

    private static IEnumerable<Type> CollectAnnotatedEnums()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
            }

            foreach (Type type in types)
            {
                if (type != null && type.IsEnum && type.IsDefined(typeof(LocalizedEnumAttribute), false))
                    yield return type;
            }
        }
    }
}
