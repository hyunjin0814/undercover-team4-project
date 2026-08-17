using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// 조준한 대상의 키 + 동작을 띄우는 안내("[E] 문 열기") — App.UI.InteractPrompt로 접근한다. (#664)
/// 조준이 유지되는 동안 떠 있고, <see cref="InteractionFeedback"/>이 조준 판정과 함께 넣고 뺀다.
/// <see cref="PromptView"/>와 자리를 나눈 이유: 저쪽은 구조 대기·운반처럼 상태가 유지되는 내내
/// 떠 있는 안내라, 한자리를 쓰면 동료를 업고 문을 겨눴을 때 동시에 성립하는 둘 중 하나가 밀린다.
/// </summary>
// 실행 순서는 베이스에도 있지만 각 구체 클래스에 다시 명시한다 — architecture.md R4.
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class InteractPromptView : LocalizedMessageView
{
    [Tooltip("키 + 동작 — HudTable/Hud.Interact.Format. {0}=키 표기, {1}=동작")]
    [SerializeField]
    private LocalizedString m_format;

    [Tooltip("막혔을 때 — HudTable/Hud.Interact.BlockedFormat. {0}=키 표기, {1}=동작, {2}=사유")]
    [SerializeField]
    private LocalizedString m_blockedFormat;

    [Tooltip("막힌 대상의 배경 톤 — 눌러도 지금은 안 된다는 것을 색으로 가른다")]
    [SerializeField]
    private Color m_blockedTone = new Color(0.25f, 0.25f, 0.25f, 0.75f);

    // 지금 띄워 둔 조합 — 매 프레임 들어오므로 같은 조합이면 아무것도 하지 않는다.
    // 매번 Show를 다시 부르면 초당 수십 번 구독을 갈아치운다 (PlayerReviveHud와 같은 방침).
    private LocalizedString m_shownAction;
    private LocalizedString m_shownReason;
    private string m_shownKey;

    /// <summary>
    /// 지금 조준 안내가 떠 있는가 — 상태 안내(<see cref="PlayerReviveHud"/>)가 겹쳐 뜨지 않으려고 본다.
    /// 조준 안내가 이미 "무슨 키로 무엇을 하는지"를 말하고 있으면 상태 줄은 같은 말을 반복한다. (#664)
    /// </summary>
    /// 실제로 그려졌는지(IsShowing)까지 본다 — 형식 문구 배선이 비어 Apply가 중단되면 문구를 잡고도
    /// 화면은 비는데, 그걸 "떠 있다"로 답하면 상태 줄까지 접혀 안내가 통째로 사라진다.
    public bool IsPromptShowing => m_shownAction != null && IsShowing;

    protected override void Awake()
    {
        base.Awake(); // App에 등록

        // 언어가 바뀌면 인자로 넣어 둔 동작·사유 문구를 다시 풀어야 한다 — 아래 Apply 주석 참고.
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
    }

    protected override void OnDestroy()
    {
        LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
        base.OnDestroy(); // App 등록 해제
    }

    /// <summary>
    /// 안내를 띄운다 — 지울 때까지 남는다. reason이 있으면 회색 톤 + 사유가 붙는다.
    /// </summary>
    /// <param name="keyLabel">실제 바인딩에서 읽은 키 표기 (예: "E")</param>
    /// <param name="action">동작 문구 — null이면 아무것도 띄우지 않는다</param>
    /// <param name="reason">막힌 사유 — null이면 정상 톤</param>
    public void ShowPrompt(string keyLabel, LocalizedString action, LocalizedString reason)
    {
        if (action == null || action.IsEmpty)
        {
            HidePrompt();
            return;
        }

        // 같은 조합이면 그대로 둔다. 상태가 바뀌면(닫힌 문 → 열린 문) 참조가 달라져 다시 띄워진다.
        if (
            ReferenceEquals(action, m_shownAction)
            && ReferenceEquals(reason, m_shownReason)
            && keyLabel == m_shownKey
        )
        {
            return;
        }

        m_shownAction = action;
        m_shownReason = reason;
        m_shownKey = keyLabel;
        Apply();
    }

    /// <summary>조준이 풀렸다 — 떠 있던 안내를 지운다.</summary>
    public void HidePrompt()
    {
        if (m_shownAction == null)
            return;

        m_shownAction = null;
        m_shownReason = null;
        m_shownKey = null;
        HideImmediate();
    }

    /// <summary>
    /// 형식 문구에 인자를 넣어 띄운다.
    /// 동작·사유를 LocalizedString 그대로 인자에 넣으면 풀리지 않고 참조가 찍힌다(Smart String을 켜도
    /// 같다) — 미리 문자열로 풀어 넣고, 언어 전환은 <see cref="HandleLocaleChanged"/>가 맞춘다.
    /// </summary>
    private void Apply()
    {
        LocalizedString format = m_shownReason != null ? m_blockedFormat : m_format;
        if (format == null || format.IsEmpty)
            return;

        // 인자를 먼저 넣어야 구독 시점의 첫 발화부터 올바른 문장이 나온다 (PlayerReviveHud와 동일).
        format.Arguments =
            m_shownReason != null
                ? new object[]
                {
                    m_shownKey,
                    m_shownAction.GetLocalizedString(),
                    m_shownReason.GetLocalizedString(),
                }
                : new object[] { m_shownKey, m_shownAction.GetLocalizedString() };

        ApplyTone(m_shownReason != null ? m_blockedTone : (Color?)null);
        ShowMessage(format);
    }

    // 언어 전환 — 형식 문구는 베이스가 구독으로 갱신하지만 이미 풀어 넣은 인자는 따라오지 않는다.
    private void HandleLocaleChanged(UnityEngine.Localization.Locale locale)
    {
        if (m_shownAction != null)
            Apply();
    }
}
