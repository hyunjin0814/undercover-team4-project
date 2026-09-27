using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;

/// <summary>
/// 머리 스타일 외형 축을 코드에 정의한 표대로 다시 쓴다. 메뉴: Tools/머리 어휘 배선.
/// 배선 후 몽타주 레이어를 다시 구워야 한다.
/// </summary>
public static class HairVocabularyPopulator
{
    private const string k_generic = "Assets/Imported/Synty/PolygonGeneric/Prefabs/Characters/Attachments/SM_Gen_Chr_Attach_";
    private const string k_police = "Assets/Imported/Synty/PolygonPoliceStation/Prefabs/Characters/Chr_Attach/SM_Chr_Attach_";
    private const string k_apocalypse = "Assets/Imported/Synty/PolygonApocalypse/Prefabs/Characters/Attachments/SM_Chr_Attach_";

    private const string k_table = "NpcTable";

    private readonly struct Value
    {
        public readonly string Key;
        public readonly string[] Meshes;

        public Value(string key, params string[] meshes)
        {
            Key = key;
            Meshes = meshes;
        }
    }

    private const string k_bald = "Npc.Appearance.HairStyle.Bald";
    private const string k_covered = "Npc.Appearance.HairStyle.Covered";
    private const string k_short = "Npc.Appearance.HairStyle.Short";
    private const string k_veryShort = "Npc.Appearance.HairStyle.VeryShort";
    private const string k_tied = "Npc.Appearance.HairStyle.Tied";
    private const string k_bob = "Npc.Appearance.HairStyle.Bob";

    private static readonly Value[] s_values =
    {
        new Value(k_bald),

        new Value(k_bob, k_generic + "Hair_04", k_generic + "Hair_05"),
        new Value(k_bob, k_apocalypse + "Nerd_Female_Hair_01"),
        new Value(k_bob, k_apocalypse + "Waitress_Female_Hair_01"),
        new Value("Npc.Appearance.HairStyle.MidBob", k_apocalypse + "Teen_Female_Hair_01"),
        new Value("Npc.Appearance.HairStyle.Dreads", k_generic + "Hair_07"),
        new Value("Npc.Appearance.HairStyle.Shaggy", k_generic + "Hair_08"),
        new Value("Npc.Appearance.HairStyle.Asymmetric", k_apocalypse + "Emo_Female_Hair_01"),
        new Value("Npc.Appearance.HairStyle.Mohawk", k_apocalypse + "Criminal_Male_Hair_01"),
        new Value("Npc.Appearance.HairStyle.Mohawk", k_police + "Hair_08"),

        new Value(k_tied, k_generic + "Ponytail_01"),
        new Value(k_tied, k_apocalypse + "Cool_Female_Hair_01"),
        new Value(k_tied, k_apocalypse + "Islander_Male_Hair_01"),
        new Value(k_tied, k_apocalypse + "Punk_Female_Hair_01", k_apocalypse + "Zombie_Female_Hair_02"),
        new Value(k_tied, k_apocalypse + "Zombie_Female_Hair_01"),
        new Value(k_tied, k_apocalypse + "Wanderer_Male_Hair_01"),
        new Value(k_tied, k_apocalypse + "Soldier_Female_Hair_01"),
        new Value(k_tied, k_police + "Hair_02"),
        new Value(k_tied, k_police + "Hair_05"),

        new Value(k_short, k_apocalypse + "Business_Male_Hair_01"),
        new Value(k_short, k_apocalypse + "Cool_Male_Hair_01"),
        new Value(k_short, k_apocalypse + "Zombie_Male_Hair_01"),
        new Value(k_short, k_apocalypse + "Zombie_Male_Hair_02"),
        new Value(k_short, k_apocalypse + "Biker_Male_Hair_01", k_generic + "Hair_11"),
        new Value(k_short, k_generic + "Hair_06"),
        new Value(k_short, k_police + "Hair_02_Alt"),
        new Value(k_short, k_police + "Hair_06"),

        new Value(k_veryShort, k_apocalypse + "Press_Male_Hair_01"),
        new Value(k_veryShort, k_apocalypse + "RiotCop_Male_Hair_01"),
        new Value(k_veryShort, k_apocalypse + "Sheriff_Male_Hair_01", k_generic + "Hair_10"),
        new Value(k_veryShort, k_generic + "Hair_09"),
        new Value(k_veryShort, k_generic + "Hair_09_alt"),
        new Value(k_veryShort, k_police + "Hair_03"),
        new Value(k_veryShort, k_police + "Hair_07"),
        new Value(k_veryShort, k_police + "Hair_07_Alt"),

        new Value(k_covered),
    };

    [MenuItem("Tools/머리 어휘 배선")]
    private static void Populate()
    {
        AppearanceDatabase database = LoadDatabase();
        if (database == null)
            return;

        SharedTableData shared = LoadSharedTable();
        if (shared == null)
            return;

        var options = new List<AppearanceDatabase.AppearanceOption>(s_values.Length);
        var missing = new List<string>();

        foreach (Value value in s_values)
        {
            long id = shared.GetId(value.Key);
            if (id == SharedTableData.EmptyId)
                missing.Add("키 " + value.Key);

            var option = new AppearanceDatabase.AppearanceOption
            {
                DisplayName = new LocalizedString(k_table, value.Key),
                Color = Color.white,
                PropPrefabs = LoadMeshes(value.Meshes, missing),
                SciFiOnly = value.Meshes.Length == 0 && value.Key == k_covered,
            };
            options.Add(option);
        }

        if (missing.Count > 0)
        {
            Debug.LogError($"[머리 어휘] 찾지 못한 것 {missing.Count}개 — 배선 중단\n  {string.Join("\n  ", missing)}");
            return;
        }

        var serialized = new SerializedObject(database);
        SerializedProperty axis = serialized.FindProperty("m_hairStyle").FindPropertyRelative("Options");
        axis.arraySize = options.Count;
        serialized.ApplyModifiedProperties();

        AppearanceDatabase.AxisDefinition definition = database.GetAxis(AppearanceAxis.HairStyle);
        for (int i = 0; i < options.Count; i++)
            definition.Options[i] = options[i];

        EditorUtility.SetDirty(database);
        AssetDatabase.SaveAssets();

        var log = new System.Text.StringBuilder();
        for (int i = 0; i < options.Count; i++)
        {
            GameObject[] meshes = options[i].PropPrefabs;
            string names = meshes == null || meshes.Length == 0
                ? "(프롭 없음)"
                : string.Join(" + ", System.Array.ConvertAll(meshes, m => m.name));
            log.Append("\n  [").Append(i).Append("] ").Append(s_values[i].Key).Append("  ").Append(names);
        }
        Debug.Log($"[머리 어휘] 값 {options.Count}개 배선 완료 — 이제 Tools/몽타주 레이어 굽기로 레이어를 다시 구울 것{log}");
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
                missing.Add("프리팹 " + paths[i] + ".prefab");
        }
        return loaded;
    }

    private static AppearanceDatabase LoadDatabase()
    {
        string[] guids = AssetDatabase.FindAssets("t:AppearanceDatabase");
        if (guids.Length == 0)
        {
            Debug.LogError("[머리 어휘] AppearanceDatabase 에셋을 찾지 못했다");
            return null;
        }
        return AssetDatabase.LoadAssetAtPath<AppearanceDatabase>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }

    private static SharedTableData LoadSharedTable()
    {
        foreach (string guid in AssetDatabase.FindAssets("t:SharedTableData"))
        {
            var shared = AssetDatabase.LoadAssetAtPath<SharedTableData>(AssetDatabase.GUIDToAssetPath(guid));
            if (shared != null && shared.TableCollectionName == k_table)
                return shared;
        }

        Debug.LogError($"[머리 어휘] {k_table}의 SharedTableData를 찾지 못했다");
        return null;
    }
}
