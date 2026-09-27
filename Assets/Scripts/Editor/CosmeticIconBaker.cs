using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 카탈로그의 치장 아이템을 로봇 머리에 씌워 아이콘을 찍고 카탈로그에 배선한다.
/// 메뉴: Tools/치장 아이콘 굽기.
/// </summary>
public static class CosmeticIconBaker
{
    private const string k_catalogPath = "Assets/Settings/Player/AccessoryCatalog.asset";
    private const string k_modelPath = "Assets/Prefabs/UI/RobotPreview.prefab";
    private const string k_outputRoot = "Assets/Imported/Art/CosmeticIcons";
    private const int k_resolution = 128;

    [MenuItem("Tools/치장 아이콘 굽기 (#818)")]
    public static void Bake()
    {
        var catalog = AssetDatabase.LoadAssetAtPath<AccessoryCatalog>(k_catalogPath);
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(k_modelPath);
        if (catalog == null || model == null)
        {
            Debug.LogError($"[{nameof(CosmeticIconBaker)}] 카탈로그 또는 모델을 찾지 못했습니다: {k_catalogPath} / {k_modelPath}");
            return;
        }

        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        int baked = 0;

        try
        {
            foreach (EAccessorySlot slot in System.Enum.GetValues(typeof(EAccessorySlot)))
            {
                Directory.CreateDirectory(Path.Combine(projectRoot, k_outputRoot + "/" + slot));

                for (int i = 1; i < catalog.CountOf(slot); i++)
                {
                    GameObject prefab = catalog.Get(slot, i);
                    if (prefab == null)
                        continue;

                    EditorUtility.DisplayProgressBar(
                        "치장 아이콘 굽기",
                        $"{slot} {i}/{catalog.CountOf(slot) - 1} — {prefab.name}",
                        baked / 100f
                    );

                    byte[] png = Render(model, prefab);
                    File.WriteAllBytes(
                        Path.Combine(projectRoot, k_outputRoot + "/" + slot + "/" + prefab.name + ".png"),
                        png
                    );
                    baked++;
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        AssetDatabase.Refresh();
        ImportAsSprites();
        int wired = WireCatalog(catalog);
        Debug.Log($"[{nameof(CosmeticIconBaker)}] 아이콘 {baked}장을 굽고 {wired}개를 카탈로그에 물렸다 — {k_outputRoot}");
    }

    private static byte[] Render(GameObject model, GameObject accessory)
    {
        Scene scene = EditorSceneManager.NewPreviewScene();
        try
        {
            var subject = (GameObject)PrefabUtility.InstantiatePrefab(model, scene);
            Transform head = FindHead(subject.transform);
            var worn = (GameObject)PrefabUtility.InstantiatePrefab(accessory, scene);
            worn.transform.SetParent(head, false);

            var camGo = new GameObject("IconCamera");
            SceneManager.MoveGameObjectToScene(camGo, scene);
            var camera = camGo.AddComponent<Camera>();
            camera.scene = scene;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camera.orthographic = true;
            camera.orthographicSize = 0.24f;
            camera.nearClipPlane = 0.01f;
            camGo.transform.position = head.position + new Vector3(0.35f, 0.05f, 0.8f);
            camGo.transform.LookAt(head.position + new Vector3(0f, 0.03f, 0f));

            var lightGo = new GameObject("IconLight");
            SceneManager.MoveGameObjectToScene(lightGo, scene);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.4f;
            lightGo.transform.rotation = Quaternion.Euler(28f, 158f, 0f);

            var target = new RenderTexture(k_resolution, k_resolution, 24, RenderTextureFormat.ARGB32);
            camera.targetTexture = target;
            camera.Render();

            RenderTexture.active = target;
            var texture = new Texture2D(k_resolution, k_resolution, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0f, 0f, k_resolution, k_resolution), 0, 0);
            texture.Apply();
            RenderTexture.active = null;

            byte[] png = texture.EncodeToPNG();

            Object.DestroyImmediate(texture);
            camera.targetTexture = null;
            target.Release();
            Object.DestroyImmediate(target);
            return png;
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }

    private static Transform FindHead(Transform root)
    {
        Transform body = root.Find("Root");
        Transform[] bones = (body != null ? body : root).GetComponentsInChildren<Transform>(true);

        for (int i = 0; i < bones.Length; i++)
            if (bones[i].name == "Head")
                return bones[i];

        return root;
    }

    private static void ImportAsSprites()
    {
        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { k_outputRoot }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
                continue;

            if (importer.textureType == TextureImporterType.Sprite && importer.alphaIsTransparency)
                continue;

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.maxTextureSize = k_resolution;
            importer.SaveAndReimport();
        }
    }

    private static int WireCatalog(AccessoryCatalog catalog)
    {
        var serialized = new SerializedObject(catalog);
        SerializedProperty slots = serialized.FindProperty("m_slots");
        var names = (EAccessorySlot[])System.Enum.GetValues(typeof(EAccessorySlot));
        int wired = 0;

        for (int s = 0; s < slots.arraySize && s < names.Length; s++)
        {
            SerializedProperty items = slots.GetArrayElementAtIndex(s).FindPropertyRelative("Items");

            for (int i = 1; i < items.arraySize; i++)
            {
                SerializedProperty item = items.GetArrayElementAtIndex(i);
                var prefab = item.FindPropertyRelative("Prefab").objectReferenceValue;
                if (prefab == null)
                    continue;

                string path = $"{k_outputRoot}/{names[s]}/{prefab.name}.png";
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                item.FindPropertyRelative("Icon").objectReferenceValue = sprite;
                if (sprite != null)
                    wired++;
            }
        }

        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        return wired;
    }
}
