using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

/// <summary>
/// 화면 중앙 크로스헤어·히트마커·처치 알림 표시. App.UI.Crosshair로 접근한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class CrosshairUI : CommonManagerBase
{
    [Header("조합형 크로스헤어 (#945)")]
    [SerializeField] private RectTransform m_lineUp;
    [SerializeField] private RectTransform m_lineDown;
    [SerializeField] private RectTransform m_lineLeft;
    [SerializeField] private RectTransform m_lineRight;
    [SerializeField] private RectTransform m_dot;
    [SerializeField] private RectTransform m_circleRing;
    [SerializeField] private Image m_circleImage;
    [SerializeField] private RectTransform m_crosshairRoot;
    [SerializeField] private PlayerColorPalette m_colorPalette;

    private CrosshairVisualRefs VisualRefs => new CrosshairVisualRefs
    {
        Up = m_lineUp, Down = m_lineDown, Left = m_lineLeft, Right = m_lineRight,
        Dot = m_dot, CircleRing = m_circleRing, CircleImage = m_circleImage,
    };

    private CrosshairSettings m_currentSettings = CrosshairSettings.Default();

    [Tooltip("공용 색 팔레트 — 상호작용 가능 대상 조준 시 Highlight를 쓴다 (#951)")]
    [SerializeField] private UiColorPalette m_uiPalette;
    [Tooltip("조준 무기(테이저·진압봉)로 명중 가능한 대상을 겨눴을 때 색 (#328/#217)")]
    [FormerlySerializedAs("m_taserTargetColor")]
    [SerializeField] private Color m_weaponTargetColor = new Color(1f, 0.25f, 0.2f);

    private bool m_uiPaletteWarned;

    private Color InteractableColor
    {
        get
        {
            if (m_uiPalette != null)
                return m_uiPalette.Highlight;

            if (!m_uiPaletteWarned)
            {
                m_uiPaletteWarned = true;
                Debug.LogWarning("CrosshairUI: UI 색 팔레트가 연결되지 않았다", this);
            }

            return Color.white;
        }
    }

    /// <summary>크로스헤어 표시를 켜고 끈다(히트마커·처치 알림은 별개).</summary>
    public void SetVisible(bool visible)
    {
        if (m_crosshairRoot != null)
            m_crosshairRoot.gameObject.SetActive(visible);
    }

    /// <summary>조준 대상의 상호작용 가능 여부에 따라 크로스헤어 색을 바꾼다.</summary>
    public void SetInteractable(bool interactable)
    {
        Color? overrideColor = interactable ? InteractableColor : (Color?)null;
        CrosshairRenderer.ApplyColor(VisualRefs, m_currentSettings, overrideColor, m_colorPalette);
    }

    /// <summary>조준 무기로 명중 가능한 대상을 겨눴는지에 따라 크로스헤어 색을 바꾼다.</summary>
    public void SetWeaponTargeting(bool onTarget)
    {
        Color? overrideColor = onTarget ? m_weaponTargetColor : (Color?)null;
        CrosshairRenderer.ApplyColor(VisualRefs, m_currentSettings, overrideColor, m_colorPalette);
    }

    [Header("히트마커")]
    [Tooltip("명중 순간 잠깐 켜지는 마커 컨테이너 — 하위 그래픽 전부에 색이 칠해진다. 비우면 히트마커가 뜨지 않는다")]
    [SerializeField] private RectTransform m_hitMarker;

    [Tooltip("유효타 히트마커 색")]
    [SerializeField] private Color m_hitColor = Color.white;

    [Tooltip("동료를 때렸을 때 히트마커 색 — 오사를 즉시 알아야 한다 (#461)")]
    [SerializeField] private Color m_friendlyFireColor = new Color(1f, 0.35f, 0.1f);

    [Tooltip("히트마커 표시 시간(초)")]
    [Min(0.02f)]
    [SerializeField] private float m_hitMarkerSeconds = 0.15f;

    private float m_hitMarkerHideTime;

    /// <summary>유효타 순간 히트마커를 잠깐 띄운다. 오너 전용.</summary>
    public void ShowHit(bool friendlyFire)
    {
        if (m_hitMarker == null)
            return;

        Color color = friendlyFire ? m_friendlyFireColor : m_hitColor;
        foreach (Graphic graphic in HitMarkerGraphics)
            graphic.color = color;

        m_hitMarker.gameObject.SetActive(true);
        m_hitMarkerHideTime = Time.time + m_hitMarkerSeconds;
    }

    private void Update()
    {
        if (m_hitMarker != null && m_hitMarker.gameObject.activeSelf && Time.time >= m_hitMarkerHideTime)
            m_hitMarker.gameObject.SetActive(false);
    }

    protected override void Awake()
    {
        base.Awake();
        CosmeticLoadout.OnCrosshairSettingsChanged += HandleCrosshairSettingsChanged;
        ApplyCurrentSettings();
    }

    private void HandleCrosshairSettingsChanged() => ApplyCurrentSettings();

    private void ApplyCurrentSettings()
    {
        m_currentSettings = CosmeticLoadout.GetCrosshairSettings();
        CrosshairRenderer.ApplyShape(VisualRefs, m_currentSettings);
        CrosshairRenderer.ApplyColor(VisualRefs, m_currentSettings, null, m_colorPalette);
    }

    protected override void OnDestroy()
    {
        CosmeticLoadout.OnCrosshairSettingsChanged -= HandleCrosshairSettingsChanged;
        CancelKillFade();
        base.OnDestroy();
    }

    private Graphic[] m_hitMarkerGraphics;

    private Graphic[] HitMarkerGraphics =>
        m_hitMarkerGraphics ??= m_hitMarker.GetComponentsInChildren<Graphic>(true);

    private const string k_table = "HudTable";
    private const string k_killKey = "Hud.Kill.Confirm";

    [Header("처치 알림")]
    [Tooltip("크로스헤어 바로 아래 뜨는 처치 대상 이름 텍스트. 비우면 처치 알림이 뜨지 않는다")]
    [SerializeField] private TMPro.TMP_Text m_killLabel;

    [Tooltip("처치 텍스트 색")]
    [SerializeField] private Color m_killColor = new Color(1f, 0.35f, 0.1f);

    [Tooltip("처치 텍스트 표시 시간(초) — 페이드 시작 전까지 또렷하게 떠 있는 시간")]
    [Min(0.02f)]
    [SerializeField] private float m_killMarkerSeconds = 1.2f;

    [Tooltip("처치 텍스트가 서서히 사라지는 데 걸리는 시간(초)")]
    [Min(0.02f)]
    [SerializeField] private float m_killFadeSeconds = 0.4f;

    private CancellationTokenSource m_killFadeCts;

    /// <summary>처치 대상 이름을 크로스헤어 아래에 잠깐 띄웠다가 페이드한다. 오너 전용.</summary>
    public void ShowKill(string victimName, bool friendlyFire)
    {
        if (m_killLabel == null)
            return;

        if (m_hitMarker != null)
            m_hitMarker.gameObject.SetActive(false);

        m_killLabel.color = m_killColor;
        m_killLabel.text = LocalizedStrings.Get(k_table, k_killKey, victimName);
        m_killLabel.alpha = 1f;
        m_killLabel.gameObject.SetActive(true);

        CancelKillFade();
        m_killFadeCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
        FadeKillLabelAsync(m_killFadeCts.Token).Forget();
    }

    private async UniTaskVoid FadeKillLabelAsync(CancellationToken ct)
    {
        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(m_killMarkerSeconds), ignoreTimeScale: true, cancellationToken: ct);

            float elapsed = 0f;
            while (elapsed < m_killFadeSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                m_killLabel.alpha = 1f - Mathf.Clamp01(elapsed / m_killFadeSeconds);
                await UniTask.Yield(PlayerLoopTiming.Update, ct);
            }

            m_killLabel.gameObject.SetActive(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelKillFade()
    {
        if (m_killFadeCts == null)
            return;
        m_killFadeCts.Cancel();
        m_killFadeCts.Dispose();
        m_killFadeCts = null;
    }
}
