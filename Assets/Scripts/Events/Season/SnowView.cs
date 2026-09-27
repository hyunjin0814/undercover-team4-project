using UnityEngine;

/// <summary>
/// SnowEvent의 on/off를 받아 각 피어에서 먹구름과 눈 표현을 켜고 끈다.
/// </summary>
public class SnowView : MonoBehaviour
{
    [Tooltip("눈이 오는 동안 씬 태양을 이 배율로 어둡게 한다 — 먹구름 표현의 본체다")]
    [Range(0.1f, 1f)]
    [SerializeField] private float m_overcastIntensityScale = 0.6f;

    [Tooltip("어두워지고 밝아지는 데 걸리는 시간(초)")]
    [Min(0f)]
    [SerializeField] private float m_overcastFadeSeconds = 3f;

    private SnowEvent m_snowEvent;
    private bool m_overcastPushed;

    private void Start()
    {
        m_snowEvent = App.Game.SuddenEvent?.GetEvent<SnowEvent>();
        if (m_snowEvent == null)
            return;

        m_snowEvent.OnSnowChanged += HandleSnowChanged;
        HandleSnowChanged(m_snowEvent.IsSnow);
    }

    private void OnDestroy()
    {
        if (m_snowEvent != null)
            m_snowEvent.OnSnowChanged -= HandleSnowChanged;

        PopOvercast(0f);
    }

    private void PopOvercast(float fadeSeconds)
    {
        if (!m_overcastPushed)
            return;

        m_overcastPushed = false;
        WeatherOvercast.Pop(fadeSeconds);
    }

    private void HandleSnowChanged(bool isSnowing)
    {
        if (isSnowing)
            ShowSnow();
        else
            HideSnow();
    }

    private void ShowSnow()
    {
        Screen?.Show(PrecipitationScreen.EKind.Snow);

        if (!m_overcastPushed)
        {
            m_overcastPushed = true;
            WeatherOvercast.Push(m_overcastIntensityScale, m_overcastFadeSeconds);
        }
    }

    private void HideSnow()
    {
        Screen?.Hide(PrecipitationScreen.EKind.Snow);
        PopOvercast(m_overcastFadeSeconds);
    }

    private PrecipitationScreen Screen =>
        m_screen != null ? m_screen : m_screen = GetComponent<PrecipitationScreen>();

    private PrecipitationScreen m_screen;
}
