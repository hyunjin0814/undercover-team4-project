using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 검거 판정 결과(진범/오검거/경범죄)를 판정별 색의 비차단 배너로 잠깐 띄운다.
/// 데이터는 ArrestVerdictFeedback가 넘기며, 일정 시간 뒤 자동으로 숨는다.
/// </summary>
public class VerdictBanner : PanelBase
{
    [Header("텍스트")]
    [SerializeField] private TextMeshProUGUI m_titleText;
    [SerializeField] private TextMeshProUGUI m_detailText;

    [Header("톤 (판정별 강조 색)")]
    [SerializeField] private Graphic m_toneTarget;
    [Range(0f, 1f)]
    [SerializeField] private float m_fillAlpha = 0.95f;
    [Tooltip("판정별 강조 색 — 성공/실패/중립/주의를 공용 팔레트에서 가져온다 (#951)")]
    [SerializeField] private UiColorPalette m_palette;

    [Header("표시 시간")]
    [SerializeField] private float m_displaySeconds = 3f;

    [Header("등장/퇴장 애니메이션 (#943)")]
    [Tooltip("펀치인·페이드에 쓰는 CanvasGroup — 비우면 애니메이션 없이 즉시 표시된다")]
    [SerializeField] private CanvasGroup m_canvasGroup;
    [Tooltip("스케일 애니메이션 대상 — 비우면 패널 루트를 그대로 쓴다")]
    [SerializeField] private RectTransform m_animRoot;
    [SerializeField] private float m_enterSeconds = 0.18f;
    [SerializeField] private float m_exitSeconds = 0.2f;

    private const float k_enterStartScale = 0.9f;

    [Tooltip("이름 + 보상 표시 — Hud.Verdict.Detail ({0}=시민 이름, {1}=보상 금액)")]
    [SerializeField] private LocalizedString m_detailFormat;

    private const string k_hudTable = "HudTable";
    private const string k_verdictPrefix = "Hud.Verdict.";

    public override bool CanCloseWithESC => false;
    public override bool IsStackable => false;

    private CancellationTokenSource m_showCts;

    private bool m_detailBound;

    private RectTransform AnimRoot => m_animRoot != null ? m_animRoot
        : (m_panelRoot != null ? m_panelRoot.GetComponent<RectTransform>() : null);

    protected override void OnDestroy()
    {
        CancelShowSequence();
        UnbindDetail();
        base.OnDestroy();
    }

    /// <summary>배너가 닫히면 구독도 끊는다 — 안 보이는 문구가 언어 변경에 반응할 이유가 없다.</summary>
    public override void ClosePanel()
    {
        UnbindDetail();
        base.ClosePanel();
    }

    /// <summary>판정 데이터를 채우고 배너를 띄운다. m_displaySeconds초 뒤 애니메이션과 함께 자동으로 숨는다.</summary>
    public void Show(VerdictFeedbackData data)
    {
        if (m_titleText != null)
            m_titleText.text = LocalizedStrings.Get(k_hudTable, k_verdictPrefix + data.Verdict);

        if (m_detailText != null)
        {
            if (data.Reward > 0)
                BindDetail(data);
            else
            {
                UnbindDetail();
                m_detailText.text = data.CitizenName;
            }
        }

        Color tone = VerdictToColor(data.Verdict);
        if (m_toneTarget != null)
            m_toneTarget.color = new Color(tone.r, tone.g, tone.b, m_fillAlpha);

        App.Sound?.PlaySfx2D(VerdictToSound(data.Verdict));

        CancelShowSequence();
        m_showCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
        PlayShowSequenceAsync(m_showCts.Token).Forget();
    }

    private async UniTaskVoid PlayShowSequenceAsync(CancellationToken ct)
    {
        try
        {
            if (m_canvasGroup != null)
                m_canvasGroup.alpha = 0f;

            RectTransform rt = AnimRoot;
            if (rt != null)
                rt.localScale = Vector3.one * k_enterStartScale;

            OpenPanel();

            await AnimateAsync(m_enterSeconds, k_enterStartScale, 1f, 0f, 1f, ct);

            await UniTask.Delay(TimeSpan.FromSeconds(m_displaySeconds), ignoreTimeScale: true, cancellationToken: ct);

            await AnimateAsync(m_exitSeconds, 1f, 1f, 1f, 0f, ct);

            ClosePanel();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async UniTask AnimateAsync(
        float seconds, float fromScale, float toScale, float fromAlpha, float toAlpha, CancellationToken ct)
    {
        RectTransform rt = AnimRoot;

        if (seconds <= 0f)
        {
            if (rt != null)
                rt.localScale = Vector3.one * toScale;
            if (m_canvasGroup != null)
                m_canvasGroup.alpha = toAlpha;
            return;
        }

        float elapsed = 0f;
        while (elapsed < seconds)
        {
            ct.ThrowIfCancellationRequested();

            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / seconds);

            if (rt != null)
                rt.localScale = Vector3.one * Mathf.Lerp(fromScale, toScale, t);
            if (m_canvasGroup != null)
                m_canvasGroup.alpha = Mathf.Lerp(fromAlpha, toAlpha, t);

            await UniTask.Yield(PlayerLoopTiming.Update, ct);
        }

        if (rt != null)
            rt.localScale = Vector3.one * toScale;
        if (m_canvasGroup != null)
            m_canvasGroup.alpha = toAlpha;
    }

    private void CancelShowSequence()
    {
        if (m_showCts == null)
            return;
        m_showCts.Cancel();
        m_showCts.Dispose();
        m_showCts = null;
    }

    private void BindDetail(VerdictFeedbackData data)
    {
        if (m_detailFormat == null || m_detailFormat.IsEmpty)
        {
            Debug.LogWarning("VerdictBanner: 이름·보상 문구가 연결되지 않았다", this);

            m_detailText.text = data.CitizenName;
            return;
        }

        UnbindDetail();

        m_detailFormat.Arguments = new object[] { data.CitizenName, data.Reward };
        m_detailFormat.StringChanged += HandleDetailChanged;
        m_detailBound = true;
    }

    private void HandleDetailChanged(string localized)
    {
        if (m_detailText != null)
            m_detailText.text = localized;
    }

    private void UnbindDetail()
    {
        if (!m_detailBound)
            return;

        m_detailFormat.StringChanged -= HandleDetailChanged;
        m_detailBound = false;
    }

    private static EAudioClip VerdictToSound(ArrestVerdict verdict)
    {
        return verdict.IsCredited() ? EAudioClip.UiSuccess : EAudioClip.UiFail;
    }

    private Color VerdictToColor(ArrestVerdict verdict)
    {
        if (m_palette == null)
        {
            Debug.LogWarning("VerdictBanner: 색 팔레트가 연결되지 않았다", this);
            return Color.white;
        }

        return verdict switch
        {
            ArrestVerdict.WantedCriminal => m_palette.Positive,
            ArrestVerdict.Misdemeanor => m_palette.Neutral,
            ArrestVerdict.ConditionUnmet => m_palette.Caution,
            _ => m_palette.Negative,
        };
    }
}
