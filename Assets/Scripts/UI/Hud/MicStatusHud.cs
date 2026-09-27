using TMPro;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 내 마이크 음소거 아이콘과, 음소거 중 무전 키를 눌렀을 때의 안내를 표시한다.
/// </summary>
public class MicStatusHud : MonoBehaviour
{
    [SerializeField]
    private GameObject m_mutedIcon;

    [Header("음소거 중 무전 시도 안내")]
    [SerializeField]
    private GameObject m_hintRoot;

    [SerializeField]
    private TextMeshProUGUI m_hintText;

    [Tooltip("음소거 안내 문구 — Hud.Mic.MutedHint ({0}=음소거 해제 키 이름)")]
    [SerializeField]
    private LocalizedString m_hintMessage;

    [Tooltip("안내가 화면에 남는 시간(초)")]
    [SerializeField]
    private float m_hintSeconds = 2f;

    private float m_hintHideTime;

    private bool m_bound;

    private VivoxManager Vivox => App.Net.Vivox;

    private void OnEnable()
    {
        GameSettings.OnMicMutedChanged += HandleMicMutedChanged;

        if (Vivox != null)
            Vivox.OnMutedTalkAttempt += ShowHint;

        HandleMicMutedChanged(GameSettings.MicMuted);
        HideHint();
    }

    private void OnDisable()
    {
        GameSettings.OnMicMutedChanged -= HandleMicMutedChanged;

        if (Vivox != null)
            Vivox.OnMutedTalkAttempt -= ShowHint;

        Unbind();
    }

    private void HandleMicMutedChanged(bool muted)
    {
        if (m_mutedIcon != null)
            m_mutedIcon.SetActive(muted);

        if (!muted)
            HideHint();
    }

    private void ShowHint()
    {
        Bind();

        if (m_hintRoot != null)
            m_hintRoot.SetActive(true);

        m_hintHideTime = Time.unscaledTime + m_hintSeconds;
    }

    private void HideHint()
    {
        Unbind();

        if (m_hintRoot != null)
            m_hintRoot.SetActive(false);
    }

    private void Bind()
    {
        if (m_hintMessage == null || m_hintMessage.IsEmpty)
        {
            Debug.LogWarning("MicStatusHud: 음소거 안내 문구가 연결되지 않았다", this);
            return;
        }

        Unbind();

        m_hintMessage.Arguments = new object[] { Vivox != null ? Vivox.MicMuteBinding : "(미할당)" };
        m_hintMessage.StringChanged += HandleHintChanged;
        m_bound = true;
    }

    private void HandleHintChanged(string localized)
    {
        if (m_hintText != null)
            m_hintText.text = localized;
    }

    private void Unbind()
    {
        if (!m_bound)
            return;

        m_hintMessage.StringChanged -= HandleHintChanged;
        m_bound = false;
    }

    private void Update()
    {
        if (m_hintRoot == null || !m_hintRoot.activeSelf)
            return;
        if (Time.unscaledTime < m_hintHideTime)
            return;

        HideHint();
    }
}
