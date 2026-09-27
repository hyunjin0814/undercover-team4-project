using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 세션 입퇴장 알림을 토스트로 띄우는 상주 컴포넌트. 토스트가 없는 씬에서는 무동작한다.
/// </summary>
public class PlayerPresenceToastView : MonoBehaviour
{
    [Tooltip("입장 알림 — Session.Presence.Joined ({0}=닉네임)")]
    [SerializeField] private LocalizedString m_joinedToast;

    [Tooltip("퇴장 알림 — Session.Presence.Left ({0}=닉네임). 조사(이/가)를 피하려 \"님이\"로 적는다 (#598)")]
    [SerializeField] private LocalizedString m_leftToast;

    [Tooltip("알림이 떠 있는 시간(초)")]
    [Min(0.5f)]
    [SerializeField] private float m_toastSeconds = 3f;

    [Tooltip("알림 배경색 — 검거 알림(ArrestNoticeBroadcaster)과 같은 공용 팔레트를 쓴다 (#951)")]
    [SerializeField] private UiColorPalette m_palette;

    [Tooltip("알림 배경 채움 투명도")]
    [Range(0f, 1f)]
    [SerializeField] private float m_toastAlpha = 0.95f;

    private SessionRoster m_roster;

    private void OnDisable() => Unbind();

    private void Update()
    {
        if (m_roster == null)
            TryBind();
    }

    private void TryBind()
    {
        SessionRoster roster = App.Game.Roster;
        if (roster == null)
            return;

        m_roster = roster;
        m_roster.OnPlayerJoined += HandlePlayerJoined;
        m_roster.OnPlayerLeft += HandlePlayerLeft;
    }

    private void Unbind()
    {
        if (m_roster != null)
        {
            m_roster.OnPlayerJoined -= HandlePlayerJoined;
            m_roster.OnPlayerLeft -= HandlePlayerLeft;
        }
        m_roster = null;
    }

    private void HandlePlayerJoined(string nickname) =>
        ShowToast(m_joinedToast, nickname, Tone(true));

    private void HandlePlayerLeft(string nickname) =>
        ShowToast(m_leftToast, nickname, Tone(false));

    private Color? Tone(bool joined)
    {
        if (m_palette == null)
            return null;

        return UiColorPalette.WithAlpha(
            joined ? m_palette.Positive : m_palette.Neutral,
            m_toastAlpha
        );
    }

    private void ShowToast(LocalizedString message, string nickname, Color? tone)
    {
        if (string.IsNullOrEmpty(nickname))
            return;

        if (message == null || message.IsEmpty)
        {
            Debug.LogWarning("PlayerPresenceToastView: 알림 문구가 연결되지 않았습니다.", this);
            return;
        }

        message.Arguments = new object[] { nickname };

        App.UI.Toast?.Show(message, m_toastSeconds, tone);
    }
}
