using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 라운드 정산 패널 — 결과·팀 자금 증감·개인 칭호를 보여 주고 복귀 카운트다운을 표시한다(GDD 3-2).
/// 닫으면 SettlementConfirmGate에 확인을 보고한다. 열려 있는 동안 입력을 정지한다.
/// </summary>
public class SettlementPanel : PanelBase
{
    [Header("표시 텍스트 (지연 등장)")]
    [SerializeField]
    private TextMeshProUGUI m_resultText;

    [SerializeField]
    private TextMeshProUGUI m_fundText;

    [Header("개인 칭호 로스터 (#739, 리썰 컴퍼니 스타일)")]
    [SerializeField]
    private RectTransform m_titleRowContainer;

    [SerializeField]
    private SettlementTitleRowView m_titleRowPrefab;

    private readonly List<SettlementTitleRowView> m_titleRows = new List<SettlementTitleRowView>();

    [Header("상점 복귀 카운트다운 (화면 중앙 상단)")]
    [SerializeField]
    private TextMeshProUGUI m_countdownText;

    [Header("연출 타이밍")]
    [Tooltip("패널이 뜬 뒤 결과 텍스트가 나타나기까지의 지연(초)")]
    [SerializeField]
    private float m_textRevealDelay = 1.5f;

    [Tooltip("텍스트 등장 후 상점 복귀까지 카운트다운(초). RoundEndResetter 딜레이 = 이 값 + 텍스트 지연으로 맞출 것")]
    [SerializeField]
    private float m_countdownSeconds = 10f;

    [Header("닫기 버튼")]
    [SerializeField]
    private Button m_confirmButton;

    [Header("문구")]
    [Tooltip("팀 자금 증감 요약 — Settlement.Fund.Summary ({0}=팀 몫 증감, {1}=팀 자금, {2}=할당량)")]
    [SerializeField]
    private LocalizedString m_fundSummary;

    [Tooltip("실패 시 결과 줄에 사유를 흡수 — Settlement.Result.WithReason ({0}=결과, {1}=사유)")]
    [SerializeField]
    private LocalizedString m_resultWithReasonFormat;

    [Tooltip("복귀 카운트다운 — Settlement.Countdown ({0}=도착지, {1}=남은 초, {2}=확인 인원, {3}=총원)")]
    [SerializeField]
    private LocalizedString m_countdownFormat;

    private const string k_table = "SettlementTable";
    private const string k_resultPrefix = "Settlement.Result.";
    private const string k_returnPrefix = "Settlement.Return.";
    private const string k_reasonPrefix = "Settlement.Reason.";
    private const string k_titlePrefix = "Settlement.Title.";

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    private bool m_playerBlocked;

    private CancellationTokenSource m_revealCts;

    private int m_secondsLeft;

    private bool m_confirmEnabled;

    private bool m_confirmReported;

    private SettlementConfirmGate Gate => App.Game.SettlementGate;

    protected override void Awake()
    {
        base.Awake();
        SetResultTextsVisible(false);
        SetCountdownVisible(false);
        SetConfirmEnabled(false);
        if (m_confirmButton != null)
            m_confirmButton.onClick.AddListener(ClosePanel);
    }

    protected override void OnDestroy()
    {
        CancelReveal();
        UnbindGate();

        if (m_playerBlocked)
            SetLocalPlayerBlocked(false);
        if (m_confirmButton != null)
            m_confirmButton.onClick.RemoveListener(ClosePanel);
        base.OnDestroy();
    }

    private string m_returnLabel = string.Empty;

    /// <summary>정산 데이터를 채우고 패널을 연다. 결과 텍스트는 지연 후 등장한다.</summary>
    public void Show(SettlementData data)
    {
        RoundResult result = data.Result == RoundResult.Success ? RoundResult.Success : RoundResult.Failure;

        m_returnLabel = LocalizedStrings.Get(k_table, k_returnPrefix + result);

        if (m_resultText != null)
        {
            string resultBase = LocalizedStrings.Get(k_table, k_resultPrefix + result);
            m_resultText.text =
                result == RoundResult.Failure && data.Reason != RoundEndReason.None
                    ? m_resultWithReasonFormat.GetLocalizedString(resultBase, ReasonToText(data.Reason))
                    : resultBase;
        }

        if (m_fundText != null)
            m_fundText.text = m_fundSummary.GetLocalizedString(
                data.FundDelta,
                data.FundBalance,
                data.TargetFund
            );

        BuildTitleRows(data.PlayerTitles);

        BindGate();

        m_confirmReported = false;
        m_secondsLeft = Mathf.CeilToInt(m_countdownSeconds);

        SetResultTextsVisible(false);
        SetCountdownVisible(false);
        SetConfirmEnabled(false);
        OpenPanel();

        CancelReveal();
        m_revealCts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
        RevealAndCountdownAsync(m_revealCts.Token).Forget();
    }

    private async UniTaskVoid RevealAndCountdownAsync(CancellationToken ct)
    {
        try
        {
            await UniTask.Delay(
                TimeSpan.FromSeconds(m_textRevealDelay),
                ignoreTimeScale: true,
                cancellationToken: ct
            );

            SetResultTextsVisible(true);
            SetCountdownVisible(true);
            SetConfirmEnabled(true);

            for (int sec = Mathf.CeilToInt(m_countdownSeconds); sec > 0; sec--)
            {
                SetCountdownSeconds(sec);
                await UniTask.Delay(TimeSpan.FromSeconds(1), ignoreTimeScale: true, cancellationToken: ct);
            }
            SetCountdownSeconds(0);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetCountdownSeconds(int seconds)
    {
        m_secondsLeft = seconds;
        RefreshCountdownText();
    }

    private void RefreshCountdownText()
    {
        if (m_countdownText == null)
            return;

        SettlementConfirmGate gate = Gate;
        int confirmed = gate != null ? gate.ConfirmedCount : 0;
        int expected = gate != null ? gate.ExpectedCount : 1;

        m_countdownText.text = m_countdownFormat.GetLocalizedString(
            m_returnLabel,
            m_secondsLeft,
            confirmed,
            expected
        );
    }

    private static string ReasonToText(RoundEndReason reason)
    {
        return reason == RoundEndReason.None
            ? string.Empty
            : LocalizedStrings.Get(k_table, k_reasonPrefix + reason);
    }

    private void BindGate()
    {
        UnbindGate();

        if (Gate != null)
            Gate.OnCountsChanged += RefreshCountdownText;
    }

    private void UnbindGate()
    {
        if (Gate != null)
            Gate.OnCountsChanged -= RefreshCountdownText;
    }

    private void SetResultTextsVisible(bool visible)
    {
        if (m_resultText != null)
            m_resultText.gameObject.SetActive(visible);
        if (m_fundText != null)
            m_fundText.gameObject.SetActive(visible);
        if (m_titleRowContainer != null)
            m_titleRowContainer.gameObject.SetActive(visible);
    }

    private void BuildTitleRows(List<SettlementPlayerTitle> titles)
    {
        if (m_titleRowContainer == null || m_titleRowPrefab == null)
            return;

        titles ??= new List<SettlementPlayerTitle>();

        while (m_titleRows.Count < titles.Count)
            m_titleRows.Add(Instantiate(m_titleRowPrefab, m_titleRowContainer));

        for (int i = 0; i < m_titleRows.Count; i++)
        {
            bool used = i < titles.Count;
            m_titleRows[i].gameObject.SetActive(used);
            if (!used)
                continue;

            m_titleRows[i].SetName(titles[i].PlayerName);
            m_titleRows[i].SetTitle(TitleText(titles[i].Title));
        }
    }

    private static string TitleText(SettlementTitleKind kind) =>
        kind == SettlementTitleKind.None ? string.Empty : LocalizedStrings.Get(k_table, k_titlePrefix + kind);

    private void SetCountdownVisible(bool visible)
    {
        if (m_countdownText != null)
            m_countdownText.gameObject.SetActive(visible);
    }

    private void SetConfirmEnabled(bool enabled)
    {
        m_confirmEnabled = enabled;
        if (m_confirmButton != null)
            m_confirmButton.interactable = enabled;
    }

    private void ReportConfirmed()
    {
        if (m_confirmReported || !m_confirmEnabled || !IsOpened)
            return;

        m_confirmReported = true;
        if (Gate != null)
            Gate.ReportSelfConfirmed();
    }

    private void CancelReveal()
    {
        if (m_revealCts == null)
            return;
        m_revealCts.Cancel();
        m_revealCts.Dispose();
        m_revealCts = null;
    }

    public override void OpenPanel()
    {
        base.OpenPanel();
        SetLocalPlayerBlocked(true);
    }

    public override void ClosePanel()
    {
        ReportConfirmed();

        SetLocalPlayerBlocked(false);
        base.ClosePanel();
    }

    private void SetLocalPlayerBlocked(bool blocked)
    {
        if (m_playerBlocked == blocked)
            return;

        m_playerBlocked = blocked;

        if (blocked)
            CursorLock.PushUnlock();
        else
            CursorLock.PopUnlock();

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.LocalClient == null || nm.LocalClient.PlayerObject == null)
            return;

        GameObject player = nm.LocalClient.PlayerObject.gameObject;

        PlayerInputHandler input = player.GetComponent<PlayerInputHandler>();
        if (input != null)
            input.SetSuspended(blocked);

        PlayerMovement movement = player.GetComponent<PlayerMovement>();
        if (movement == null)
            return;

        movement.SetIgnoreRoundEndFreeze(!blocked);
    }
}
