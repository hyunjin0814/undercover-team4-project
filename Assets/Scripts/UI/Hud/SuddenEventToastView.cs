using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// SuddenEventManager.Announce를 구독해 돌발 이벤트 발생 알림을 토스트로 띄운다.
/// </summary>
public class SuddenEventToastView : MonoBehaviour
{
    [Tooltip("발생 알림 — Hud.Event.Notice ({0}=이벤트 이름)")]
    [SerializeField] private LocalizedString m_noticeMessage;

    [Tooltip("알림이 떠 있는 시간(초)")]
    [Min(0.5f)]
    [SerializeField] private float m_noticeSeconds = 3f;

    [Tooltip("알림 배경색 — 기본은 다홍. 좋은 소식이 아니라는 것이 색으로 먼저 읽혀야 한다")]
    [SerializeField] private Color m_noticeTone = new Color(0.89f, 0.26f, 0.20f, 0.95f);

    [Tooltip("아래 키로 뜨는 알림에만 쓰는 색 — 다홍 한 덩어리에서 갈라 두려는 이벤트용 (#894)")]
    [SerializeField] private Color m_altNoticeTone = new Color(0.18f, 0.62f, 0.35f, 0.95f);

    [Tooltip("m_altNoticeTone으로 띄울 문구 키 — 현재는 전자기기 장애(발생·복구) 두 줄")]
    [SerializeField] private string[] m_altToneKeys =
    {
        "Hud.Event.Notice.Blackout",
        "Hud.Event.Notice.BlackoutRecovered",
    };

    private SuddenEventManager m_manager;

    private void OnDisable() => Unbind();

    private void Update()
    {
        if (m_manager == null)
            TryBind();
    }

    private void TryBind()
    {
        SuddenEventManager manager = App.Game.SuddenEvent;
        if (manager == null)
            return;

        m_manager = manager;
        m_manager.OnEventAnnounced += HandleEventAnnounced;
    }

    private void Unbind()
    {
        if (m_manager != null)
            m_manager.OnEventAnnounced -= HandleEventAnnounced;

        m_manager = null;
    }

    private void HandleEventAnnounced(string displayName, string noticeKey)
    {
        if (string.IsNullOrEmpty(displayName) && string.IsNullOrEmpty(noticeKey))
            return;

        if (m_noticeMessage == null || m_noticeMessage.IsEmpty)
        {
            Debug.LogWarning("SuddenEventToastView: 발생 알림 문구가 연결되지 않았다", this);
            return;
        }

        if (!string.IsNullOrEmpty(noticeKey))
        {
            App.UI.Toast?.Show(
                new LocalizedString(m_noticeMessage.TableReference, noticeKey),
                m_noticeSeconds,
                ToneFor(noticeKey));
            return;
        }

        m_noticeMessage.Arguments = new object[] { displayName };

        App.UI.Toast?.Show(m_noticeMessage, m_noticeSeconds, m_noticeTone);
    }

    private Color ToneFor(string noticeKey)
    {
        if (m_altToneKeys != null)
        {
            for (int i = 0; i < m_altToneKeys.Length; i++)
            {
                if (m_altToneKeys[i] == noticeKey)
                    return m_altNoticeTone;
            }
        }

        return m_noticeTone;
    }
}
