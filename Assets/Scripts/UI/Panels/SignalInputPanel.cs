using TMPro;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 신호 해석기 메시지 입력 모달 — 상호작용한 본인 클라에서만 열리며, 열려 있는 동안 입력을 정지한다.
/// </summary>
public class SignalInputPanel : PanelBase
{
    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    [Header("입력")]
    [SerializeField]
    private TMP_InputField m_field;

    [Header("문구")]
    [Tooltip("창 제목 — WorldTable/World.SignalDecoder.InputTitle")]
    [SerializeField]
    private LocalizedString m_title;

    [SerializeField]
    private TMP_Text m_titleText;

    [Tooltip("조작 안내 — WorldTable/World.SignalDecoder.InputHint ({0}=입력 길이, {1}=최대 길이)")]
    [SerializeField]
    private LocalizedString m_hint;

    [SerializeField]
    private TMP_Text m_hintText;

    private SignalDecoder m_decoder;
    private PlayerInputHandler m_input;

    protected override void Awake()
    {
        base.Awake();

        if (m_field != null)
        {
            m_field.lineType = TMP_InputField.LineType.SingleLine;
            m_field.characterLimit = SignalDecoder.k_maxMessageLength;
            m_field.onSubmit.AddListener(HandleSubmit);
            m_field.onValueChanged.AddListener(HandleValueChanged);
        }

        m_title.StringChanged += HandleTitleChanged;

        ApplyHintArguments(0);
        m_hint.StringChanged += HandleHintChanged;
    }

    protected override void OnDestroy()
    {
        if (m_field != null)
        {
            m_field.onSubmit.RemoveListener(HandleSubmit);
            m_field.onValueChanged.RemoveListener(HandleValueChanged);
        }

        m_title.StringChanged -= HandleTitleChanged;
        m_hint.StringChanged -= HandleHintChanged;

        base.OnDestroy();
    }

    /// <summary>입력창을 연다 — 상호작용한 본인 클라이언트에서만 호출된다.</summary>
    public void Open(SignalDecoder decoder, GameObject interactor)
    {
        if (IsOpened || decoder == null || interactor == null)
            return;

        m_decoder = decoder;
        m_input = interactor.GetComponent<PlayerInputHandler>();

        if (m_field != null)
            m_field.SetTextWithoutNotify(string.Empty);
        RefreshHint(0);

        SetBlocked(true);
        OpenPanel();

        if (m_field != null)
            m_field.ActivateInputField();
    }

    /// <summary>창을 닫고 정지시킨 입력과 커서를 되돌린다.</summary>
    public override void ClosePanel()
    {
        if (!IsOpened)
            return;

        base.ClosePanel();

        if (m_field != null)
        {
            m_field.DeactivateInputField();
            m_field.SetTextWithoutNotify(string.Empty);
        }

        SetBlocked(false);

        m_input = null;
        m_decoder = null;
    }

    /// <summary>창이 열린 채 비활성·파괴될 때 입력 정지와 커서 해제를 되돌린다.</summary>
    private void OnDisable()
    {
        ClosePanel();

        SetBlocked(false);
    }

    protected override PlayerInputHandler BlockTarget => m_input;

    private void Update()
    {
        if (IsOpened && m_input == null)
            ClosePanel();
    }

    private void HandleSubmit(string text)
    {
        SignalDecoder decoder = m_decoder;
        ClosePanel();

        if (decoder != null)
            decoder.SendSignal(text);
    }

    private void HandleValueChanged(string text) => RefreshHint(text != null ? text.Length : 0);

    private void ApplyHintArguments(int length) =>
        m_hint.Arguments = new object[] { length, SignalDecoder.k_maxMessageLength };

    private void RefreshHint(int length)
    {
        ApplyHintArguments(length);
        m_hint.RefreshString();
    }

    private void HandleTitleChanged(string localized)
    {
        if (m_titleText != null)
            m_titleText.text = localized;
    }

    private void HandleHintChanged(string localized)
    {
        if (m_hintText != null)
            m_hintText.text = localized;
    }
}
