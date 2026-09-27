using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 감정표현 클립의 대표 프레임을 전신 렌더로 찍어 아이콘을 굽는다.
/// 메뉴: Tools/감정표현 아이콘 굽기.
/// </summary>
public class EmoteIconBaker : EditorWindow
{
    private const int k_resolution = 256;
    private const float k_ringOuter = 124f;
    private const float k_ringInner = 114f;

    private const string k_defaultOutput = "Assets/Imported/Art/EmoteIcons";

    [SerializeField] private EmoteCatalog m_catalog;
    [SerializeField] private GameObject m_subjectPrefab;

    [Tooltip("대표 프레임 = 클립 길이 × 이 비율")]
    [SerializeField] private float m_frameRatio = 0.5f;

    [Tooltip("대상을 이만큼 돌린 뒤 찍는다 — 정면이 안 나오면 조정")]
    [SerializeField] private float m_yaw;

    [Tooltip("전신 프레임 여백 배율")]
    [SerializeField] private float m_padding = 1.15f;

    [SerializeField] private string m_outputFolder = k_defaultOutput;

    [SerializeField]
    private List<string> m_excluded = new List<string> { "Camera", "Corpse", "NameTag", "Canvas" };

    private readonly Dictionary<int, float> m_ratioOverrides = new Dictionary<int, float>();

    private readonly Dictionary<int, Texture2D> m_preview = new Dictionary<int, Texture2D>();

    private Vector2 m_scroll;

    [MenuItem("Tools/감정표현 아이콘 굽기")]
    private static void Open() => GetWindow<EmoteIconBaker>("감정표현 아이콘");

    private void OnEnable()
    {
        if (m_catalog == null)
            m_catalog = AssetDatabase.LoadAssetAtPath<EmoteCatalog>("Assets/Settings/Emote/EmoteCatalog.asset");

        if (m_subjectPrefab == null)
            m_subjectPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
    }

    private void OnDisable() => ClearPreview();

    private void OnGUI()
    {
        m_catalog = (EmoteCatalog)EditorGUILayout.ObjectField("카탈로그", m_catalog, typeof(EmoteCatalog), false);
        m_subjectPrefab = (GameObject)
            EditorGUILayout.ObjectField("찍을 프리팹", m_subjectPrefab, typeof(GameObject), false);

        m_frameRatio = EditorGUILayout.Slider("대표 프레임 비율", m_frameRatio, 0f, 1f);
        m_yaw = EditorGUILayout.Slider("대상 회전(도)", m_yaw, -180f, 180f);
        m_padding = EditorGUILayout.Slider("여백 배율", m_padding, 1f, 2f);
        m_outputFolder = EditorGUILayout.TextField("저장 폴더", m_outputFolder);

        string excluded = EditorGUILayout.TextField("제외할 하위 오브젝트", string.Join(", ", m_excluded));
        m_excluded = new List<string>(excluded.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries));
        for (int i = 0; i < m_excluded.Count; i++)
            m_excluded[i] = m_excluded[i].Trim();

        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(m_catalog == null || m_subjectPrefab == null))
        {
            if (GUILayout.Button("전부 미리 굽기"))
                BakeAll();

            using (new EditorGUI.DisabledScope(m_preview.Count == 0))
            {
                if (GUILayout.Button($"미리 구운 {m_preview.Count}장 저장 (기존 PNG 덮어씀)"))
                    SaveAll();
            }
        }

        if (m_catalog == null)
        {
            EditorGUILayout.HelpBox("카탈로그를 지정하라", MessageType.Info);
            return;
        }

        EditorGUILayout.Space();
        DrawList();
    }

    private void DrawList()
    {
        m_scroll = EditorGUILayout.BeginScrollView(m_scroll);

        for (int i = 0; i < m_catalog.Count; i++)
        {
            EmoteDefinition definition = m_catalog.Get(i);
            if (definition == null)
                continue;

            EditorGUILayout.BeginHorizontal("box");

            DrawThumb(definition.Icon != null ? definition.Icon.texture : null, "현재");
            DrawThumb(m_preview.TryGetValue(i, out Texture2D baked) ? baked : null, "구운 것");

            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField($"{i:00}  {definition.Id}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                definition.Clip != null ? definition.Clip.name : "클립 없음 — 굽지 않는다",
                EditorStyles.miniLabel
            );

            float ratio = m_ratioOverrides.TryGetValue(i, out float over) ? over : m_frameRatio;
            float edited = EditorGUILayout.Slider("프레임", ratio, 0f, 1f);
            if (!Mathf.Approximately(edited, ratio))
                m_ratioOverrides[i] = edited;

            using (new EditorGUI.DisabledScope(definition.Clip == null || m_subjectPrefab == null))
            {
                if (GUILayout.Button("이것만 다시 굽기"))
                    BakeOne(i, definition);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
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
            for (int i = 0; i < m_catalog.Count; i++)
            {
                EmoteDefinition definition = m_catalog.Get(i);
                if (definition == null || definition.Clip == null)
                    continue;

                EditorUtility.DisplayProgressBar("감정표현 아이콘", definition.Id, (float)i / m_catalog.Count);
                BakeOne(i, definition);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private void BakeOne(int index, EmoteDefinition definition)
    {
        float ratio = m_ratioOverrides.TryGetValue(index, out float over) ? over : m_frameRatio;

        SampleBackground(definition, out Color fill, out Color ring);

        Texture2D icon = RenderPose(definition.Clip, ratio, fill);
        if (icon == null)
            return;

        MaskCircle(icon, ring);

        if (m_preview.TryGetValue(index, out Texture2D old) && old != null)
            DestroyImmediate(old);

        m_preview[index] = icon;
        Repaint();
    }

    private Texture2D RenderPose(AnimationClip clip, float ratio, Color background)
    {
        var root = new GameObject("~EmoteIconBake") { hideFlags = HideFlags.HideAndDontSave };
        root.transform.position = new Vector3(0f, -10000f, 0f);

        RenderTexture target = null;
        RenderTexture previous = RenderTexture.active;

        try
        {
            var subject = (GameObject)PrefabUtility.InstantiatePrefab(m_subjectPrefab, root.transform);
            subject.transform.localPosition = Vector3.zero;
            subject.transform.localRotation = Quaternion.Euler(0f, m_yaw, 0f);

            HideExcluded(subject);

            GameObject sampleTarget = ResolveAnimatorObject(subject);
            if (sampleTarget == null)
            {
                Debug.LogError($"EmoteIconBaker: {m_subjectPrefab.name}에 Animator가 없어 자세를 잡을 수 없다");
                return null;
            }

            AnimationMode.StartAnimationMode();
            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(sampleTarget, clip, Mathf.Clamp01(ratio) * clip.length);
            AnimationMode.EndSampling();

            if (!TryGetBounds(subject, out Bounds bounds))
            {
                Debug.LogError($"EmoteIconBaker: {m_subjectPrefab.name}에 렌더러가 없다");
                return null;
            }

            var lightObject = new GameObject("~BakeLight");
            lightObject.transform.SetParent(root.transform, false);
            lightObject.transform.rotation = Quaternion.LookRotation(
                -subject.transform.forward + Vector3.down * 0.35f
            );

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
            camera.backgroundColor = background;
            camera.cullingMask = ~0;
            camera.enabled = false;

            cameraObject.transform.position = bounds.center + subject.transform.forward * 10f;
            cameraObject.transform.rotation = Quaternion.LookRotation(-subject.transform.forward, Vector3.up);

            target = new RenderTexture(k_resolution, k_resolution, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };

            camera.targetTexture = target;
            camera.Render();
            camera.targetTexture = null;

            RenderTexture.active = target;
            var pose = new Texture2D(k_resolution, k_resolution, TextureFormat.RGBA32, false);
            pose.ReadPixels(new Rect(0f, 0f, k_resolution, k_resolution), 0, 0);
            pose.Apply();
            return pose;
        }
        finally
        {
            if (AnimationMode.InAnimationMode())
                AnimationMode.StopAnimationMode();

            RenderTexture.active = previous;
            if (target != null)
            {
                target.Release();
                DestroyImmediate(target);
            }

            DestroyImmediate(root);
        }
    }

    private void HideExcluded(GameObject subject)
    {
        Transform[] all = subject.GetComponentsInChildren<Transform>(true);

        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == null || all[i] == subject.transform)
                continue;

            if (m_excluded.Contains(all[i].name))
                all[i].gameObject.SetActive(false);
        }
    }

    private static GameObject ResolveAnimatorObject(GameObject subject)
    {
        Animator animator = subject.GetComponentInChildren<Animator>(true);
        return animator != null ? animator.gameObject : null;
    }

    private static bool TryGetBounds(GameObject subject, out Bounds bounds)
    {
        bounds = default;

        Renderer[] renderers = subject.GetComponentsInChildren<Renderer>(false);
        var baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
        bool found = false;

        try
        {
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null || renderers[i] is ParticleSystemRenderer)
                    continue;

                Bounds world;

                if (renderers[i] is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                {
                    skinned.BakeMesh(baked, true);
                    world = TransformBounds(skinned.transform, baked.bounds);
                }
                else
                {
                    world = renderers[i].bounds;
                }

                if (!found)
                {
                    bounds = world;
                    found = true;
                    continue;
                }

                bounds.Encapsulate(world);
            }
        }
        finally
        {
            DestroyImmediate(baked);
        }

        return found;
    }

    private static Bounds TransformBounds(Transform transform, Bounds local)
    {
        Matrix4x4 matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
        var result = new Bounds(matrix.MultiplyPoint3x4(local.center), Vector3.zero);

        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? local.min.x : local.max.x,
                (i & 2) == 0 ? local.min.y : local.max.y,
                (i & 4) == 0 ? local.min.z : local.max.z
            );

            result.Encapsulate(matrix.MultiplyPoint3x4(corner));
        }

        return result;
    }

    private static void SampleBackground(EmoteDefinition definition, out Color fill, out Color ring)
    {
        fill = new Color32(43, 46, 79, 255);
        ring = new Color32(122, 152, 240, 255);

        if (definition.Icon == null)
            return;

        string path = AssetDatabase.GetAssetPath(definition.Icon);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            if (!source.LoadImage(File.ReadAllBytes(path)) || source.width < k_resolution)
                return;

            float scale = source.height / (float)k_resolution;
            ring = SampleRingMode(source, (k_ringInner + k_ringOuter) * 0.5f * scale);
            fill = SampleRingMode(source, (k_ringInner - 14f) * scale);
        }
        finally
        {
            DestroyImmediate(source);
        }
    }

    private static Color SampleRingMode(Texture2D source, float radius)
    {
        const int k_samples = 24;

        var counts = new Dictionary<Color32, int>();
        float centerX = (source.width - 1) * 0.5f;
        float centerY = (source.height - 1) * 0.5f;
        Color32 best = default;
        int bestCount = 0;

        for (int i = 0; i < k_samples; i++)
        {
            float angle = i * Mathf.PI * 2f / k_samples;
            int x = Mathf.RoundToInt(centerX + Mathf.Cos(angle) * radius);
            int y = Mathf.RoundToInt(centerY + Mathf.Sin(angle) * radius);

            Color32 sample = source.GetPixel(x, y);
            counts.TryGetValue(sample, out int count);
            counts[sample] = ++count;

            if (count > bestCount)
            {
                best = sample;
                bestCount = count;
            }
        }

        return best;
    }

    private static void MaskCircle(Texture2D icon, Color ring)
    {
        Color[] pixels = icon.GetPixels();
        float center = (k_resolution - 1) * 0.5f;

        for (int y = 0; y < k_resolution; y++)
        {
            for (int x = 0; x < k_resolution; x++)
            {
                int i = y * k_resolution + x;
                float distance = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center));

                if (distance >= k_ringInner)
                    pixels[i] = ring;

                float alpha = Mathf.Clamp01(k_ringOuter - distance);
                pixels[i].a = alpha;
            }
        }

        icon.SetPixels(pixels);
        icon.Apply();
    }

    private void SaveAll()
    {
        if (!Directory.Exists(m_outputFolder))
        {
            Debug.LogError($"EmoteIconBaker: 저장 폴더가 없다 — {m_outputFolder}");
            return;
        }

        int written = 0;

        foreach (KeyValuePair<int, Texture2D> entry in m_preview)
        {
            EmoteDefinition definition = m_catalog.Get(entry.Key);
            if (definition == null || entry.Value == null)
                continue;

            string path =
                definition.Icon != null
                    ? AssetDatabase.GetAssetPath(definition.Icon)
                    : $"{m_outputFolder}/Emote_{definition.Id}.png";

            File.WriteAllBytes(path, entry.Value.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            written++;
        }

        Debug.Log($"[감정표현] 아이콘 {written}장을 다시 구웠다");
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
