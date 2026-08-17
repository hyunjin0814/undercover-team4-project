using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 몽타주 레이어 굽기 (#607) — 메뉴: Tools/몽타주 레이어 굽기
///
/// 포트레이트는 축 값마다 그림 한 장을 베이스 위에 얹어 만든다. 그 그림들을 손으로 그리는 대신
/// AppearanceDatabase에 이미 배선된 프롭을 마네킹 머리에 하나씩 붙여 정면에서 렌더해 뽑는다 —
/// 화면의 NPC와 같은 프롭을 쓰므로 몽타주가 실물과 어긋날 수 없다 (appearance-montage.md §1).
///
/// 프롭 레이어는 평면 실루엣으로 굽고 색은 런타임에 입힌다(옵션 색 / 머리색). 프롭 원본 아틀라스가
/// 어두워 그대로 구우면 곱셈 틴트가 탁해지는 문제(NpcAppearance의 중립 베이스와 같은 이유)를 피한다.
///
/// 렌더 자체는 <see cref="MontageBakeRig"/>가 맡는다. 여기 남은 것은 어느 값을 굽고 어디에 꽂느냐다.
///
/// SciFi 전용 값(가림·후드·풀헬멧·특수 안경)은 프롭이 없어 자동으로 나오지 않는다 — 손으로 채워야 하고,
/// 채우기 전까지는 AppearanceDatabase.CanDepict가 그 축을 공개 후보에서 빼 준다.
/// </summary>
public class MontageLayerBaker : EditorWindow
{
    private const float k_unknownHairCapRatio = 0.3f; // 형태 미상 머리가 정수리에서 덮는 깊이 (두상 높이 대비)

    private const int k_closePairsPerAxis = 8; // 구분 문턱에 걸린 쌍을 축마다 이만큼만 로그에 적는다

    [SerializeField]
    private AppearanceDatabase m_database;

    [SerializeField]
    private GameObject m_mannequinPrefab;

    [SerializeField]
    private Material m_flatMaterial;

    [SerializeField]
    private int m_resolution = 16;

    [SerializeField]
    private float m_orthoSize = 0.16f;

    // Synty 휴머노이드의 Head 본은 두개골 밑동에 있다 — 머리 중심은 거기서 9cm쯤 위다.
    // 낮게 잡으면 정수리가 잘리고 대신 목·어깨가 프레임에 들어온다.
    [SerializeField]
    private float m_headOffset = 0.09f;

    [SerializeField]
    private float m_cameraDistance = 1.5f;

    // 정면에서 거의 안 보이는 프롭(뒤로 넘긴 묶음머리·번)을 알리는 선이다. 자를지 말지의 기준은 아니다 —
    // 희미해도 구분은 글이 하므로 그대로 꽂는 쪽으로 정했다 (#619, docs §13-7). 여기 걸리면 사람이 보고 판단한다.
    [SerializeField]
    private float m_minLayerCoverage = 0.06f;

    // 구운 레이어끼리 얼마나 갈리는지의 선 — 같은 축의 두 값이 이보다 덜 다르면 알린다.
    // 비교 시트를 대신하는 자리다: 시트는 배선 '전' 후보를 보는 그림인데, 어휘를 그냥 다 넣기로 한 축(색으로
    // 굽는 축)에서는 배선 '후' 실제 레이어로 재는 것이 정확하다. 걸린 쌍은 ExcludeFromMontage 후보다.
    [SerializeField]
    private float m_minPairDistance = 0.12f;

    // 이미지는 전부 Imported 공유 저장소에 둔다 (2026-08-12 에셋 폴더 정리)
    [SerializeField]
    private string m_outputFolder = "Assets/Imported/Art/Montage/Layers";

    [SerializeField]
    private GameObject m_sciFiPrefab;

    [SerializeField]
    private int m_sciFiModelIndex = 13;

    [SerializeField]
    private Material[] m_sciFiMaterials;

    [Header("해상도 비교 시트 (#619)")]
    [SerializeField]
    private AppearanceAxis m_compareAxis = AppearanceAxis.HairStyle;

    [SerializeField]
    private int[] m_compareResolutions = { 16 };

    // 프레임을 위로 올려 정수리 위를 담고 목을 버리는 실험용 — 묶은 머리·번이 프레임 위로 잘려 나간다 (#619)
    [SerializeField]
    private float[] m_compareOffsets = { 0.09f };

    [SerializeField]
    private GameObject[] m_compareProps;

    // 대상 주위를 도는 각도들 — 0이 정면. 묶은 머리처럼 뒤로 넘어간 것은 정면에 안 나온다 (#619)
    [SerializeField]
    private float[] m_compareAngles = { 0f };

    [SerializeField]
    private bool m_compareLit;

    [SerializeField]
    private GameObject m_comparePairedProp;

    [SerializeField]
    private int m_compareCell = 96;

    [SerializeField]
    private Color m_compareBackground = new Color(0.5f, 0.5f, 0.52f, 1f);

    [SerializeField]
    private string m_compareFolder = "Assets/Imported/Art/Montage/Compare";

    [SerializeField]
    private Vector2 m_scroll;

    [MenuItem("Tools/몽타주 레이어 굽기")]
    private static void Open() => GetWindow<MontageLayerBaker>("몽타주 레이어");

    private void OnGUI()
    {
        m_scroll = EditorGUILayout.BeginScrollView(m_scroll);

        EditorGUILayout.HelpBox(
            "AppearanceDatabase의 프롭을 마네킹 머리에 붙여 정면 렌더로 레이어를 뽑고, 각 옵션의 MontageLayer에 바로 꽂는다.\n"
                + "마네킹 프리팹은 원하는 바디 하나만 활성인 상태여야 한다 (Generic 민머리 바디 권장).",
            MessageType.Info
        );

        m_database = (AppearanceDatabase)
            EditorGUILayout.ObjectField("외형 DB", m_database, typeof(AppearanceDatabase), false);
        m_mannequinPrefab = (GameObject)
            EditorGUILayout.ObjectField(
                "마네킹 프리팹",
                m_mannequinPrefab,
                typeof(GameObject),
                false
            );
        m_flatMaterial = (Material)
            EditorGUILayout.ObjectField(
                new GUIContent(
                    "평면 머티리얼",
                    "프롭 레이어를 실루엣으로 굽는 흰색 Unlit 머티리얼. 비우면 프롭 원본 머티리얼 그대로 굽는다"
                ),
                m_flatMaterial,
                typeof(Material),
                false
            );

        m_resolution = EditorGUILayout.IntSlider("해상도", m_resolution, 16, 256);
        m_orthoSize = EditorGUILayout.FloatField(
            new GUIContent("프레임 크기", "직교 카메라 크기 — 작을수록 머리를 크게 잡는다"),
            m_orthoSize
        );
        m_headOffset = EditorGUILayout.FloatField(
            new GUIContent("머리 오프셋", "머리 본보다 이만큼 위를 화면 중심으로 잡는다"),
            m_headOffset
        );
        m_cameraDistance = EditorGUILayout.FloatField("카메라 거리", m_cameraDistance);
        m_minLayerCoverage = EditorGUILayout.Slider(
            new GUIContent(
                "확인 문턱",
                "구운 그림이 프레임에서 이보다 적게 차지하면 로그로 알린다(꽂기는 한다). 정면에서 '없음'과 구분되지 않는지 사람이 보고 판단할 후보를 골라 주는 용도 — 구분이 안 되면 그 옵션의 ExcludeFromMontage를 켠다"
            ),
            m_minLayerCoverage,
            0f,
            0.2f
        );
        m_minPairDistance = EditorGUILayout.Slider(
            new GUIContent(
                "구분 문턱",
                "같은 축의 두 값이 이보다 덜 다르면 로그로 알린다. 본부가 그림으로 두 값을 못 가리면 그 축이 후보를 못 좁히므로, 걸린 쌍 중 하나는 ExcludeFromMontage를 켤 후보다"
            ),
            m_minPairDistance,
            0f,
            0.3f
        );
        m_outputFolder = EditorGUILayout.TextField("저장 폴더", m_outputFolder);

        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(m_database == null || m_mannequinPrefab == null))
        {
            if (GUILayout.Button("레이어 굽기", GUILayout.Height(30)))
                Bake();
        }

        DrawCompareSection();
        DrawSciFiSection();

        EditorGUILayout.EndScrollView();
    }

    /// <summary>
    /// 해상도 비교 시트 (#619) — 어휘를 몇 개까지 늘릴 수 있는지는 <b>해상도가 정한다</b>.
    /// 16px에서 머리는 10픽셀 남짓이라 단발과 스포츠머리가 같은 덩어리가 되고, 해상도를 올리면
    /// 값은 늘릴 수 있지만 뭉갬을 난이도 레버로 쓰던 것과 맞바꾼다. 그 맞바꿈을 눈으로 보고 정하는 자리다.
    /// </summary>
    private void DrawCompareSection()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("해상도 비교 시트", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "같은 프롭을 여러 해상도로 구워 한 장에 나란히 붙인다 — 가로는 프롭, 세로는 해상도(위가 낮은 쪽). DB는 건드리지 않는다.\n"
                + "왼쪽 첫 칸은 프롭 없는 맨 두상이다 (안 그린 자리가 어떻게 읽히는지의 기준).\n"
                + "후보 프롭에는 아직 어휘에 없는 것을 넣는다 — 미사용 Generic 부착물(Hair_05/06/08/09/09_alt/10/11, Bun_01, Beard_01),\n"
                + "다른 팩 프롭(PoliceStation Helmet·Goggles, Apocalypse RiotCop_Male_Helmet_01 등). 다른 팩 프롭은 이 시트가 Generic 두상 맞춤 확인도 겸한다.",
            MessageType.None
        );

        m_compareAxis = (AppearanceAxis)
            EditorGUILayout.EnumPopup(
                new GUIContent(
                    "비교할 축",
                    "이 축의 DB 프롭이 먼저 깔리고 뒤에 후보 프롭이 붙는다. 머리스타일은 실루엣으로(머리색이 칠할 자리라), 나머지는 실제 색으로 굽는다"
                ),
                m_compareAxis
            );

        var serialized = new SerializedObject(this);
        EditorGUILayout.PropertyField(
            serialized.FindProperty("m_compareResolutions"),
            new GUIContent("해상도들"),
            true
        );
        EditorGUILayout.PropertyField(
            serialized.FindProperty("m_compareOffsets"),
            new GUIContent(
                "머리 오프셋들",
                "프레임을 위로 올려 정수리 위를 담고 목을 버리는 실험용. 줄은 해상도 × 오프셋 조합만큼 생긴다"
            ),
            true
        );
        EditorGUILayout.PropertyField(
            serialized.FindProperty("m_compareAngles"),
            new GUIContent(
                "각도들",
                "대상 주위를 도는 각도(도). 0=정면, 90=옆, 180=뒤. 묶은 머리는 정면에 안 나오니 실물을 눈으로 분류할 땐 옆·뒤를 함께 볼 것"
            ),
            true
        );
        EditorGUILayout.PropertyField(
            serialized.FindProperty("m_compareProps"),
            new GUIContent("후보 프롭"),
            true
        );
        serialized.ApplyModifiedProperties();

        m_compareLit = EditorGUILayout.Toggle(
            new GUIContent(
                "실물 색으로",
                "머리스타일 축도 실루엣 대신 실제 머티리얼로 굽는다. 몽타주에 쓸 그림이 아니라 사람이 메시를 눈으로 분류하려고 볼 때 켠다"
            ),
            m_compareLit
        );

        m_comparePairedProp = (GameObject)
            EditorGUILayout.ObjectField(
                new GUIContent(
                    "같이 붙일 프롭",
                    "모든 칸에 함께 붙여 검정 가림막으로 쓴다 — 결과에는 이 프롭 밖으로 나온 부분만 남는다. 모자를 넣고 머리 축을 구우면 어느 머리가 모자를 뚫는지 보인다"
                ),
                m_comparePairedProp,
                typeof(GameObject),
                false
            );

        m_compareCell = EditorGUILayout.IntField(
            new GUIContent(
                "칸 크기",
                "시트에서 한 칸이 차지하는 픽셀. 해상도의 정수배로 확대해 넣는다 — 96이면 16/24/32가 각각 6·4·3배"
            ),
            m_compareCell
        );
        m_compareBackground = EditorGUILayout.ColorField(
            new GUIContent(
                "배경색",
                "빈 자리를 채우는 색. 검은 머리와 밝은 머리가 둘 다 보이는 중간 톤으로 둘 것"
            ),
            m_compareBackground
        );
        m_compareFolder = EditorGUILayout.TextField("시트 저장 폴더", m_compareFolder);

        using (new EditorGUI.DisabledScope(m_mannequinPrefab == null))
        {
            if (GUILayout.Button("비교 시트 굽기"))
                BakeCompareSheet();
        }
    }

    private void DrawSciFiSection()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("SciFi 전용 값 레이어", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "후드·헬멧·발광렌즈는 Generic 프롭이 없어 위 굽기로 나오지 않는다. SciFi 모델은 머리·모자가 메시에 통짜로 구워져 있어 부위만 떼어낼 수 없으므로,\n"
                + "여기서 모델 하나를 같은 프레임으로 렌더한 뒤 이미지 편집기에서 그 부위만 남기고 지운다.",
            MessageType.None
        );

        m_sciFiPrefab = (GameObject)
            EditorGUILayout.ObjectField("SciFi 프리팹", m_sciFiPrefab, typeof(GameObject), false);
        m_sciFiModelIndex = EditorGUILayout.IntField(
            new GUIContent(
                "모델 인덱스",
                "후드=13 / 헬멧=4,14 / 발광렌즈=11,16,19 (AppearanceModelCatalog 기준)"
            ),
            m_sciFiModelIndex
        );

        using (new EditorGUI.DisabledScope(m_sciFiPrefab == null))
        {
            if (GUILayout.Button("SciFi 모델 렌더"))
                RenderSciFiModel();
        }

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            "모델 시트는 20모델을 한 장에 나열한다 — 카탈로그(AppearanceModelCatalog)가 적어 둔 값이 실물과 맞는지 대조하는 용도다.\n"
                + "아틀라스를 넣으면 대신 '모델 인덱스' 하나를 그 머티리얼 수만큼 굽는다 — Alts 24종이 무엇을 흔드는지(옷인지 피부인지) 보는 용도.",
            MessageType.None
        );

        var serialized = new SerializedObject(this);
        EditorGUILayout.PropertyField(
            serialized.FindProperty("m_sciFiMaterials"),
            new GUIContent(
                "아틀라스 (선택)",
                "PolygonSciFiCity/Materials/Alts 의 머티리얼들. 비우면 모델을 나열한다"
            ),
            true
        );
        serialized.ApplyModifiedProperties();

        using (new EditorGUI.DisabledScope(m_sciFiPrefab == null))
        {
            if (GUILayout.Button("SciFi 모델 시트"))
                BakeSciFiSheet();
        }
    }

    /// <summary>
    /// SciFi 모델(또는 아틀라스 변형)을 한 장에 나열한다 (#619).
    /// 통짜 메시라 축을 떼어낼 수 없으니 카탈로그가 손으로 적은 값이 정본인데, 그 값이 실물과 맞는지
    /// 확인할 길이 지금 없다 — 모델을 한 줄로 세워 눈으로 대조하는 것이 그 길이다.
    /// </summary>
    private void BakeSciFiSheet()
    {
        using var rig = new MontageBakeRig(
            m_sciFiPrefab,
            m_flatMaterial,
            m_resolution,
            m_orthoSize,
            m_headOffset,
            m_cameraDistance
        );
        if (!rig.IsValid)
        {
            Debug.LogError("MontageLayerBaker: SciFi 모델에서 머리 본을 찾지 못했다");
            return;
        }

        bool byAtlas = m_sciFiMaterials != null && m_sciFiMaterials.Length > 0;
        int columns = byAtlas ? m_sciFiMaterials.Length : rig.BodyCount;
        if (columns == 0)
        {
            Debug.LogWarning("MontageLayerBaker: 구울 모델이 없다");
            return;
        }

        int cellSize = Mathf.Max(m_compareCell, m_resolution);
        int width = columns * cellSize;

        var sheet = new Color[width * cellSize];
        for (int i = 0; i < sheet.Length; i++)
            sheet[i] = m_compareBackground;

        if (byAtlas && !rig.SelectBody(m_sciFiModelIndex))
            return;

        var labels = new List<string>(columns);
        for (int column = 0; column < columns; column++)
        {
            if (byAtlas)
            {
                labels.Add(
                    m_sciFiMaterials[column] != null ? m_sciFiMaterials[column].name : "(빈 칸)"
                );
            }
            else
            {
                if (!rig.SelectBody(column))
                    return;
                labels.Add(column.ToString("00"));
            }

            Color[] pixels = rig.RenderSubject(byAtlas ? m_sciFiMaterials[column] : null);
            var cell = new Color[pixels.Length];
            for (int i = 0; i < cell.Length; i++)
                cell[i] = Over(m_compareBackground, pixels[i]);

            BlitCell(sheet, width, cellSize, cellSize, cell, m_resolution, column, 0);
        }

        Directory.CreateDirectory(m_compareFolder);
        AssetDatabase.Refresh();

        string fileName = byAtlas
            ? $"Montage_SciFi_Atlas_{m_sciFiModelIndex:00}"
            : "Montage_SciFi_Models";
        SavePixels(sheet, width, cellSize, m_compareFolder, fileName);
        Debug.Log(
            $"[몽타주] SciFi 시트 → {m_compareFolder}/{fileName}.png\n  가로(왼→오른): {string.Join(", ", labels)}"
        );
    }

    /// <summary>
    /// SciFi 모델 하나를 프롭 레이어와 같은 프레임으로 렌더해 PNG로 뽑는다 (#607).
    /// 통짜 메시라 부위를 코드로 떼어낼 수 없어 <b>사람이 지워서</b> 레이어를 만든다 —
    /// 그래도 화면의 그 모델을 그대로 찍은 그림이라 몽타주가 실물과 어긋나지 않는다.
    /// </summary>
    private void RenderSciFiModel()
    {
        using var rig = new MontageBakeRig(
            m_sciFiPrefab,
            m_flatMaterial,
            m_resolution,
            m_orthoSize,
            m_headOffset,
            m_cameraDistance
        );
        if (!rig.IsValid)
        {
            Debug.LogError("MontageLayerBaker: SciFi 모델에서 머리 본을 찾지 못했다");
            return;
        }
        if (!rig.SelectBody(m_sciFiModelIndex))
            return;

        Directory.CreateDirectory(m_outputFolder);
        AssetDatabase.Refresh();

        string fileName = $"Montage_SciFi_{m_sciFiModelIndex:00}";
        SavePixels(rig.RenderSubject(), m_resolution, m_resolution, m_outputFolder, fileName);
        Debug.Log(
            $"[몽타주] SciFi 모델 {m_sciFiModelIndex} 렌더 → {m_outputFolder}/{fileName}.png — 필요한 부위만 남기고 지운 뒤 해당 옵션의 MontageLayer에 꽂을 것"
        );
    }

    private void Bake()
    {
        if (m_flatMaterial == null)
        {
            Debug.LogError(
                "MontageLayerBaker: 평면 머티리얼이 없으면 실루엣을 오려낼 수 없다 — 흰색 URP/Unlit 머티리얼을 지정할 것"
            );
            return;
        }

        Directory.CreateDirectory(m_outputFolder);
        AssetDatabase.Refresh(); // 새로 만든 폴더를 인식시켜야 아래 ImportAsset이 먹는다

        using var rig = new MontageBakeRig(
            m_mannequinPrefab,
            m_flatMaterial,
            m_resolution,
            m_orthoSize,
            m_headOffset,
            m_cameraDistance
        );
        if (!rig.IsValid)
        {
            Debug.LogError(
                "MontageLayerBaker: 마네킹에서 머리 본을 찾지 못했다 — 프리팹의 휴머노이드 리그 또는 'Head' 이름 자식을 확인할 것"
            );
            return;
        }

        var baked = new List<string>();
        var excluded = new List<string>();
        var faint = new List<string>();
        var close = new List<string>();
        var groups = new List<string>();

        // ① 살 — 이목구비 레이어는 없앴다(#669) — 사람 얼굴로 안 보이는 마네킹 그대로 둔다
        Color[] basePixels = rig.RenderBase();
        Sprite baseSprite = SaveLayer(basePixels, "Montage_Base");
        if (baseSprite != null)
        {
            SetPrivateSprite("m_montageBase", baseSprite);
            baked.Add("살");
        }

        // ② 형태 미상 머리 — 머리 프롭을 쓰지 않고 두상에서 뽑는다 (BuildUnknownHair 참고)
        Sprite unknownHair = SaveLayer(BuildUnknownHair(basePixels), "Montage_HairUnknown");
        if (unknownHair != null)
        {
            SetPrivateSprite("m_montageUnknownHair", unknownHair);
            baked.Add("형태 미상 머리");
        }

        // ③ 프롭 레이어
        foreach (AppearanceAxis axis in PropAxes())
        {
            var layers = new List<(int Index, Color[] Pixels)>();
            int count = m_database.GetOptionCount(axis);
            for (int i = 0; i < count; i++)
            {
                AppearanceDatabase.AppearanceOption option = m_database.GetOption(axis, i);
                if (option?.MontageProp == null)
                    continue;

                // 몽타주에서 뺀 값은 그림을 꽂지 않는다 — 꽂으면 CanDepict가 공개 축 후보로 되살린다
                if (option.ExcludeFromMontage)
                {
                    option.MontageLayer = null; // 예전에 꽂아 둔 그림이 있으면 걷어낸다
                    excluded.Add($"{axis}[{i}] {option.MontageProp.name}");
                    continue;
                }

                Color[] pixels = rig.RenderProp(
                    option.MontageProp,
                    option.Color,
                    IsSilhouetteAxis(axis)
                );

                // 노출이 적은 프롭은 꽂되 알린다 — 자를지 말지는 사람이 굽은 그림을 보고 정할 몫이다.
                // 면적이 곧 구분 가능성은 아니라서다: 콧수염은 5%대여도 '없음'과 확실히 구분된다.
                float coverage = Coverage(pixels);
                if (coverage < m_minLayerCoverage)
                    faint.Add($"{axis}[{i}] {option.MontageProp.name} ({coverage:P1})");

                Sprite sprite = SaveLayer(pixels, $"Montage_{axis}_{i}");
                if (sprite == null)
                    continue;

                option.MontageLayer = sprite;
                layers.Add((i, pixels));
                baked.Add($"{axis}[{i}]");
            }

            CollectClosePairs(axis, layers, close);
            CollectGroups(axis, layers, groups);
        }

        EditorUtility.SetDirty(m_database);
        AssetDatabase.SaveAssets();

        Debug.Log(
            $"[몽타주] 레이어 {baked.Count}장 구움 → {m_outputFolder}\n  {string.Join(", ", baked)}"
                + (
                    excluded.Count > 0
                        ? $"\n  몽타주 제외(ExcludeFromMontage) — 공개 축 후보에서 빠진다:\n    {string.Join("\n    ", excluded)}"
                        : string.Empty
                )
                + (
                    faint.Count > 0
                        ? $"\n  ⚠ 노출이 {m_minLayerCoverage:P0} 미만이라 꽂긴 했지만 확인 요망 — '없음'과 구분되지 않으면 ExcludeFromMontage를 켤 것:\n    {string.Join("\n    ", faint)}"
                        : string.Empty
                )
                + (
                    close.Count > 0
                        ? $"\n  ⚠ 서로 {m_minPairDistance:P0} 미만으로만 갈리는 값 쌍 — 본부가 그림으로 못 가리는 쌍이다:\n    {string.Join("\n    ", close)}"
                        : string.Empty
                )
                + (
                    groups.Count > 0
                        ? $"\n  값이 실제로 몇 갈래로 갈리는가 (구분 문턱 {m_minPairDistance:P0} 기준):\n    {string.Join("\n    ", groups)}"
                        : string.Empty
                )
        );
    }

    /// <summary>
    /// 해상도별로 리그를 세워 같은 프롭을 굽고 한 장에 붙인다 (#619). DB에는 아무것도 꽂지 않는다 —
    /// 어휘 예산을 정하려고 보는 그림이라, 보는 동안 지금 어휘가 깨지면 안 된다.
    /// </summary>
    private void BakeCompareSheet()
    {
        if (m_flatMaterial == null)
        {
            Debug.LogError(
                "MontageLayerBaker: 평면 머티리얼이 없으면 실루엣을 오려낼 수 없다 — 흰색 URP/Unlit 머티리얼을 지정할 것"
            );
            return;
        }
        if (m_compareResolutions == null || m_compareResolutions.Length == 0)
        {
            Debug.LogError("MontageLayerBaker: 비교할 해상도가 없다");
            return;
        }

        var props = new List<GameObject>();
        var colors = new List<Color>();

        // 지금 어휘가 이 해상도에서 서로 구분되는지가 기준선이다 — DB 값을 먼저 깐다
        int optionCount = m_database != null ? m_database.GetOptionCount(m_compareAxis) : 0;
        for (int i = 0; i < optionCount; i++)
        {
            AppearanceDatabase.AppearanceOption option = m_database.GetOption(m_compareAxis, i);
            if (option?.MontageProp == null)
                continue;

            props.Add(option.MontageProp);
            colors.Add(option.Color);
        }

        if (m_compareProps != null)
        {
            foreach (GameObject prefab in m_compareProps)
            {
                if (prefab == null)
                    continue;

                props.Add(prefab);
                colors.Add(Color.white); // 아직 어휘에 없어 옵션 색이 없다
            }
        }

        if (props.Count == 0)
        {
            Debug.LogWarning(
                "MontageLayerBaker: 비교할 프롭이 없다 — 축을 바꾸거나 후보 프롭을 넣을 것"
            );
            return;
        }

        // 실물 색으로 켜면 실루엣을 안 쓴다 — 몽타주용 그림이 아니라 사람이 메시를 보려는 시트다
        bool silhouette = IsSilhouetteAxis(m_compareAxis) && !m_compareLit;
        Color skinColor = FirstOptionColor(
            AppearanceAxis.SkinColor,
            new Color(0.85f, 0.68f, 0.55f)
        );
        Color hairColor = FirstOptionColor(
            AppearanceAxis.HairColor,
            new Color(0.06f, 0.06f, 0.06f)
        );

        // 줄은 해상도 × 머리 오프셋 조합이다 — 둘 다 "무엇이 프레임에 담기나"를 정하는 노브라 같이 봐야 한다
        var rowSetups = new List<(int Resolution, float Offset, float Angle)>();
        foreach (int resolution in m_compareResolutions)
        {
            foreach (float offset in Offsets())
            {
                foreach (float angle in Angles())
                    rowSetups.Add((Mathf.Max(1, resolution), offset, angle));
            }
        }

        int columns = props.Count + 1; // 맨 두상 한 칸
        int rows = rowSetups.Count;

        // 칸이 원본보다 작으면 축소해야 하는데, 축소는 곧 이 시트가 판단하려는 뭉갬을 한 번 더 먹이는 것이다.
        // 그래서 칸은 가장 큰 해상도보다 작아지지 않는다.
        int cellSize = Mathf.Max(1, m_compareCell);
        foreach (var setup in rowSetups)
            cellSize = Mathf.Max(cellSize, setup.Resolution);

        int width = columns * cellSize;
        int height = rows * cellSize;

        var sheet = new Color[width * height];
        for (int i = 0; i < sheet.Length; i++)
            sheet[i] = m_compareBackground;

        for (int row = 0; row < rows; row++)
        {
            (int resolution, float offset, float angle) = rowSetups[row];
            using var rig = new MontageBakeRig(
                m_mannequinPrefab,
                m_flatMaterial,
                resolution,
                m_orthoSize,
                offset,
                m_cameraDistance,
                angle
            );
            if (!rig.IsValid)
            {
                Debug.LogError(
                    "MontageLayerBaker: 마네킹에서 머리 본을 찾지 못했다 — 프리팹의 휴머노이드 리그 또는 'Head' 이름 자식을 확인할 것"
                );
                return;
            }

            // 굽기와 같은 순서로 찍는다 (살 → 프롭) — 시트가 실물과 다르면 보고 정할 이유가 없다
            Color[] basePixels = rig.RenderBase();

            // 같이 붙일 프롭은 자기 색으로 한 번 그려 두고, 후보 프롭을 찍을 땐 검정 가림막으로 쓴다
            Color[] pairedLayer =
                m_comparePairedProp != null
                    ? rig.RenderProp(m_comparePairedProp, Color.white, false)
                    : null;

            for (int column = 0; column < columns; column++)
            {
                Color[] layer =
                    column == 0
                        ? null
                        : rig.RenderProp(
                            props[column - 1],
                            colors[column - 1],
                            silhouette,
                            m_comparePairedProp
                        );
                Color[] cell = ComposeCell(
                    basePixels,
                    pairedLayer,
                    layer,
                    skinColor,
                    silhouette ? hairColor : Color.white
                );
                BlitCell(sheet, width, height, cellSize, cell, resolution, column, row);
            }
        }

        Directory.CreateDirectory(m_compareFolder);
        AssetDatabase.Refresh();

        string fileName = $"Montage_Compare_{m_compareAxis}";
        SavePixels(sheet, width, height, m_compareFolder, fileName);

        var names = new List<string>(props.Count) { "(맨 두상)" };
        foreach (GameObject prop in props)
            names.Add(prop.name);
        var rowLabels = new List<string>(rows);
        foreach (var setup in rowSetups)
            rowLabels.Add(
                $"{setup.Resolution}px/오프셋 {setup.Offset:0.###}/각도 {setup.Angle:0}°"
            );
        Debug.Log(
            $"[몽타주] 비교 시트 → {m_compareFolder}/{fileName}.png"
                + (
                    m_comparePairedProp != null
                        ? $"  (같이 붙임: {m_comparePairedProp.name} — 이 프롭 밖으로 나온 부분만 남는다)"
                        : string.Empty
                )
                + $"\n  세로(위→아래): {string.Join(", ", rowLabels)}\n"
                + $"  가로(왼→오른): {string.Join(", ", names)}"
        );
    }

    /// <summary>포트레이트와 같은 순서로 한 칸을 합성한다 — 배경 → 살(피부색) → 같이 붙인 프롭 → 후보 프롭.</summary>
    private Color[] ComposeCell(
        Color[] basePixels,
        Color[] pairedPixels,
        Color[] layerPixels,
        Color skinColor,
        Color layerTint
    )
    {
        var cell = new Color[basePixels.Length];
        for (int i = 0; i < cell.Length; i++)
        {
            Color pixel = Over(m_compareBackground, Tint(basePixels[i], skinColor));
            if (pairedPixels != null)
                pixel = Over(pixel, pairedPixels[i]);
            if (layerPixels != null)
                pixel = Over(pixel, Tint(layerPixels[i], layerTint));
            cell[i] = pixel;
        }
        return cell;
    }

    /// <summary>비교할 각도 목록 — 비어 있으면 정면 하나.</summary>
    private IEnumerable<float> Angles()
    {
        if (m_compareAngles == null || m_compareAngles.Length == 0)
        {
            yield return 0f;
            yield break;
        }

        foreach (float angle in m_compareAngles)
            yield return angle;
    }

    /// <summary>비교할 머리 오프셋 목록 — 비어 있으면 굽기와 같은 값 하나.</summary>
    private IEnumerable<float> Offsets()
    {
        if (m_compareOffsets == null || m_compareOffsets.Length == 0)
        {
            yield return m_headOffset;
            yield break;
        }

        foreach (float offset in m_compareOffsets)
            yield return offset;
    }

    private static Color Tint(Color source, Color tint) =>
        new Color(source.r * tint.r, source.g * tint.g, source.b * tint.b, source.a * tint.a);

    private static Color Over(Color under, Color over) =>
        new Color(
            over.r * over.a + under.r * (1f - over.a),
            over.g * over.a + under.g * (1f - over.a),
            over.b * over.a + under.b * (1f - over.a),
            1f
        );

    /// <summary>한 칸을 정수배로 확대해 시트에 박는다 — 픽셀 그림이라 보간하면 판단이 흐려진다.</summary>
    private static void BlitCell(
        Color[] sheet,
        int sheetWidth,
        int sheetHeight,
        int cellSize,
        Color[] cell,
        int size,
        int column,
        int row
    )
    {
        int scale = Mathf.Max(1, cellSize / size);
        int drawn = size * scale;
        int offsetX = column * cellSize + (cellSize - drawn) / 2;
        // 픽셀 배열은 아래가 0행이라, 위에서 아래로 해상도가 커지게 하려면 뒤집어 넣는다
        int offsetY = sheetHeight - (row + 1) * cellSize + (cellSize - drawn) / 2;

        for (int y = 0; y < drawn; y++)
        {
            int sourceRow = (y / scale) * size;
            int targetRow = (offsetY + y) * sheetWidth + offsetX;
            for (int x = 0; x < drawn; x++)
                sheet[targetRow + x] = cell[sourceRow + x / scale];
        }
    }

    /// <summary>
    /// 형태 미상 머리 — 머리 스타일이 미공개일 때 까는 레이어. 머리색을 얹을 자리를 만들되 어느 스타일도
    /// 지목하지 않아야 한다. 살 실루엣에서 정수리 쪽 일부를 떼어 캡으로 쓴다.
    ///
    /// 머리 프롭을 전부 겹친 합집합으로 굽던 것을 바꾼 것이다 — 합집합은 정의상 가장 바깥 포락선이라
    /// 긴머리 외곽선을 그대로 물려받고, 미상이 장발이라는 말한 적 없는 정보를 사칭했다. 정수리 캡은
    /// 어느 스타일에나 공통이라 길이를 주장하지 않는다.
    ///
    /// 캡은 채우지 않고 <b>윤곽만</b> 남긴다 — 통짜로 칠하면 이번엔 짧은머리 하나로 읽힌다. 바깥 한 겹은
    /// 체크무늬로 솎아 윤곽까지 흐린다(미공개 색을 반투명으로 두는 것과 같은 취지 — 농도로 말한다).
    /// 윤곽의 아랫변은 뺀다: 이마를 가로지르는 띠는 모자·이마밴드로 읽혀 Headwear 축과 섞인다.
    /// </summary>
    private Color[] BuildUnknownHair(Color[] basePixels)
    {
        int size = m_resolution;
        var head = new bool[basePixels.Length];
        int top = -1;
        int bottom = -1;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (basePixels[y * size + x].a <= 0.5f)
                    continue;

                head[y * size + x] = true;
                if (bottom < 0)
                    bottom = y;
                top = y;
            }
        }

        if (top < 0)
        {
            Debug.LogWarning("MontageLayerBaker: 살 레이어가 비어 형태 미상 머리를 만들 수 없다");
            return new Color[basePixels.Length];
        }

        // 깊이는 두상 높이의 비율로 잡는다 — 해상도를 바꿔도 캡이 이마 아래로 내려오지 않는다
        int depth = Mathf.Max(2, Mathf.RoundToInt((top - bottom + 1) * k_unknownHairCapRatio));

        int capBottom = Mathf.Max(0, top - depth + 1);
        var cap = new bool[basePixels.Length];
        for (int y = capBottom; y <= top; y++)
        {
            for (int x = 0; x < size; x++)
                cap[y * size + x] = head[y * size + x];
        }

        // 굽는 크기에 비례해 부풀려야 해상도를 올려도 윤곽 두께가 같아 보인다
        bool[] grown = (bool[])cap.Clone();
        int thickness = Mathf.Max(1, Mathf.RoundToInt(size / 16f));
        for (int step = 0; step < thickness; step++)
        {
            var next = (bool[])grown.Clone();
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (!grown[y * size + x])
                        continue;

                    if (y + 1 < size)
                        next[(y + 1) * size + x] = true;
                    if (x > 0)
                        next[y * size + x - 1] = true;
                    if (x + 1 < size)
                        next[y * size + x + 1] = true;
                }
            }
            grown = next;
        }

        bool[] capEdge = EdgeOf(cap, size);
        bool[] grownEdge = EdgeOf(grown, size);

        var result = new Color[basePixels.Length];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                bool outline = capEdge[i] && y > capBottom;
                bool fuzz = grownEdge[i] && ((x + y) & 1) == 0;
                result[i] = outline || fuzz ? Color.white : Color.clear; // 머리색으로 칠할 것이므로 흰색이다
            }
        }
        return result;
    }

    private static bool[] EdgeOf(bool[] mask, int size)
    {
        var edge = new bool[mask.Length];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (!mask[y * size + x])
                    continue;

                edge[y * size + x] =
                    x == 0
                    || x + 1 == size
                    || y == 0
                    || y + 1 == size
                    || !mask[y * size + x - 1]
                    || !mask[y * size + x + 1]
                    || !mask[(y - 1) * size + x]
                    || !mask[(y + 1) * size + x];
            }
        }
        return edge;
    }

    /// <summary>
    /// 같은 축의 값끼리 재서 구분 문턱에 걸린 쌍만 가까운 순으로 모은다 (#619).
    /// '없음'은 레이어가 없어(안 그리는 것이 곧 그 값이다) 여기 안 들어온다 — 그쪽은 노출 문턱이 본다.
    /// </summary>
    private void CollectClosePairs(
        AppearanceAxis axis,
        List<(int Index, Color[] Pixels)> layers,
        List<string> close
    )
    {
        int cells = m_resolution * m_resolution;
        var found = new List<(int Cells, int A, int B)>();

        for (int a = 0; a < layers.Count; a++)
        {
            for (int b = a + 1; b < layers.Count; b++)
            {
                int different = DifferentCells(layers[a].Pixels, layers[b].Pixels);
                if (different < cells * m_minPairDistance)
                    found.Add((different, layers[a].Index, layers[b].Index));
            }
        }

        found.Sort((left, right) => left.Cells.CompareTo(right.Cells));

        // 축마다 가까운 쪽 몇 쌍만 찍는다 — 값이 늘면 쌍은 제곱으로 늘어 로그가 통째로 잘린다.
        // 잘라낸 수는 함께 적는다: 안 적으면 "이게 전부"로 읽힌다.
        int shown = Mathf.Min(k_closePairsPerAxis, found.Count);
        for (int i = 0; i < shown; i++)
        {
            (int cellCount, int a, int b) = found[i];
            close.Add(
                $"{axis}[{a}] ↔ {axis}[{b}] — {cellCount}/{cells}칸 ({(float)cellCount / cells:P1})"
            );
        }
        if (found.Count > shown)
            close.Add(
                $"{axis} — 그 밖 {found.Count - shown}쌍 더 (가까운 순으로 {shown}쌍만 적었다)"
            );
    }

    /// <summary>
    /// 값들이 실제로 몇 갈래로 갈리는지 센다 (#619) — 어휘 예산을 정하는 숫자다.
    /// <b>완전연결</b>이다: 한 갈래 안의 모든 쌍이 구분 문턱 미만이어야 합친다. 가까운 쌍 하나만 보고
    /// 사슬로 이으면 서로 멀리 떨어진 값까지 한 갈래로 빨려 들어가 실태가 과장된다.
    /// </summary>
    private void CollectGroups(
        AppearanceAxis axis,
        List<(int Index, Color[] Pixels)> layers,
        List<string> groups
    )
    {
        if (layers.Count < 2)
            return;

        int cells = m_resolution * m_resolution;
        float threshold = cells * m_minPairDistance;

        int[,] distance = new int[layers.Count, layers.Count];
        for (int a = 0; a < layers.Count; a++)
        {
            for (int b = a + 1; b < layers.Count; b++)
                distance[a, b] = distance[b, a] = DifferentCells(
                    layers[a].Pixels,
                    layers[b].Pixels
                );
        }

        var buckets = new List<List<int>>(layers.Count);
        for (int i = 0; i < layers.Count; i++)
            buckets.Add(new List<int> { i });

        // 합칠 수 있는 것 중 가장 가까운 둘을 합치기를, 합칠 게 없어질 때까지 반복한다
        while (true)
        {
            int bestWorst = int.MaxValue;
            int bestA = -1;
            int bestB = -1;

            for (int a = 0; a < buckets.Count; a++)
            {
                for (int b = a + 1; b < buckets.Count; b++)
                {
                    int worst = 0;
                    foreach (int left in buckets[a])
                    {
                        foreach (int right in buckets[b])
                            worst = Mathf.Max(worst, distance[left, right]);
                    }
                    if (worst < threshold && worst < bestWorst)
                    {
                        bestWorst = worst;
                        bestA = a;
                        bestB = b;
                    }
                }
            }

            if (bestA < 0)
                break;

            buckets[bestA].AddRange(buckets[bestB]);
            buckets.RemoveAt(bestB);
        }

        groups.Add($"{axis} — 레이어 {layers.Count}장이 {buckets.Count}갈래");
        foreach (List<int> bucket in buckets)
        {
            if (bucket.Count < 2)
                continue;

            bucket.Sort();
            string members = string.Join(
                ", ",
                bucket.ConvertAll(slot => $"[{layers[slot].Index}]")
            );
            groups.Add($"  한 갈래로 뭉친 {bucket.Count}값: {members}");
        }
    }

    /// <summary>두 레이어가 몇 칸에서 다르게 보이는가 — 있고 없음이 갈리거나, 둘 다 보이는데 색이 갈리는 칸.</summary>
    private static int DifferentCells(Color[] left, Color[] right)
    {
        const float k_visible = 0.5f; // 이 알파부터 그림으로 보인다
        const float k_colorStep = 0.1f; // 채널 하나라도 이만큼 다르면 다른 칸으로 센다

        int different = 0;
        int cells = Mathf.Min(left.Length, right.Length);
        for (int i = 0; i < cells; i++)
        {
            bool leftVisible = left[i].a > k_visible;
            bool rightVisible = right[i].a > k_visible;
            if (leftVisible != rightVisible)
            {
                different++;
                continue;
            }
            if (!leftVisible)
                continue;

            float delta = Mathf.Max(
                Mathf.Abs(left[i].r - right[i].r),
                Mathf.Abs(left[i].g - right[i].g),
                Mathf.Abs(left[i].b - right[i].b)
            );
            if (delta > k_colorStep)
                different++;
        }
        return different;
    }

    /// <summary>레이어가 프레임에서 차지하는 비율 — 정면에서 얼마나 보이는가.</summary>
    private static float Coverage(Color[] pixels)
    {
        int opaque = 0;
        foreach (Color pixel in pixels)
        {
            if (pixel.a > 0.5f)
                opaque++;
        }
        return pixels.Length > 0 ? (float)opaque / pixels.Length : 0f;
    }

    /// <summary>이 축의 프롭을 실루엣으로 굽는가 — 머리스타일만 그렇다(머리색 축이 칠할 자리라).</summary>
    private static bool IsSilhouetteAxis(AppearanceAxis axis) => axis == AppearanceAxis.HairStyle;

    private static IEnumerable<AppearanceAxis> PropAxes()
    {
        yield return AppearanceAxis.HairStyle;
        yield return AppearanceAxis.FacialHair;
        yield return AppearanceAxis.Headwear;
        yield return AppearanceAxis.Eyewear;
    }

    /// <summary>비교 시트에서 살·머리를 칠할 색 — 어휘의 첫 값을 쓴다. 값이 없으면 폴백.</summary>
    private Color FirstOptionColor(AppearanceAxis axis, Color fallback)
    {
        AppearanceDatabase.AppearanceOption option =
            m_database != null ? m_database.GetOption(axis, 0) : null;
        return option != null ? option.Color : fallback;
    }

    private Sprite SaveLayer(Color[] pixels, string fileName) =>
        SavePixels(pixels, m_resolution, m_resolution, m_outputFolder, fileName);

    private static Sprite SavePixels(
        Color[] pixels,
        int width,
        int height,
        string folder,
        string fileName
    )
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.SetPixels(pixels);
        texture.Apply();

        string path = $"{folder}/{fileName}.png";
        File.WriteAllBytes(path, texture.EncodeToPNG());
        DestroyImmediate(texture);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        ApplyImportSettings(path);
        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    // 픽셀 몽타주라 확대해도 뭉개지지 않게 Point 필터·무압축으로 둔다
    private static void ApplyImportSettings(string path)
    {
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        if (importer == null)
            return;

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.filterMode = FilterMode.Point;
        importer.mipmapEnabled = false;
        importer.alphaIsTransparency = true;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();
    }

    // 베이스·미상 머리는 옵션이 아니라 DB의 private 필드라 SerializedObject로 넣는다
    private void SetPrivateSprite(string fieldName, Sprite sprite)
    {
        var serialized = new SerializedObject(m_database);
        SerializedProperty property = serialized.FindProperty(fieldName);
        if (property == null)
        {
            Debug.LogWarning($"MontageLayerBaker: AppearanceDatabase에 {fieldName} 필드가 없다");
            return;
        }

        property.objectReferenceValue = sprite;
        serialized.ApplyModifiedProperties();
    }
}
