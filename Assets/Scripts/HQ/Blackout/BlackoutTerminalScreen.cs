using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 복구 단말의 월드 공간 화면 — 서버가 내린 코드를 띄우고 숫자 키 입력을 서버에 제출한다.
/// </summary>
public class BlackoutTerminalScreen : MonoBehaviour
{
    private const string k_table = "HudTable";

    private static readonly Key[] s_digitRow =
    {
        Key.Digit0, Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4,
        Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9,
    };

    private static readonly Key[] s_numpad =
    {
        Key.Numpad0, Key.Numpad1, Key.Numpad2, Key.Numpad3, Key.Numpad4,
        Key.Numpad5, Key.Numpad6, Key.Numpad7, Key.Numpad8, Key.Numpad9,
    };

    private static readonly LocalizedString s_hint = new LocalizedString(k_table, "Hud.Terminal.HackHint");

    [Header("연결")]
    [Tooltip("비우면 부모에서 찾는다 — 프리팹 안에서 쓰는 것이 기본이라 대개 비워 둔다")]
    [SerializeField] private BlackoutRecoveryTerminal m_terminal;

    [Header("표시")]
    [Tooltip("서버가 내린 복구 코드를 그대로 띄운다")]
    [SerializeField] private TextMeshProUGUI m_codeLabel;

    [Tooltip("지금까지 누른 숫자")]
    [SerializeField] private TextMeshProUGUI m_entryLabel;

    [Tooltip("본부가 무엇을 해야 하는지 — 코드는 몰라도 이 줄만 보면 알 수 있어야 한다")]
    [SerializeField] private TextMeshProUGUI m_hintLabel;

    [Tooltip("남은 시간 게이지 — 0이 되면 서버가 코드를 새로 뽑는다 (Filled 이미지)")]
    [SerializeField] private Image m_timerBar;

    private readonly List<int> m_entry = new List<int>();

    private bool m_submitted;

    private void Awake()
    {
        if (m_terminal == null)
            m_terminal = GetComponentInParent<BlackoutRecoveryTerminal>();
    }

    private void OnEnable()
    {
        ClearEntry();

        if (m_terminal != null)
            m_terminal.OnCodeChanged += HandleCodeChanged;

        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;
        ApplyHint();

        Redraw();
    }

    private void OnDisable()
    {
        LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;

        if (m_terminal != null)
            m_terminal.OnCodeChanged -= HandleCodeChanged;
    }

    private void HandleLocaleChanged(UnityEngine.Localization.Locale locale) => ApplyHint();

    private void ApplyHint()
    {
        if (m_hintLabel != null)
            m_hintLabel.text = s_hint.GetLocalizedString();
    }

    private void Update()
    {
        UpdateTimerBar();

        if (m_terminal == null || !m_terminal.IsLocalFocused || !m_terminal.IsOnline)
            return;

        ReadKeyboard();
    }

    private void UpdateTimerBar()
    {
        if (m_timerBar == null || m_terminal == null)
            return;

        float total = m_terminal.CodeSeconds;
        m_timerBar.fillAmount = total > 0f ? Mathf.Clamp01(m_terminal.RemainingSeconds / total) : 0f;
    }

    private void ReadKeyboard()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        for (int digit = 0; digit < s_digitRow.Length; digit++)
        {
            if (keyboard[s_digitRow[digit]].wasPressedThisFrame || keyboard[s_numpad[digit]].wasPressedThisFrame)
                Press(digit);
        }

        if (keyboard[Key.Backspace].wasPressedThisFrame)
            Erase();
    }

    private void HandleCodeChanged(int code)
    {
        ClearEntry();
        Redraw();
    }

    private void Press(int digit)
    {
        if (m_submitted)
            ClearEntry();

        if (m_entry.Count >= BlackoutRecoveryTerminal.k_codeDigits)
            return;

        m_entry.Add(digit);
        Redraw();

        if (m_entry.Count == BlackoutRecoveryTerminal.k_codeDigits)
            Submit();
    }

    private void Erase()
    {
        if (m_submitted)
        {
            ClearEntry();
            Redraw();
            return;
        }

        if (m_entry.Count == 0)
            return;

        m_entry.RemoveAt(m_entry.Count - 1);
        Redraw();
    }

    private void ClearEntry()
    {
        m_entry.Clear();
        m_submitted = false;
    }

    private void Submit()
    {
        int value = 0;
        for (int i = 0; i < m_entry.Count; i++)
            value = value * 10 + m_entry[i];

        m_submitted = true;
        m_terminal.SubmitCode(value);
    }

    private void Redraw()
    {
        if (m_codeLabel != null)
            m_codeLabel.text = FormatCode(m_terminal != null ? m_terminal.Code : -1);

        if (m_entryLabel == null)
            return;

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < BlackoutRecoveryTerminal.k_codeDigits; i++)
            sb.Append(i < m_entry.Count ? m_entry[i].ToString() : "_");

        m_entryLabel.text = sb.ToString();
    }

    private static string FormatCode(int code)
    {
        if (code < 0)
            return new string('-', BlackoutRecoveryTerminal.k_codeDigits);

        return code.ToString().PadLeft(BlackoutRecoveryTerminal.k_codeDigits, '0');
    }
}
