using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;

/// <summary>
/// 수염 외형 축을 코드에 정의한 표대로 다시 쓴다. 메뉴: Tools/수염 어휘 배선.
/// 배선 후 몽타주 레이어를 다시 구워야 한다.
/// </summary>
public static class FacialHairVocabularyPopulator
{
    private const string k_generic = "Assets/Imported/Synty/PolygonGeneric/Prefabs/Characters/Attachments/SM_Gen_Chr_Attach_";
    private const string k_police = "Assets/Imported/Synty/PolygonPoliceStation/Prefabs/Characters/Chr_Attach/SM_Chr_Attach_";
    private const string k_apocalypse = "Assets/Imported/Synty/PolygonApocalypse/Prefabs/Characters/Attachments/SM_Chr_Attach_";

    private const string k_table = "NpcTable";

    private static readonly Color k_beardColor = new Color(0.15f, 0.12f, 0.1f, 1f);

    private readonly struct Value
    {
        public readonly string Key;
        public readonly string Ko;
        public readonly string En;
        public readonly string[] Meshes;

        public Value(string key, string ko, string en, params string[] meshes)
        {
            Key = key;
            Ko = ko;
            En = en;
            Meshes = meshes;
        }
    }

    private static readonly Value[] s_values =
    {
        new Value("Npc.Appearance.None", "없음", "None"),

        new Value(
            "Npc.Appearance.FacialHair.Mustache",
            "콧수염",
            "Moustache",
            k_generic + "Moustache_01",
            k_police + "Moustache_01",
            k_police + "Moustache_02"
        ),
        new Value(
            "Npc.Appearance.FacialHair.Beard",
            "턱수염",
            "Beard",
            k_apocalypse + "Homeless_Male_Beard_01",
            k_apocalypse + "Wanderer_Male_Beard_01"
        ),
        new Value("Npc.Appearance.FacialHair.Sideburns", "구레나룻", "Sideburns", k_generic + "Chops_01"),

        new Value(
            "Npc.Appearance.FacialHair.Goatee",
            "염소수염",
            "Goatee",
            k_apocalypse + "Criminal_Male_Beard_01",
            k_apocalypse + "Biker_Male_Beard_01"
        ),
        new Value(
            "Npc.Appearance.FacialHair.Stubble",
            "짧은 수염",
            "Stubble",
            k_apocalypse + "Hunter_Male_Beard_01",
            k_apocalypse + "Zombie_Male_Beard_01"
        ),
        new Value(
            "Npc.Appearance.FacialHair.Bushy",
            "덥수룩한 수염",
            "Bushy beard",
            k_generic + "Beard_01",
            k_generic + "Beard_02",
            k_apocalypse + "RiotCop_Male_Beard_01"
        ),
    };

    [MenuItem("Tools/수염 어휘 배선")]
    private static void Populate()
    {
        AppearanceDatabase database = LoadDatabase();
        if (database == null)
            return;

        StringTableCollection collection = LocalizationEditorSettings.GetStringTableCollection(k_table);
        if (collection == null)
        {
            Debug.LogError($"[수염 어휘] {k_table} 문자열 테이블을 찾지 못했다");
            return;
        }

        var options = new List<AppearanceDatabase.AppearanceOption>(s_values.Length);
        var missing = new List<string>();
        var created = new List<string>();

        foreach (Value value in s_values)
        {
            if (EnsureKey(collection, value))
                created.Add($"{value.Key} = {value.Ko} / {value.En}");

            options.Add(
                new AppearanceDatabase.AppearanceOption
                {
                    DisplayName = new LocalizedString(k_table, value.Key),
                    Color = value.Meshes.Length == 0 ? Color.white : k_beardColor,
                    PropPrefabs = LoadMeshes(value.Meshes, missing),
                }
            );
        }

        if (missing.Count > 0)
        {
            Debug.LogError($"[수염 어휘] 찾지 못한 프리팹 {missing.Count}개 — 배선 중단\n  {string.Join("\n  ", missing)}");
            return;
        }

        database.GetAxis(AppearanceAxis.FacialHair).Options = options.ToArray();
        EditorUtility.SetDirty(database);
        AssetDatabase.SaveAssets();

        var log = new System.Text.StringBuilder();
        for (int i = 0; i < options.Count; i++)
        {
            GameObject[] meshes = options[i].PropPrefabs;
            string names = meshes == null || meshes.Length == 0
                ? "(프롭 없음)"
                : string.Join(" + ", System.Array.ConvertAll(meshes, m => m.name));
            log.Append("\n  [").Append(i).Append("] ").Append(s_values[i].Ko).Append("  ").Append(names);
        }
        if (created.Count > 0)
            log.Append("\n  새로 판 이름 키: ").Append(string.Join(", ", created));

        Debug.Log($"[수염 어휘] 값 {options.Count}개 배선 완료 — 이제 Tools/몽타주 레이어 굽기로 레이어를 다시 구울 것{log}");
    }

    /// <summary>이름 키가 없으면 판다 — id는 테이블의 생성기가 발급해야 나중에 겹치지 않는다.</summary>
    private static bool EnsureKey(StringTableCollection collection, in Value value)
    {
        SharedTableData shared = collection.SharedData;
        if (shared.GetId(value.Key) != SharedTableData.EmptyId)
            return false;

        shared.AddKey(value.Key);
        foreach (StringTable table in collection.StringTables)
        {
            table.AddEntry(value.Key, table.LocaleIdentifier.Code.StartsWith("ko") ? value.Ko : value.En);
            EditorUtility.SetDirty(table);
        }
        EditorUtility.SetDirty(shared);
        return true;
    }

    private static GameObject[] LoadMeshes(string[] paths, List<string> missing)
    {
        if (paths.Length == 0)
            return System.Array.Empty<GameObject>();

        var loaded = new GameObject[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            loaded[i] = AssetDatabase.LoadAssetAtPath<GameObject>(paths[i] + ".prefab");
            if (loaded[i] == null)
                missing.Add(paths[i] + ".prefab");
        }
        return loaded;
    }

    private static AppearanceDatabase LoadDatabase()
    {
        string[] guids = AssetDatabase.FindAssets("t:AppearanceDatabase");
        if (guids.Length == 0)
        {
            Debug.LogError("[수염 어휘] AppearanceDatabase 에셋을 찾지 못했다");
            return null;
        }
        return AssetDatabase.LoadAssetAtPath<AppearanceDatabase>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }
}
