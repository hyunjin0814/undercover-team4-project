using TMPro;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 로비 접속자 목록의 한 행 — 닉네임·방장·발화·음소거 아이콘을 표시한다.
/// </summary>
public class LobbyRosterRowView : MonoBehaviour
{
    [SerializeField]
    private TextMeshProUGUI m_nicknameText;

    [SerializeField]
    private GameObject m_hostIcon;

    [SerializeField]
    private GameObject m_speakerIcon;

    [SerializeField]
    private GameObject m_micMutedIcon;

    [Tooltip("카드 왼쪽 얼굴 — LobbyPortraitStage가 구운 텍스처를 받는다 (#598)")]
    [SerializeField]
    private UnityEngine.UI.RawImage m_portrait;

    [Header("문구 (LobbyTable)")]
    [Tooltip("닉네임 보고가 아직 안 닿음 — Lobby.Roster.Connecting")]
    [SerializeField]
    private LocalizedString m_connectingLabel;

    [Tooltip("빈 자리 — Lobby.Roster.Empty")]
    [SerializeField]
    private LocalizedString m_emptyLabel;

    private LocalizedString m_boundLabel;

    private bool m_speaking;
    private bool m_micMuted;

    public string PlayerId { get; private set; } = string.Empty;

    public void Bind(LobbyPlayerEntry entry, bool isHost)
    {
        PlayerId = entry.PlayerId.ToString();

        if (entry.Nickname.IsEmpty)
        {
            BindLabel(m_connectingLabel);
        }
        else
        {
            UnbindLabel();
            m_nicknameText.text = entry.Nickname.ToString();
        }

        SetHost(isHost);
        SetMicMuted(entry.MicMuted);
        SetSpeaking(false);
    }

    /// <summary>정원까지 남은 자리 — 몇 명 더 들어올 수 있는지 보이게 한다.</summary>
    public void BindEmpty()
    {
        PlayerId = string.Empty;
        BindLabel(m_emptyLabel);
        SetPortrait(null);
        SetHost(false);
        SetMicMuted(false);
        SetSpeaking(false);
    }

    /// <summary>카드 얼굴 텍스처를 건다. null이면 얼굴 칸을 숨긴다.</summary>
    public void SetPortrait(Texture portrait)
    {
        if (m_portrait == null)
            return;

        m_portrait.texture = portrait;
        m_portrait.gameObject.SetActive(portrait != null);
    }

    public void SetSpeaking(bool on)
    {
        m_speaking = on;

        if (m_speakerIcon != null)
            m_speakerIcon.SetActive(on && !m_micMuted);
    }

    public void SetMicMuted(bool on)
    {
        m_micMuted = on;

        if (m_micMutedIcon != null)
            m_micMutedIcon.SetActive(on);

        SetSpeaking(m_speaking);
    }

    private void SetHost(bool on)
    {
        if (m_hostIcon != null)
            m_hostIcon.SetActive(on);
    }

    /// <summary>안내 문구를 교체하고 언어 변경을 구독한다.</summary>
    private void BindLabel(LocalizedString label)
    {
        if (m_nicknameText == null)
            return;

        if (label == null || label.IsEmpty)
        {
            Debug.LogWarning($"[{nameof(LobbyRosterRowView)}] 로스터 행 안내 문구가 연결되지 않았습니다.", this);
            return;
        }

        UnbindLabel();

        m_boundLabel = label;
        m_boundLabel.StringChanged += HandleLabelChanged;
    }

    private void HandleLabelChanged(string localized)
    {
        if (m_nicknameText != null)
            m_nicknameText.text = localized;
    }

    private void UnbindLabel()
    {
        if (m_boundLabel == null)
            return;

        m_boundLabel.StringChanged -= HandleLabelChanged;
        m_boundLabel = null;
    }

    private void OnDestroy() => UnbindLabel();
}
