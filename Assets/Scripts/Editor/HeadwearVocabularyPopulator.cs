using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Tables;

/// <summary>
/// 모자 외형 축을 코드에 정의한 표대로 다시 쓴다. 메뉴: Tools/모자 어휘 배선.
/// 값 [0]~[6]은 AppearanceModelCatalog가 참조하므로 새 값은 뒤에만 붙인다.
/// </summary>
public static class HeadwearVocabularyPopulator
{
    private const string k_generic = "Assets/Imported/Synty/PolygonGeneric/Prefabs/Characters/Attachments/SM_Gen_Chr_Attach_";
    private const string k_police = "Assets/Imported/Synty/PolygonPoliceStation/Prefabs/Characters/Chr_Attach/SM_Chr_Attach_";
    private const string k_apocalypse = "Assets/Imported/Synty/PolygonApocalypse/Prefabs/Characters/Attachments/SM_Chr_Attach_";

    private const string k_table = "NpcTable";

    private readonly struct Value
    {
        public readonly string Key;
        public readonly string Ko;
        public readonly string En;
        public readonly Color Color;
        public readonly bool SciFiOnly;
        public readonly string[] Meshes;

        public Value(string key, string ko, string en, Color color, bool sciFiOnly, params string[] meshes)
        {
            Key = key;
            Ko = ko;
            En = en;
            Color = color;
            SciFiOnly = sciFiOnly;
            Meshes = meshes;
        }
    }

    private static readonly Value[] s_values =
    {
        new Value("Npc.Appearance.None", "없음", "None", Color.white, false),

        new Value(
            "Npc.Appearance.Headwear.Beanie",
            "비니",
            "Beanie",
            new Color(0.2f, 0.2f, 0.25f, 1f),
            false,
            k_generic + "Beanie_01"
        ),
        new Value(
            "Npc.Appearance.Headwear.Cap",
            "캡",
            "Cap",
            new Color(0.7f, 0.15f, 0.15f, 1f),
            false,
            k_generic + "Hat_01"
        ),
        new Value(
            "Npc.Appearance.Headwear.Hat",
            "모자",
            "Hat",
            new Color(0.3f, 0.3f, 0.35f, 1f),
            false,
            k_generic + "Hat_02"
        ),
        new Value(
            "Npc.Appearance.Headwear.Hood",
            "후드",
            "Hood",
            new Color(0.4f, 0.35f, 0.3f, 1f),
            true
        ),
        new Value(
            "Npc.Appearance.Headwear.Headphones",
            "헤드폰",
            "Headphones",
            new Color(0.15f, 0.15f, 0.15f, 1f),
            false,
            k_generic + "Headset_02",
            k_police + "Headset_01",
            k_police + "Headset_02"
        ),
        new Value(
            "Npc.Appearance.Headwear.Helmet",
            "헬멧",
            "Helmet",
            Color.white,
            false,
            k_apocalypse + "RiotCop_Male_Helmet_01"
        ),

        new Value("Npc.Appearance.Headwear.BrimHat", "챙모자", "Brim hat", Color.white, false, k_police + "Hat_05"),
        new Value("Npc.Appearance.Headwear.Beret", "베레모", "Beret", Color.white, false, k_police + "Hat_06"),
        new Value(
            "Npc.Appearance.Headwear.WideBrimHat",
            "챙 넓은 모자",
            "Wide-brim hat",
            Color.white,
            false,
            k_police + "Hat_07"
        ),

        new Value(
            "Npc.Appearance.Headwear.MotorcycleHelmet",
            "모터사이클 헬멧",
            "Motorcycle helmet",
            Color.white,
            false,
            k_police + "Helmet_02"
        ),
        new Value(
            "Npc.Appearance.Headwear.TacticalHelmet",
            "전술 헬멧",
            "Tactical helmet",
            Color.white,
            false,
            k_police + "Helmet_03"
        ),

        new Value(
            "Npc.Appearance.Headwear.CamoCap",
            "카모 캡",
            "Camo cap",
            Color.white,
            false,
            k_apocalypse + "Hunter_Male_Hat_01"
        ),
        new Value(
            "Npc.Appearance.Headwear.HuntingHat",
            "사냥 모자",
            "Hunting hat",
            Color.white,
            false,
            k_apocalypse + "Hunter_Male_Hat_02"
        ),
        new Value(
            "Npc.Appearance.Headwear.AviatorCap",
            "비행모",
            "Aviator cap",
            Color.white,
            false,
            k_apocalypse + "Scout_Female_Hat_01"
        ),
        new Value(
            "Npc.Appearance.Headwear.WideBrimHat",
            "챙 넓은 모자",
            "Wide-brim hat",
            Color.white,
            false,
            k_apocalypse + "Sheriff_Male_Hat_01"
        ),
        new Value(
            "Npc.Appearance.Headwear.Cap",
            "캡",
            "Cap",
            Color.white,
            false,
            k_apocalypse + "Teen_Male_Hat_01"
        ),
        new Value(
            "Npc.Appearance.Headwear.FootballHelmet",
            "풋볼 헬멧",
            "Football helmet",
            Color.white,
            false,
            k_apocalypse + "FootballHelmet_01"
        ),
        new Value(
            "Npc.Appearance.Headwear.SoldierHelmet",
            "군용 헬멧",
            "Soldier helmet",
            Color.white,
            false,
            k_apocalypse + "Soldier_Male_Helmet_01"
        ),
    };

    private static readonly string[] s_retiredKeys =
    {
        "Npc.Appearance.Headwear.PsHat1",
        "Npc.Appearance.Headwear.PsHat2",
        "Npc.Appearance.Headwear.PsHat3",
        "Npc.Appearance.Headwear.PsHat4",
        "Npc.Appearance.Headwear.PsHat5",
        "Npc.Appearance.Headwear.PsHat6",
        "Npc.Appearance.Headwear.PsHat7",
        "Npc.Appearance.Headwear.PsHelmet1",
        "Npc.Appearance.Headwear.PsHelmet2",
        "Npc.Appearance.Headwear.PsHelmet3",
        "Npc.Appearance.Headwear.PsHelmet4",
        "Npc.Appearance.Headwear.HunterHat1",
        "Npc.Appearance.Headwear.HunterHat2",
        "Npc.Appearance.Headwear.ScoutHat",
        "Npc.Appearance.Headwear.SheriffHat",
        "Npc.Appearance.Headwear.TeenHat",
        "Npc.Appearance.Headwear.PoliceCap",
        "Npc.Appearance.Headwear.CustodianHelmet",
        "Npc.Appearance.Headwear.PoliceLetterCap",
        "Npc.Appearance.Headwear.RiotHelmet",
    };

    [MenuItem("Tools/모자 어휘 배선")]
    private static void Populate()
    {
        AppearanceDatabase database = LoadDatabase();
        if (database == null)
            return;

        StringTableCollection collection = LocalizationEditorSettings.GetStringTableCollection(k_table);
        if (collection == null)
        {
            Debug.LogError($"[모자 어휘] {k_table} 문자열 테이블을 찾지 못했다");
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
                    Color = value.Color,
                    PropPrefabs = LoadMeshes(value.Meshes, missing),
                    SciFiOnly = value.SciFiOnly,
                }
            );
        }

        if (missing.Count > 0)
        {
            Debug.LogError($"[모자 어휘] 찾지 못한 프리팹 {missing.Count}개 — 배선 중단\n  {string.Join("\n  ", missing)}");
            return;
        }

        List<string> retired = RemoveRetiredKeys(collection);

        database.GetAxis(AppearanceAxis.Headwear).Options = options.ToArray();
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
        if (retired.Count > 0)
            log.Append("\n  걷어낸 이름 키: ").Append(string.Join(", ", retired));

        Debug.Log($"[모자 어휘] 값 {options.Count}개 배선 완료 — 이제 Tools/몽타주 레이어 굽기로 레이어를 다시 구울 것{log}");
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

    /// <summary>버린 이름 키를 테이블에서 지운다 — 이 표가 쓰는 키는 건드리지 않는다.</summary>
    private static List<string> RemoveRetiredKeys(StringTableCollection collection)
    {
        var used = new HashSet<string>();
        foreach (Value value in s_values)
            used.Add(value.Key);

        SharedTableData shared = collection.SharedData;
        var removed = new List<string>();

        foreach (string key in s_retiredKeys)
        {
            if (used.Contains(key) || shared.GetId(key) == SharedTableData.EmptyId)
                continue;

            foreach (StringTable table in collection.StringTables)
            {
                table.RemoveEntry(key);
                EditorUtility.SetDirty(table);
            }
            shared.RemoveKey(key);
            EditorUtility.SetDirty(shared);
            removed.Add(key);
        }
        return removed;
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
            Debug.LogError("[모자 어휘] AppearanceDatabase 에셋을 찾지 못했다");
            return null;
        }
        return AssetDatabase.LoadAssetAtPath<AppearanceDatabase>(AssetDatabase.GUIDToAssetPath(guids[0]));
    }
}
