using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 창 모드·해상도 적용 확인창 — [유지]를 누르지 않으면 카운트다운 후 이전 값으로 되돌린다.
/// 저장은 [유지]에서만 한다.
/// </summary>
public class DisplayConfirmPanel : PanelBase
{
    private const float k_revertSeconds = 15f;

    [Header("문구")]
    [SerializeField]
    private TMP_Text m_countdownText;

    [Tooltip("남은 초를 채울 서식 — Settings.DisplayConfirm.Countdown ({0} = 남은 초)")]
    [SerializeField]
    private LocalizedString m_countdownFormat;

    [Header("버튼")]
    [SerializeField]
    private Button m_keepButton;

    [SerializeField]
    private Button m_revertButton;

    private EWindowMode m_previousMode;
    private Vector2Int m_previousResolution;
    private Action m_onReverted;

    private bool m_pending;

    private CancellationTokenSource m_countdownCts;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    protected override void Awake()
    {
        base.Awake();
        if (m_keepButton != null)
            m_keepButton.onClick.AddListener(HandleKeepClicked);
        if (m_revertButton != null)
            m_revertButton.onClick.AddListener(ClosePanel);
    }

    protected override void OnDestroy()
    {
        Revert();
        CancelCountdown();

        if (m_keepButton != null)
            m_keepButton.onClick.RemoveListener(HandleKeepClicked);
        if (m_revertButton != null)
            m_revertButton.onClick.RemoveListener(ClosePanel);

        base.OnDestroy();
    }

    /// <summary>적용 직전 값을 받아 확인 카운트다운을 시작한다. 되돌리면 onReverted를 호출한다.</summary>
    public void Begin(EWindowMode previousMode, Vector2Int previousResolution, Action onReverted)
    {
        m_previousMode = previousMode;
        m_previousResolution = previousResolution;
        m_onReverted = onReverted;
        m_pending = true;

        OpenPanel();

        CancelCountdown();
        m_countdownCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
        CountdownAsync(m_countdownCts.Token).Forget();
    }

    public override void ClosePanel()
    {
        Revert();
        CancelCountdown();
        base.ClosePanel();
    }

    private void HandleKeepClicked()
    {
        m_pending = false;
        GameSettings.KeepDisplay();
        ClosePanel();
    }

    private async UniTaskVoid CountdownAsync(CancellationToken ct)
    {
        try
        {
            for (int remaining = Mathf.CeilToInt(k_revertSeconds); remaining > 0; remaining--)
            {
                ShowRemaining(remaining);
                await UniTask.Delay(TimeSpan.FromSeconds(1), ignoreTimeScale: true, cancellationToken: ct);
            }

            ClosePanel();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ShowRemaining(int seconds)
    {
        if (m_countdownText == null || m_countdownFormat == null || m_countdownFormat.IsEmpty)
            return;

        m_countdownFormat.Arguments = new object[] { seconds };
        m_countdownText.text = m_countdownFormat.GetLocalizedString();
    }

    private void Revert()
    {
        if (!m_pending)
            return;

        m_pending = false;
        GameSettings.ApplyDisplay(m_previousMode, m_previousResolution);
        m_onReverted?.Invoke();
        m_onReverted = null;
    }

    private void CancelCountdown()
    {
        if (m_countdownCts == null)
            return;

        m_countdownCts.Cancel();
        m_countdownCts.Dispose();
        m_countdownCts = null;
    }
}
