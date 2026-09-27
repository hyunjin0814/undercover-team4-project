using UnityEngine;

/// <summary>
/// 시점 카메라 앞 쿼드에 애디티브 셰이더로 눈·비를 그리는 화면 강수 표현.
/// PrecipitationMask가 픽셀별로 가리며, 눈·비 프리셋을 함께 둔다.
/// </summary>
[RequireComponent(typeof(PrecipitationMask))]
public class PrecipitationScreen : MonoBehaviour
{
    public enum EKind
    {
        None,
        Snow,
        Rain,
    }

    [System.Serializable]
    private struct Preset
    {
        [ColorUsage(showAlpha: false, hdr: true)]
        public Color Tint;

        [Tooltip("한 겹의 칸 수 — 크면 촘촘하다")]
        public float Cells;

        [Tooltip("낙하 속도(m/s) — 월드 속도라 가까운 겹이 화면에서 더 빨리 떨어진다. 실제 눈은 1~1.5")]
        public float Fall;

        [Tooltip("줄기 길이 — 1이면 점(눈), 크면 선(비)")]
        public float Streak;

        [Tooltip("굵기 — 크면 얇다")]
        public float Thickness;

        [Tooltip("칸이 채워질 확률")]
        public float Occupancy;

        [Tooltip("기울기(바람) — 아래로 갈수록 옆으로 밀린다")]
        public float Tilt;

        [Tooltip("좌우 흔들림 — 눈만 쓴다. 비는 0")]
        public float Drift;

        [Tooltip("겹 수 — 많으면 깊이가 생기고 비용이 는다")]
        public float Layers;

        [Tooltip("전체 진하기 — 1이면 셰이더가 낸 값 그대로")]
        [Range(0f, 1f)]
        public float Opacity;

        [Tooltip(
            "화면 중앙을 비우는 정도 — 0이면 전면에 고르게 덮는다. 전면 균일은 '세계의 날씨'가 아니라 "
                + "'렌즈에 묻은 것'처럼 보이고 크로스헤어까지 가린다"
        )]
        [Range(0f, 1f)]
        public float CenterClear;

        [Tooltip("마스크 경계 기준 — 낮으면 조금만 열려도 강수가 보이고, 높으면 확실히 열린 곳만")]
        [Range(0.1f, 0.9f)]
        public float MaskCut;

        [Tooltip("마스크 경계 부드러움 — 작으면 칼같이 끊기고, 크면 번진다(실내로 새 보인다)")]
        [Range(0.01f, 0.4f)]
        public float MaskSoft;

        [Tooltip("가장 가까운 겹이 떠 있다고 칠 거리(m) — 이보다 가까운 벽·바닥에서는 눈이 사라진다")]
        public float NearDistance;

        [Tooltip("가장 먼 겹의 거리(m) — 크면 먼 건물까지 눈이 덮인다")]
        public float FarDistance;

        [Tooltip("닿기 전에 흐려지는 폭(m) — 작으면 칼같이 끊기고, 크면 부드럽게 녹아 사라진다")]
        public float LandFade;
    }

    [Header("셰이더")]
    [Tooltip("Undercover/Weather/PrecipitationScreen — 참조로 물려야 빌드에서 안 털린다")]
    [SerializeField] private Shader m_shader;

    [Header("프리셋 — 눈")]
    [SerializeField] private Preset m_snow = new Preset
    {
        Tint = new Color(0.85f, 0.90f, 1f),
        Cells = 26f,
        Fall = 2f,
        Streak = 1.4f,
        Thickness = 16f,
        Occupancy = 0.30f,
        Tilt = 0.10f,
        Drift = 0.12f,
        Layers = 3f,
        Opacity = 0.55f,
        CenterClear = 0.55f,
        MaskCut = 0.55f,
        MaskSoft = 0.10f,
        NearDistance = 3f,
        FarDistance = 15f,
        LandFade = 2f,
    };

    [Header("프리셋 — 비")]
    [SerializeField] private Preset m_rain = new Preset
    {
        Tint = new Color(0.72f, 0.80f, 0.95f),
        Cells = 20f,
        Fall = 8f,
        Streak = 13f,
        Thickness = 28f,
        Occupancy = 0.50f,
        Tilt = 0f,
        Drift = 0f,
        Layers = 3f,
        Opacity = 0.55f,
        CenterClear = 0.5f,
        MaskCut = 0.55f,
        MaskSoft = 0.10f,
        NearDistance = 2f,
        FarDistance = 20f,
        LandFade = 1f,
    };

    [Header("페이드")]
    [Tooltip("켜지고 꺼지는 데 걸리는 시간(초) — 0이면 툭 바뀐다")]
    [Min(0f)]
    [SerializeField] private float m_fadeSeconds = 2.5f;

    [Header("배치")]
    [Tooltip("카메라 앞 거리(m) — 근평면보다 살짝 앞. 깊이를 안 쓰므로 값 자체는 그림에 영향이 없다")]
    [Min(0.01f)]
    [SerializeField] private float m_distance = 0.5f;

    private static readonly int s_tintId = Shader.PropertyToID("_Tint");
    private static readonly int s_cellsId = Shader.PropertyToID("_Cells");
    private static readonly int s_fallId = Shader.PropertyToID("_Fall");
    private static readonly int s_streakId = Shader.PropertyToID("_Streak");
    private static readonly int s_thicknessId = Shader.PropertyToID("_Thickness");
    private static readonly int s_occupancyId = Shader.PropertyToID("_Occupancy");
    private static readonly int s_tiltId = Shader.PropertyToID("_Tilt");
    private static readonly int s_driftId = Shader.PropertyToID("_Drift");
    private static readonly int s_layersId = Shader.PropertyToID("_Layers");
    private static readonly int s_opacityId = Shader.PropertyToID("_Opacity");
    private static readonly int s_centerClearId = Shader.PropertyToID("_CenterClear");
    private static readonly int s_maskCutId = Shader.PropertyToID("_MaskCut");
    private static readonly int s_maskSoftId = Shader.PropertyToID("_MaskSoft");
    private static readonly int s_fovHId = Shader.PropertyToID("_FovH");
    private static readonly int s_nearDistanceId = Shader.PropertyToID("_NearDistance");
    private static readonly int s_farDistanceId = Shader.PropertyToID("_FarDistance");
    private static readonly int s_landFadeId = Shader.PropertyToID("_LandFade");

    private PrecipitationMask m_mask;
    private Material m_material;
    private MeshRenderer m_renderer;
    private Transform m_quad;
    private Camera m_attachedTo;

    private EKind m_kind;
    private float m_target;
    private float m_current;

    public EKind Kind => m_kind;

    private void Awake() => m_mask = GetComponent<PrecipitationMask>();

    /// <summary>강수를 켠다 — 뷰(<c>SnowView</c>·<c>LightningView</c>)가 부른다. 종류가 바뀌면 값이 갈린다.</summary>
    public void Show(EKind kind)
    {
        if (kind == EKind.None)
        {
            ForceHide();
            return;
        }

        m_kind = kind;
        m_target = 1f;
        ApplyPreset();
    }

    /// <summary>자기가 켠 강수를 페이드로 끈다. 다른 종류가 그리는 중이면 무동작.</summary>
    public void Hide(EKind kind)
    {
        if (m_kind != kind)
            return;

        m_target = 0f;
    }

    private void ForceHide() => m_target = 0f;

    private void OnDisable()
    {
        m_current = 0f;
        m_target = 0f;
        m_kind = EKind.None;
        if (m_mask != null)
            m_mask.Amount = 0f;
        if (m_renderer != null)
            m_renderer.enabled = false;
    }

    private void OnDestroy()
    {
        if (m_quad != null)
            Destroy(m_quad.gameObject);
        if (m_material != null)
            Destroy(m_material);

        m_quad = null;
        m_material = null;
        m_renderer = null;
    }

    private void LateUpdate()
    {
        m_current = m_fadeSeconds <= 0f
            ? m_target
            : Mathf.MoveTowards(m_current, m_target, Time.deltaTime / m_fadeSeconds);

        m_mask.Amount = m_current;

        if (m_current <= 0.001f)
        {
            if (m_renderer != null)
                m_renderer.enabled = false;
            if (m_target <= 0f)
                m_kind = EKind.None;

            return;
        }

        EnsureQuad();
        if (m_quad == null)
            return;

        m_renderer.enabled = true;
        FitToCamera();
        ApplyViewUniforms();
    }

    private void ApplyViewUniforms()
    {
        if (m_material != null)
            m_material.SetFloat(s_fovHId, HorizontalFovRadians());
    }

    private float HorizontalFovRadians()
    {
        float halfV = Mathf.Max(m_attachedTo.fieldOfView, 1f) * 0.5f * Mathf.Deg2Rad;
        return 2f * Mathf.Atan(Mathf.Tan(halfV) * m_attachedTo.aspect);
    }

    private void EnsureQuad()
    {
        Camera camera = m_mask.Camera;
        if (camera == null)
            return;

        if (m_quad == null)
        {
            if (m_shader == null)
            {
                Debug.LogWarning($"[{nameof(PrecipitationScreen)}] 셰이더가 연결되지 않았다 (#782)", this);
                return;
            }

            m_material = new Material(m_shader) { name = "PrecipitationScreen (런타임)" };

            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "PrecipitationQuad";
            Destroy(quad.GetComponent<Collider>());

            m_renderer = quad.GetComponent<MeshRenderer>();
            m_renderer.sharedMaterial = m_material;
            m_renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_renderer.receiveShadows = false;
            m_renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;

            m_quad = quad.transform;
            ApplyPreset();
        }

        if (m_attachedTo != camera)
        {
            m_attachedTo = camera;
            m_quad.SetParent(camera.transform, worldPositionStays: false);
        }
    }

    private void FitToCamera()
    {
        Camera camera = m_attachedTo;
        float distance = Mathf.Max(m_distance, camera.nearClipPlane + 0.01f);
        float height = camera.orthographic
            ? camera.orthographicSize * 2f
            : 2f * distance * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);

        m_quad.localPosition = new Vector3(0f, 0f, distance);
        m_quad.localRotation = Quaternion.identity;
        m_quad.localScale = new Vector3(height * camera.aspect, height, 1f);
    }

    private void ApplyPreset()
    {
        if (m_material == null)
            return;

        Preset preset = m_kind == EKind.Rain ? m_rain : m_snow;
        m_material.SetColor(s_tintId, preset.Tint);
        m_material.SetFloat(s_cellsId, preset.Cells);
        m_material.SetFloat(s_fallId, preset.Fall);
        m_material.SetFloat(s_streakId, preset.Streak);
        m_material.SetFloat(s_thicknessId, preset.Thickness);
        m_material.SetFloat(s_occupancyId, preset.Occupancy);
        m_material.SetFloat(s_tiltId, preset.Tilt);
        m_material.SetFloat(s_driftId, preset.Drift);
        m_material.SetFloat(s_layersId, preset.Layers);
        m_material.SetFloat(s_opacityId, preset.Opacity);
        m_material.SetFloat(s_centerClearId, preset.CenterClear);
        m_material.SetFloat(s_maskCutId, preset.MaskCut);
        m_material.SetFloat(s_maskSoftId, preset.MaskSoft);
        m_material.SetFloat(s_nearDistanceId, preset.NearDistance);
        m_material.SetFloat(s_farDistanceId, preset.FarDistance);
        m_material.SetFloat(s_landFadeId, preset.LandFade);
    }
}
