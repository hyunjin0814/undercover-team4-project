using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 비·번개 표현 계층 — LightningEvent의 on/off와 OnStrike를 받아 먹구름·섬광·낙뢰 FX를 낸다.
/// </summary>
public class LightningView : MonoBehaviour
{
    [Header("FX 프리팹")]
    [Tooltip("낙뢰 지점에 터지는 파티클")]
    [SerializeField] private GameObject m_strikeParticlePrefab;

    [Tooltip(
        "떨어질 자리에 미리 띄우는 예고 파티클 (#647) — 벼락이 떨어질 때 치운다.\n\n"
            + "비워 두면 예고 연출이 없다. 판정은 그대로 예고 시간 뒤에 나므로 회피는 성립한다"
    )]
    [SerializeField] private GameObject m_warningPrefab;

    [Header("먹구름 — 어두워지기")]
    [Tooltip(
        "비가 오는 동안 어둡게 할 라이트. <b>비워 두는 것이 기본이다</b> — 비어 있으면 씬의 태양"
            + "(Lighting 창의 Sun Source = RenderSettings.sun)을 런타임에 찾아 쓴다.\n\n"
            + "이 컴포넌트는 프리팹(SuddenEvents)에 붙으므로 씬 오브젝트를 직렬화로 물릴 수 없다 — "
            + "씬마다 다른 라이트를 가리켜야 하는데 프리팹은 씬을 모른다. 특정 라이트를 강제하고 싶을 때만 채운다"
    )]
    [SerializeField] private Light m_globalLight;

    [Tooltip("먹구름이 낀 동안의 밝기 배율 — 1이면 그대로. 구름이 꼈는데 한낮처럼 밝으면 구름이 안 읽힌다")]
    [Range(0.1f, 1f)]
    [SerializeField] private float m_overcastIntensityScale = 0.55f;

    [Tooltip("밝아지고 어두워지는 데 걸리는 시간(초) — 즉시 바꾸면 조명이 툭 튄다")]
    [Min(0f)]
    [SerializeField] private float m_overcastFadeSeconds = 2.5f;

    [Header("낙뢰 섬광")]
    [Tooltip("섬광 최대 밝기 — 원래 밝기가 아니라 절대값이다")]
    [SerializeField] private float m_maxFlashIntensity = 3f;

    [Tooltip("섬광 한 번의 전체 길이(초). 이 안에서 밝아짐-죽음-더 밝아짐-감쇠가 일어난다")]
    [Min(0.05f)]
    [SerializeField] private float m_flashDuration = 0.45f;

    [Tooltip("낙뢰 파티클이 남아 있는 시간(초)")]
    [Min(0.1f)]
    [SerializeField] private float m_strikeFxSeconds = 3f;

    private LightningEvent m_lightningEvent;
    private bool m_overcastPushed;

    private float m_baseIntensity;
    private bool m_hasBaseIntensity;

    private CancellationTokenSource m_flashCts;

    private GameObject m_warningFx;

    private void Start()
    {
        m_lightningEvent = App.Game.SuddenEvent?.GetEvent<LightningEvent>();
        if (m_lightningEvent == null)
            return;

        CaptureBaseIntensity();

        m_lightningEvent.OnLightningChanged += HandleLightningChanged;
        m_lightningEvent.OnStrikeWarning += HandleStrikeWarning;
        m_lightningEvent.OnStrike += HandleStrike;
        HandleLightningChanged(m_lightningEvent.IsLightningActive);
    }

    private void OnDestroy()
    {
        if (m_lightningEvent != null)
        {
            m_lightningEvent.OnLightningChanged -= HandleLightningChanged;
            m_lightningEvent.OnStrikeWarning -= HandleStrikeWarning;
            m_lightningEvent.OnStrike -= HandleStrike;
        }

        ClearWarningFx();
        App.Sound?.StopAmbient2D(EAudioClip.RainLoop);

        PopOvercast(0f);

        StopFlash();
        RestoreIntensity();
    }

    private void PopOvercast(float fadeSeconds)
    {
        if (!m_overcastPushed)
            return;

        m_overcastPushed = false;
        WeatherOvercast.Pop(fadeSeconds);
    }

    private void CaptureBaseIntensity()
    {
        if (m_hasBaseIntensity)
            return;

        if (m_globalLight == null)
            m_globalLight = RenderSettings.sun;

        if (m_globalLight == null)
            return;

        m_baseIntensity = m_globalLight.intensity;
        m_hasBaseIntensity = true;
    }

    private void RestoreIntensity()
    {
        if (m_globalLight != null && m_hasBaseIntensity)
            m_globalLight.intensity = m_baseIntensity;
    }

    private void HandleLightningChanged(bool isActive)
    {
        if (isActive)
            ShowRain();
        else
            HideRain();
    }

    private void ShowRain()
    {
        CaptureBaseIntensity();

        Screen?.Show(PrecipitationScreen.EKind.Rain);

        if (!m_overcastPushed)
        {
            m_overcastPushed = true;
            WeatherOvercast.Push(m_overcastIntensityScale, m_overcastFadeSeconds);
        }

        App.Sound?.PlayAmbient2D(EAudioClip.RainLoop);
    }

    private void HideRain()
    {
        Screen?.Hide(PrecipitationScreen.EKind.Rain);

        StopFlash();
        ClearWarningFx();
        PopOvercast(m_overcastFadeSeconds);

        App.Sound?.StopAmbient2D(EAudioClip.RainLoop);
    }

    private PrecipitationScreen Screen =>
        m_screen != null ? m_screen : m_screen = GetComponent<PrecipitationScreen>();

    private PrecipitationScreen m_screen;

    private void HandleStrikeWarning(Vector3 position)
    {
        ClearWarningFx();

        if (m_warningPrefab != null)
            m_warningFx = Instantiate(m_warningPrefab, position, Quaternion.identity);
    }

    private void ClearWarningFx()
    {
        if (m_warningFx != null)
            Destroy(m_warningFx);

        m_warningFx = null;
    }

    private void HandleStrike(Vector3 position)
    {
        ClearWarningFx();

        App.Sound?.PlaySfxAt(EAudioClip.LightningStrike, position);

        if (m_strikeParticlePrefab != null)
            Destroy(Instantiate(m_strikeParticlePrefab, position, Quaternion.identity), m_strikeFxSeconds);

        if (m_globalLight == null || !gameObject.activeInHierarchy)
            return;

        CaptureBaseIntensity();

        StopFlash();

        m_flashCts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
        FlashAsync(m_flashCts.Token).Forget();
    }

    /// <summary>다중 피크와 감쇠 꼬리를 가진 번개 섬광을 재생한다.</summary>
    private async UniTaskVoid FlashAsync(CancellationToken token)
    {
        float dark = m_hasBaseIntensity ? m_globalLight.intensity : 0f;

        for (float t = 0f; t < m_flashDuration; t += Time.deltaTime)
        {
            float p = t / m_flashDuration;
            float strength = StrikeEnvelope(p);
            m_globalLight.intensity = Mathf.Lerp(dark, m_maxFlashIntensity, strength);
            await UniTask.NextFrame(token);
        }

        if (WeatherOvercast.HasSun)
            m_globalLight.intensity = WeatherOvercast.CurrentTarget;
    }

    private static float StrikeEnvelope(float p)
    {
        if (p < 0.12f)
            return Mathf.InverseLerp(0f, 0.12f, p) * 0.55f;
        if (p < 0.22f)
            return Mathf.Lerp(0.55f, 0.15f, Mathf.InverseLerp(0.12f, 0.22f, p));
        if (p < 0.32f)
            return Mathf.Lerp(0.15f, 1f, Mathf.InverseLerp(0.22f, 0.32f, p));
        return Mathf.Lerp(1f, 0f, Mathf.InverseLerp(0.32f, 1f, p));
    }

    private void StopFlash()
    {
        if (m_flashCts == null)
            return;

        m_flashCts.Cancel();
        m_flashCts.Dispose();
        m_flashCts = null;
    }
}
