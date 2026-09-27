using UnityEngine;

/// <summary>
/// 안개 플래그를 구독해 각 피어에서 거리 안개와 파티클을 켜고 끈다.
/// 켜기 전 RenderSettings 값을 저장했다가 꺼질 때 되돌린다.
/// </summary>
public class FogView : MonoBehaviour
{
    [Header("참조 (비우면 씬에서 자동 탐색)")]
    [SerializeField]
    private FogEvent m_fog;

    [Header("거리 안개 (RenderSettings — URP 환경 안개)")]
    [Tooltip("안개 색")]
    [SerializeField]
    private Color m_fogColor = new Color(0.6f, 0.62f, 0.66f);

    [Tooltip("안개 밀도 — 높을수록 가까이서부터 뿌예진다 (ExponentialSquared 기준)")]
    [SerializeField]
    private float m_fogDensity = 0.03f;

    [Header("파티클 (선택 — 씬에 배치한 안개 FX를 연결, 비활성 상태로 둘 것)")]
    [Tooltip("안개 동안만 켜지는 파티클 오브젝트들 (예: FX_Fog). 비워도 거리 안개는 동작한다")]
    [SerializeField]
    private GameObject[] m_fogVfx;

    private bool m_savedFogEnabled;
    private Color m_savedFogColor;
    private FogMode m_savedFogMode;
    private float m_savedFogDensity;
    private bool m_saved;

    private void Start()
    {
        if (m_fog == null)
            m_fog = App.Game.SuddenEvent?.GetEvent<FogEvent>();

        if (m_fog == null)
        {
            Debug.LogWarning("FogView: FogEvent를 찾지 못해 안개 표현이 동작하지 않는다", this);
            return;
        }

        SetVfxActive(false);

        m_fog.OnFogChanged += HandleFogChanged;
        HandleFogChanged(m_fog.IsFog);
    }

    private void OnDestroy()
    {
        if (m_fog != null)
            m_fog.OnFogChanged -= HandleFogChanged;

        if (m_saved)
            RestoreFog();
    }

    private void HandleFogChanged(bool active)
    {
        if (active)
            ApplyFog();
        else if (m_saved)
            RestoreFog();

        SetVfxActive(active);
    }

    private void ApplyFog()
    {
        if (!m_saved)
        {
            m_savedFogEnabled = RenderSettings.fog;
            m_savedFogColor = RenderSettings.fogColor;
            m_savedFogMode = RenderSettings.fogMode;
            m_savedFogDensity = RenderSettings.fogDensity;
            m_saved = true;
        }

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogColor = m_fogColor;
        RenderSettings.fogDensity = m_fogDensity;
    }

    private void RestoreFog()
    {
        RenderSettings.fog = m_savedFogEnabled;
        RenderSettings.fogColor = m_savedFogColor;
        RenderSettings.fogMode = m_savedFogMode;
        RenderSettings.fogDensity = m_savedFogDensity;
        m_saved = false;
    }

    private void SetVfxActive(bool active)
    {
        if (m_fogVfx == null)
            return;

        for (int i = 0; i < m_fogVfx.Length; i++)
        {
            if (m_fogVfx[i] != null)
                m_fogVfx[i].SetActive(active);
        }
    }
}
