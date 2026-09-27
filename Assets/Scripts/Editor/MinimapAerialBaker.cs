using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 열린 맵 씬의 MinimapViewer 월드 영역을 직교 투영으로 내려찍어 PNG로 굽는다.
/// 메뉴: Tools/미니맵 항공뷰 굽기. 결과는 HQ 미니맵과 맵 선택 콘솔 미리보기에 쓴다.
/// </summary>
public class MinimapAerialBaker : EditorWindow
{
    private const string k_defaultOutput = "Assets/Imported/Art/Minimap";

    private const int k_uiLayer = 5;

    [SerializeField]
    private MinimapViewer m_viewer;

    [Tooltip("월드 1m를 몇 픽셀로 찍을지 — 해상도가 이 값으로 정해진다")]
    [SerializeField]
    private float m_pixelsPerMeter = 10f;

    [Tooltip("카메라를 띄울 높이(m) — 맵에서 제일 높은 건물보다 위여야 한다")]
    [SerializeField]
    private float m_cameraHeight = 300f;

    [SerializeField]
    private Color m_background = new Color(0.08f, 0.09f, 0.12f, 1f);

    [SerializeField]
    private string m_outputFolder = k_defaultOutput;

    [Tooltip("굽는 동안 끌 오브젝트 이름 — 쉼표로 구분. 자식까지 함께 꺼진다")]
    [SerializeField]
    private List<string> m_hidden = new List<string>();

    private Texture2D m_preview;

    [MenuItem("Tools/미니맵 항공뷰 굽기")]
    private static void Open() => GetWindow<MinimapAerialBaker>("미니맵 항공뷰");

    private void OnEnable() => TryFindViewer();

    private void OnDisable() => ClearPreview();

    private void OnGUI()
    {
        m_viewer = (MinimapViewer)
            EditorGUILayout.ObjectField("MinimapViewer", m_viewer, typeof(MinimapViewer), true);

        if (m_viewer == null)
        {
            EditorGUILayout.HelpBox(
                "맵 씬을 열고 HQ의 Minimap 오브젝트를 지정하라",
                MessageType.Info
            );
            if (GUILayout.Button("열린 씬에서 찾기"))
                TryFindViewer();
            return;
        }

        if (!TryReadArea(m_viewer, out Vector2 center, out Vector2 size))
        {
            EditorGUILayout.HelpBox(
                "worldSize가 0이다 — 씬에서 맵 크기를 배선하라",
                MessageType.Error
            );
            return;
        }

        m_pixelsPerMeter = EditorGUILayout.Slider("픽셀/미터", m_pixelsPerMeter, 1f, 40f);
        m_cameraHeight = EditorGUILayout.FloatField("카메라 높이(m)", m_cameraHeight);
        m_background = EditorGUILayout.ColorField("배경색", m_background);
        m_outputFolder = EditorGUILayout.TextField("저장 폴더", m_outputFolder);

        string hidden = EditorGUILayout.TextField("굽는 동안 끌 것", string.Join(", ", m_hidden));
        m_hidden = new List<string>(
            hidden.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries)
        );
        for (int i = 0; i < m_hidden.Count; i++)
            m_hidden[i] = m_hidden[i].Trim();

        Vector2Int resolution = ResolutionFor(size);
        EditorGUILayout.LabelField(
            $"월드 X {center.x - size.x * 0.5f:0.#} ~ {center.x + size.x * 0.5f:0.#}    "
                + $"Z {center.y - size.y * 0.5f:0.#} ~ {center.y + size.y * 0.5f:0.#}",
            EditorStyles.miniLabel
        );
        EditorGUILayout.LabelField(
            $"해상도 {resolution.x} x {resolution.y}",
            EditorStyles.miniLabel
        );

        EditorGUILayout.Space();

        if (GUILayout.Button("굽기"))
            BakePreview(center, size);

        using (new EditorGUI.DisabledScope(m_preview == null))
        {
            if (GUILayout.Button($"PNG 저장 ({SceneNameOf(m_viewer)}_Aerial.png)"))
                Save();
        }

        if (m_preview != null)
        {
            Rect rect = GUILayoutUtility.GetRect(position.width, 320f);
            GUI.DrawTexture(rect, m_preview, ScaleMode.ScaleToFit);
        }
    }

    private void TryFindViewer() =>
        m_viewer = FindFirstObjectByType<MinimapViewer>(FindObjectsInactive.Include);

    private static bool TryReadArea(MinimapViewer viewer, out Vector2 center, out Vector2 size)
    {
        if (MinimapArea.Current != null)
        {
            MinimapArea area = MinimapArea.Current;
            center = new Vector2(area.WorldCenterX, area.WorldCenterZ);
            size = new Vector2(area.WorldSizeX, area.WorldSizeZ);
            return size.x > 0.01f && size.y > 0.01f;
        }

        center = Vector2.zero;
        size = Vector2.zero;

        var serialized = new SerializedObject(viewer);
        SerializedProperty centerX = serialized.FindProperty("m_worldCenterX");
        SerializedProperty centerZ = serialized.FindProperty("m_worldCenterZ");
        SerializedProperty sizeX = serialized.FindProperty("m_worldSizeX");
        SerializedProperty sizeZ = serialized.FindProperty("m_worldSizeZ");

        if (centerX == null || centerZ == null || sizeX == null || sizeZ == null)
        {
            Debug.LogError("MinimapAerialBaker: MinimapViewer의 월드 영역 필드명이 바뀌었다");
            return false;
        }

        center = new Vector2(centerX.floatValue, centerZ.floatValue);
        size = new Vector2(sizeX.floatValue, sizeZ.floatValue);
        return size.x > 0.01f && size.y > 0.01f;
    }

    private Vector2Int ResolutionFor(Vector2 size) =>
        new Vector2Int(
            Mathf.Max(8, Mathf.RoundToInt(size.x * m_pixelsPerMeter)),
            Mathf.Max(8, Mathf.RoundToInt(size.y * m_pixelsPerMeter))
        );

    private void BakePreview(Vector2 center, Vector2 size)
    {
        ClearPreview();
        m_preview = Bake(center, size);
        Repaint();
    }

    private Texture2D Bake(Vector2 center, Vector2 size)
    {
        Vector2Int resolution = ResolutionFor(size);

        var cameraObject = new GameObject("~MinimapAerialCamera")
        {
            hideFlags = HideFlags.HideAndDontSave,
        };

        RenderTexture target = null;
        RenderTexture previous = RenderTexture.active;

        bool fogWasOn = RenderSettings.fog;
        RenderSettings.fog = false;

        List<GameObject> hidden = HideMarkers();

        try
        {
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = size.y * 0.5f;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = m_cameraHeight * 2f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = m_background;
            camera.cullingMask = ~(1 << k_uiLayer);
            camera.useOcclusionCulling = false;
            camera.enabled = false;

            cameraObject.transform.position = new Vector3(center.x, m_cameraHeight, center.y);
            cameraObject.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            target = new RenderTexture(resolution.x, resolution.y, 24, RenderTextureFormat.ARGB32);
            camera.targetTexture = target;
            camera.Render();
            camera.targetTexture = null;

            RenderTexture.active = target;
            var aerial = new Texture2D(resolution.x, resolution.y, TextureFormat.RGBA32, false);
            aerial.ReadPixels(new Rect(0f, 0f, resolution.x, resolution.y), 0, 0);
            aerial.Apply();
            return aerial;
        }
        finally
        {
            for (int i = 0; i < hidden.Count; i++)
            {
                if (hidden[i] != null)
                    hidden[i].SetActive(true);
            }

            RenderSettings.fog = fogWasOn;
            RenderTexture.active = previous;
            if (target != null)
            {
                target.Release();
                DestroyImmediate(target);
            }

            DestroyImmediate(cameraObject);
        }
    }

    private void Save()
    {
        if (!Directory.Exists(m_outputFolder))
        {
            Directory.CreateDirectory(m_outputFolder);
            AssetDatabase.Refresh();
        }

        string path = $"{m_outputFolder}/{SceneNameOf(m_viewer)}_Aerial.png";
        File.WriteAllBytes(path, m_preview.EncodeToPNG());
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        if (AssetImporter.GetAtPath(path) is TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.maxTextureSize = 4096;
            importer.SaveAndReimport();
        }

        Debug.Log($"[미니맵] 항공뷰 저장 — {path}");
        EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Sprite>(path));
    }

    private List<GameObject> HideMarkers()
    {
        var hidden = new List<GameObject>();
        if (m_hidden == null || m_hidden.Count == 0)
            return hidden;

        GameObject[] all = FindObjectsByType<GameObject>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None
        );

        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].activeSelf && m_hidden.Contains(all[i].name))
            {
                all[i].SetActive(false);
                hidden.Add(all[i]);
            }
        }

        return hidden;
    }

    private static string SceneNameOf(MinimapViewer viewer)
    {
        string name = viewer != null ? viewer.gameObject.scene.name : null;
        return string.IsNullOrEmpty(name) ? "Map" : name;
    }

    private void ClearPreview()
    {
        if (m_preview != null)
            DestroyImmediate(m_preview);

        m_preview = null;
    }
}
