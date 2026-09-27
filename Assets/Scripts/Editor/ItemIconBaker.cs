using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 아이템의 HeldModelPrefab(설치형은 진열 모델)을 찍어 아이콘을 굽고 해당 필드에 배선한다.
/// 메뉴: Tools/아이템 아이콘 굽기.
/// </summary>
public class ItemIconBaker : EditorWindow
{
    private const int k_resolution = 256;
    private const string k_defaultOutput = "Assets/Imported/Art/ItemIcons";
    private const string k_prefabSearchFolder = "Assets/Prefabs";

    [SerializeField] private float m_yaw = 30f;
    [SerializeField] private float m_pitch = 20f;
    [SerializeField] private float m_padding = 1.2f;
    [SerializeField] private string m_outputFolder = k_defaultOutput;

    private readonly List<BakeTarget> m_targets = new List<BakeTarget>();

    private readonly Dictionary<string, Vector2> m_angles = new Dictionary<string, Vector2>();

    private readonly Dictionary<string, Texture2D> m_preview = new Dictionary<string, Texture2D>();

    private Vector2 m_scroll;

    private abstract class BakeTarget
    {
        public abstract string Id { get; }
        public abstract string Label { get; }
        public abstract GameObject Model { get; }

        public virtual Vector3 ModelScale => Vector3.one;

        public abstract Sprite Current { get; }
        public abstract string FileName { get; }
        public abstract void Assign(Sprite sprite);
    }

    private class ItemTarget : BakeTarget
    {
        private readonly ItemBase m_item;

        public ItemTarget(ItemBase item) => m_item = item;

        public override string Id => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(m_item));
        public override string Label => m_item.name;
        public override GameObject Model => m_item.HeldModelPrefab;
        public override Sprite Current => m_item.ItemIcon;
        public override string FileName => "Item_" + m_item.name + ".png";

        public override void Assign(Sprite sprite)
        {
            var so = new SerializedObject(m_item);
            so.FindProperty("m_itemIcon").objectReferenceValue = sprite;
            so.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SavePrefabAsset(m_item.gameObject);
        }
    }

    private class InstallableTarget : BakeTarget
    {
        private readonly ShopCatalog m_catalog;
        private readonly int m_index;

        public InstallableTarget(ShopCatalog catalog, int index)
        {
            m_catalog = catalog;
            m_index = index;
        }

        private ShopCatalog.Entry Entry => m_catalog.Get(m_index);

        public override string Id =>
            AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(m_catalog)) + ":" + m_index;

        public override string Label => Entry != null ? Entry.Installable.ToString() : "(빈 항목)";
        public override GameObject Model => Entry?.DisplayModel;

        public override Vector3 ModelScale => Entry != null ? Entry.DisplayScale : Vector3.one;

        public override Sprite Current => Entry?.Icon;
        public override string FileName => "Installable_" + Label + ".png";

        public override void Assign(Sprite sprite)
        {
            var so = new SerializedObject(m_catalog);
            SerializedProperty entries = so.FindProperty("m_entries");
            if (entries == null || m_index >= entries.arraySize)
                return;

            entries.GetArrayElementAtIndex(m_index).FindPropertyRelative("m_displayIcon").objectReferenceValue = sprite;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(m_catalog);
        }
    }

    [MenuItem("Tools/아이템 아이콘 굽기")]
    private static void Open() => GetWindow<ItemIconBaker>("아이템 아이콘");

    private void OnEnable() => CollectTargets();

    private void OnDisable() => ClearPreview();

    private void CollectTargets()
    {
        m_targets.Clear();

        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { k_prefabSearchFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
                continue;

            var item = prefab.GetComponent<ItemBase>();
            if (item != null)
                m_targets.Add(new ItemTarget(item));
        }

        foreach (string guid in AssetDatabase.FindAssets("t:ShopCatalog"))
        {
            var catalog = AssetDatabase.LoadAssetAtPath<ShopCatalog>(AssetDatabase.GUIDToAssetPath(guid));
            if (catalog == null)
                continue;

            for (int i = 0; i < catalog.Count; i++)
            {
                ShopCatalog.Entry entry = catalog.Get(i);
                if (entry != null && entry.IsInstallable)
                    m_targets.Add(new InstallableTarget(catalog, i));
            }
        }

        m_targets.Sort((a, b) => string.CompareOrdinal(a.Label, b.Label));

        foreach (BakeTarget target in m_targets)
        {
            string saved = EditorPrefs.GetString(AngleKey(target), string.Empty);
            string[] parts = saved.Split(';');
            if (parts.Length == 2
                && float.TryParse(parts[0], out float yaw)
                && float.TryParse(parts[1], out float pitch))
            {
                m_angles[target.Id] = new Vector2(yaw, pitch);
            }
        }
    }

    private void OnGUI()
    {
        m_yaw = EditorGUILayout.Slider("가로 회전(도)", m_yaw, -180f, 180f);
        m_pitch = EditorGUILayout.Slider("내려다보는 각(도)", m_pitch, -89f, 89f);
        m_padding = EditorGUILayout.Slider("여백 배율", m_padding, 1f, 2f);
        m_outputFolder = EditorGUILayout.TextField("저장 폴더", m_outputFolder);

        EditorGUILayout.Space();

        if (GUILayout.Button("목록 새로고침"))
            CollectTargets();

        if (GUILayout.Button("전부 미리 굽기"))
            BakeAll();

        using (new EditorGUI.DisabledScope(m_preview.Count == 0))
        {
            if (GUILayout.Button($"미리 구운 {m_preview.Count}장 저장 + 배선"))
                SaveAll();
        }

        EditorGUILayout.Space();
        DrawList();
    }

    private void DrawList()
    {
        m_scroll = EditorGUILayout.BeginScrollView(m_scroll);

        foreach (BakeTarget target in m_targets)
        {
            EditorGUILayout.BeginHorizontal("box");

            DrawThumb(target.Current != null ? target.Current.texture : null, "현재");
            DrawThumb(m_preview.TryGetValue(target.Id, out Texture2D baked) ? baked : null, "구운 것");

            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(target.Label, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                target.Model != null ? target.Model.name : "모델 없음 — 굽지 않는다",
                EditorStyles.miniLabel
            );

            Vector2 angle = ResolveAngle(target);
            float yaw = EditorGUILayout.Slider("가로 회전", angle.x, -180f, 180f);
            float pitch = EditorGUILayout.Slider("내려다보는 각", angle.y, -89f, 89f);

            if (!Mathf.Approximately(yaw, angle.x) || !Mathf.Approximately(pitch, angle.y))
                SetAngle(target, new Vector2(yaw, pitch));

            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(target.Model == null))
            {
                if (GUILayout.Button("이것만 다시 굽기"))
                    BakeOne(target);
            }

            using (new EditorGUI.DisabledScope(!m_angles.ContainsKey(target.Id)))
            {
                if (GUILayout.Button("기본 각도로"))
                {
                    m_angles.Remove(target.Id);
                    EditorPrefs.DeleteKey(AngleKey(target));
                }
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
    }

    private static string AngleKey(BakeTarget target) => "ItemIconBaker.angle." + target.Id;

    /// <summary>이 대상에 맞춰 둔 각도. 없으면 창의 기본값.</summary>
    private Vector2 ResolveAngle(BakeTarget target) =>
        m_angles.TryGetValue(target.Id, out Vector2 angle) ? angle : new Vector2(m_yaw, m_pitch);

    private void SetAngle(BakeTarget target, Vector2 angle)
    {
        m_angles[target.Id] = angle;
        EditorPrefs.SetString(AngleKey(target), angle.x + ";" + angle.y);
    }

    private static void DrawThumb(Texture texture, string caption)
    {
        EditorGUILayout.BeginVertical(GUILayout.Width(72f));
        Rect rect = GUILayoutUtility.GetRect(64f, 64f, GUILayout.ExpandWidth(false));
        if (texture != null)
            GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit);
        EditorGUILayout.LabelField(caption, EditorStyles.miniLabel);
        EditorGUILayout.EndVertical();
    }

    private void BakeAll()
    {
        ClearPreview();

        try
        {
            for (int i = 0; i < m_targets.Count; i++)
            {
                BakeTarget target = m_targets[i];
                if (target.Model == null)
                    continue;

                EditorUtility.DisplayProgressBar("아이템 아이콘", target.Label, (float)i / m_targets.Count);
                BakeOne(target);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private void BakeOne(BakeTarget target)
    {
        Vector2 angle = ResolveAngle(target);

        Texture2D icon = RenderModel(target.Model, angle.x, angle.y, target.ModelScale);
        if (icon == null)
            return;

        if (m_preview.TryGetValue(target.Id, out Texture2D old) && old != null)
            DestroyImmediate(old);

        m_preview[target.Id] = icon;
        Repaint();
    }

    private Texture2D RenderModel(GameObject modelPrefab, float yaw, float pitch, Vector3 scale)
    {
        var root = new GameObject("~ItemIconBake") { hideFlags = HideFlags.HideAndDontSave };
        root.transform.position = new Vector3(0f, -10000f, 0f);

        RenderTexture target = null;
        RenderTexture previous = RenderTexture.active;

        try
        {
            var subject = (GameObject)PrefabUtility.InstantiatePrefab(modelPrefab, root.transform);
            subject.transform.localPosition = Vector3.zero;
            subject.transform.localRotation = Quaternion.Euler(pitch, yaw, 0f);
            subject.transform.localScale = scale;

            if (!TryGetBounds(subject, out Bounds bounds))
            {
                Debug.LogError($"ItemIconBaker: {modelPrefab.name}에 렌더러가 없다");
                return null;
            }

            var lightObject = new GameObject("~BakeLight");
            lightObject.transform.SetParent(root.transform, false);
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;

            var cameraObject = new GameObject("~BakeCamera");
            cameraObject.transform.SetParent(root.transform, false);

            Camera camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = Mathf.Max(bounds.extents.x, bounds.extents.y) * m_padding;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.cullingMask = ~0;
            camera.enabled = false;

            cameraObject.transform.position = bounds.center + Vector3.back * 10f;
            cameraObject.transform.rotation = Quaternion.identity;

            target = new RenderTexture(k_resolution, k_resolution, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };

            Texture2D onWhite = Capture(camera, target, Color.white);
            Texture2D onBlack = Capture(camera, target, Color.black);

            Color[] white = onWhite.GetPixels();
            Color[] black = onBlack.GetPixels();
            var pixels = new Color[white.Length];

            for (int i = 0; i < pixels.Length; i++)
            {
                float alpha = 1f - ((white[i].r - black[i].r) + (white[i].g - black[i].g) + (white[i].b - black[i].b)) / 3f;
                pixels[i] = alpha <= 0.004f
                    ? Color.clear
                    : new Color(black[i].r / alpha, black[i].g / alpha, black[i].b / alpha, Mathf.Clamp01(alpha));
            }

            DestroyImmediate(onWhite);
            DestroyImmediate(onBlack);

            var icon = new Texture2D(k_resolution, k_resolution, TextureFormat.RGBA32, false);
            icon.SetPixels(pixels);
            icon.Apply();
            return icon;
        }
        finally
        {
            RenderTexture.active = previous;

            if (target != null)
            {
                target.Release();
                DestroyImmediate(target);
            }

            DestroyImmediate(root);
        }
    }

    private static Texture2D Capture(Camera camera, RenderTexture target, Color background)
    {
        camera.backgroundColor = background;
        camera.targetTexture = target;
        camera.Render();
        camera.targetTexture = null;

        RenderTexture.active = target;
        var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
        texture.ReadPixels(new Rect(0f, 0f, target.width, target.height), 0, 0);
        texture.Apply();
        return texture;
    }

    private static bool TryGetBounds(GameObject subject, out Bounds bounds)
    {
        bounds = default;
        bool found = false;

        foreach (Renderer renderer in subject.GetComponentsInChildren<Renderer>(false))
        {
            if (renderer == null || renderer is ParticleSystemRenderer)
                continue;

            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
                continue;
            }

            bounds.Encapsulate(renderer.bounds);
        }

        return found;
    }

    private void SaveAll()
    {
        if (!Directory.Exists(m_outputFolder))
        {
            Debug.LogError($"ItemIconBaker: 저장 폴더가 없다 — {m_outputFolder}");
            return;
        }

        int written = 0;

        foreach (BakeTarget target in m_targets)
        {
            if (!m_preview.TryGetValue(target.Id, out Texture2D baked) || baked == null)
                continue;

            string path = $"{m_outputFolder}/{target.FileName}";

            File.WriteAllBytes(path, baked.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            ApplySpriteImport(path);

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
            {
                Debug.LogError($"ItemIconBaker: {path}에서 스프라이트를 못 읽어 {target.Label} 배선을 건너뛴다");
                continue;
            }

            target.Assign(sprite);
            written++;
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[아이템] 아이콘 {written}장을 굽고 배선했다");
    }

    private static void ApplySpriteImport(string path)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
            return;

        if (importer.textureType == TextureImporterType.Sprite
            && importer.spriteImportMode == SpriteImportMode.Single)
            return;

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.SaveAndReimport();
    }

    private void ClearPreview()
    {
        foreach (Texture2D texture in m_preview.Values)
        {
            if (texture != null)
                DestroyImmediate(texture);
        }

        m_preview.Clear();
    }
}
