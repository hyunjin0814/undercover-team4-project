using UnityEngine;

/// <summary>
/// 화면 강수 마스크 — 각 픽셀이 보는 지점이 하늘에 열려 있는지를 레이로 판정해 저해상 텍스처로 굽는다.
/// 판정은 WeatherShelter.IsSheltered를 그대로 쓴다.
/// </summary>
public class PrecipitationMask : MonoBehaviour
{
    [Header("해상도")]
    [Tooltip(
        "화면을 한 변 몇 칸으로 나눌지 — 9면 레이 81발. 올리면 창틀 경계가 날카로워지지만 "
            + "레이 수가 제곱으로 는다. 셰이더가 바이리니어로 늘려 읽으므로 낮아도 부드럽다"
    )]
    [Range(3, 17)]
    [SerializeField] private int m_grid = 9;

    [Header("하늘 판정")]
    [Tooltip("이 거리(m) 안에 뭔가 있으면 막힌 것 — WeatherShelter와 같은 기준. 건물 높이보다 넉넉히")]
    [Min(0f)]
    [SerializeField] private float m_probeHeight = 25f;

    [Tooltip("하늘을 막는 것으로 칠 레이어 — 건물은 Default다")]
    [SerializeField] private LayerMask m_blockMask = 1;

    [Tooltip("픽셀이 보는 지점을 찾는 최대 거리(m) — 넘으면 하늘로 본다")]
    [Min(1f)]
    [SerializeField] private float m_viewProbeDistance = 60f;

    [Tooltip("맞은 표면에서 물러설 거리(m) — 콜라이더 안에서 쏴 실내 벽이 하늘로 잡히는 것을 막는다")]
    [Min(0.01f)]
    [SerializeField] private float m_surfaceBackoff = 0.5f;

    [Header("떨림 억제")]
    [Tooltip("칸 값이 0↔1로 옮겨 가는 속도(1/초) — 낮으면 경계가 부드럽고 높으면 반응이 빠르다")]
    [Min(0.1f)]
    [SerializeField] private float m_settleSpeed = 6f;

    [Header("진단")]
    [Tooltip("켜면 마스크 격자를 콘솔에 그린다 — 셰이더 없이 판정만 확인할 때 쓴다 (#782)")]
    [SerializeField] private bool m_logMask;

    [Tooltip("찍는 간격(초)")]
    [Min(0.1f)]
    [SerializeField] private float m_logInterval = 1f;

    private static readonly int s_maskId = Shader.PropertyToID("_PrecipMask");
    private static readonly int s_amountId = Shader.PropertyToID("_PrecipAmount");

    private Camera m_camera;
    private bool m_idle;

    private float m_nextLogAt;
    private System.Text.StringBuilder m_logBuffer;

    private Texture2D m_mask;
    private Color[] m_pixels;
    private int m_built;

    public float Amount { get; set; }

    public float OpenRatio { get; private set; }

    public Camera Camera => ResolveCamera();

    private void OnDisable()
    {
        Shader.SetGlobalFloat(s_amountId, 0f);
        m_idle = false;
    }

    private void OnDestroy()
    {
        if (m_mask != null)
            Destroy(m_mask);
        m_mask = null;
    }

    private void LateUpdate()
    {
        if (Amount <= 0f)
        {
            if (!m_idle)
            {
                m_idle = true;
                Shader.SetGlobalFloat(s_amountId, 0f);
            }
            return;
        }

        m_idle = false;

        Camera camera = ResolveCamera();
        if (camera == null)
            return;

        EnsureBuffers();
        Bake(camera);

        m_mask.SetPixels(m_pixels);
        m_mask.Apply(updateMipmaps: false);

        Shader.SetGlobalTexture(s_maskId, m_mask);
        Shader.SetGlobalFloat(s_amountId, Mathf.Clamp01(Amount));

        if (m_logMask)
            TickLog(camera);
    }

    private void TickLog(Camera camera)
    {
        if (Time.time < m_nextLogAt)
            return;

        m_nextLogAt = Time.time + m_logInterval;
        m_logBuffer ??= new System.Text.StringBuilder();
        m_logBuffer.Clear();

        bool cameraSheltered = WeatherShelter.IsSheltered(
            camera.transform.position,
            m_blockMask,
            m_probeHeight
        );

        m_logBuffer
            .Append("[강수마스크] 열림 ")
            .Append((OpenRatio * 100f).ToString("F0"))
            .Append("% · 카메라 머리 위=")
            .Append(cameraSheltered ? "막힘(실내)" : "열림(실외)")
            .Append('\n');

        for (int y = m_grid - 1; y >= 0; y--)
        {
            m_logBuffer.Append("  ");
            for (int x = 0; x < m_grid; x++)
            {
                float v = m_pixels[y * m_grid + x].r;
                m_logBuffer.Append(v > 0.75f ? '#' : v < 0.25f ? '.' : '+');
            }
            m_logBuffer.Append('\n');
        }

        Debug.Log(m_logBuffer.ToString(), this);
    }

    /// <summary>마스크를 구울 카메라를 찾는다 — 로컬 플레이어 시점 카메라 우선, Camera.main은 폴백.</summary>
    private Camera ResolveCamera()
    {
        if (m_camera != null && m_camera.isActiveAndEnabled)
            return m_camera;

        m_camera = null;

        Unity.Netcode.NetworkManager manager = Unity.Netcode.NetworkManager.Singleton;
        if (manager != null && manager.IsListening && manager.LocalClient.PlayerObject != null)
        {
            foreach (Camera camera in manager.LocalClient.PlayerObject.GetComponentsInChildren<Camera>(true))
            {
                if (!camera.isActiveAndEnabled)
                    continue;

                m_camera = camera;
                return m_camera;
            }
        }

        m_camera = Camera.main;
        return m_camera;
    }

    private void EnsureBuffers()
    {
        if (m_built == m_grid && m_mask != null)
            return;

        m_built = m_grid;
        m_pixels = new Color[m_grid * m_grid];

        if (m_mask != null)
            Destroy(m_mask);

        m_mask = new Texture2D(m_grid, m_grid, TextureFormat.R8, mipChain: false, linear: true)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "PrecipMask",
        };

        for (int i = 0; i < m_pixels.Length; i++)
            m_pixels[i] = Color.white;
    }

    private void Bake(Camera camera)
    {
        float step = 1f / (m_grid - 1);
        float lerp = Mathf.Clamp01(m_settleSpeed * Time.deltaTime);
        int openCount = 0;

        for (int y = 0; y < m_grid; y++)
        {
            for (int x = 0; x < m_grid; x++)
            {
                Ray ray = camera.ViewportPointToRay(new Vector3(x * step, y * step, 0f));
                bool open = IsSkyOpenAlong(ray);
                if (open)
                    openCount++;

                int index = y * m_grid + x;
                float settled = Mathf.MoveTowards(m_pixels[index].r, open ? 1f : 0f, lerp);
                m_pixels[index].r = settled;
            }
        }

        OpenRatio = (float)openCount / (m_grid * m_grid);
    }

    private bool IsSkyOpenAlong(Ray ray)
    {
        Vector3 probe = Physics.Raycast(
            ray,
            out RaycastHit hit,
            m_viewProbeDistance,
            m_blockMask,
            QueryTriggerInteraction.Ignore
        )
            ? hit.point - ray.direction * m_surfaceBackoff
            : ray.origin + ray.direction * m_viewProbeDistance;

        return !WeatherShelter.IsSheltered(probe, m_blockMask, m_probeHeight);
    }
}
