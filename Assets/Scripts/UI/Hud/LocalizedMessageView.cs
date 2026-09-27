using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 번역된 한 줄을 HUD 한 자리에 띄우는 공통 베이스 — 구독·표시·숨김만 담당한다.
/// 언제 사라지는지는 파생(TimedMessageView·PromptView)이 정한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public abstract class LocalizedMessageView : CommonManagerBase
{
    [Tooltip("표시 루트 — 배경 + 문구를 함께 켜고 끄고, 알파로 페이드한다")]
    [SerializeField]
    private CanvasGroup m_group;

    [Tooltip("문구를 그릴 TMP 텍스트 (m_group 하위)")]
    [SerializeField]
    private TMP_Text m_label;

    [Tooltip("톤 색을 입힐 배경 그래픽 — 비어 있으면 색을 건드리지 않는다")]
    [SerializeField]
    private Graphic m_toneTarget;

    private Color m_defaultTone = Color.white;

    private LocalizedString m_bound;

    protected CanvasGroup Group => m_group;

    protected bool IsShowing { get; private set; }

    protected override void Awake()
    {
        base.Awake();

        if (m_toneTarget != null)
            m_defaultTone = m_toneTarget.color;

        SetVisible(false);
    }

    protected override void OnDestroy()
    {
        Unbind();
        base.OnDestroy();
    }

    /// <summary>문구를 표시한다. 이미 떠 있으면 덮어쓴다.</summary>
    protected void ShowMessage(LocalizedString message)
    {
        if (m_group == null || m_label == null)
            return;
        if (message == null || message.IsEmpty)
            return;

        Unbind();

        m_bound = message;
        m_bound.StringChanged += HandleStringChanged;

        IsShowing = true;
        SetVisible(true);
    }

    /// <summary>배경 톤을 정한다. null이면 프리팹 기본색으로 되돌린다.</summary>
    protected void ApplyTone(Color? tone)
    {
        if (m_toneTarget == null)
            return;

        m_toneTarget.color = tone ?? m_defaultTone;
    }

    /// <summary>이 문구가 아직 떠 있을 때만 지운다 — 그 사이 다른 문구가 덮어썼으면 건드리지 않는다.</summary>
    public void Hide(LocalizedString message)
    {
        if (message == null || !ReferenceEquals(m_bound, message))
            return;

        HideImmediate();
    }

    /// <summary>누가 띄웠든 즉시 지운다 (라운드 종료·씬 전환 등 화면을 통째로 비울 때).</summary>
    public void HideImmediate()
    {
        Unbind();
        IsShowing = false;
        SetVisible(false);
    }

    private void HandleStringChanged(string localized)
    {
        if (m_label != null)
            m_label.text = localized;
    }

    private void Unbind()
    {
        if (m_bound == null)
            return;

        m_bound.StringChanged -= HandleStringChanged;
        m_bound = null;
    }

    private void SetVisible(bool visible)
    {
        if (m_group == null)
            return;

        if (visible)
            m_group.alpha = 1f;

        m_group.gameObject.SetActive(visible);
    }
}
