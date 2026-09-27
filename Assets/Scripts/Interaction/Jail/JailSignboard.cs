using TMPro;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 감옥 컨테이너 위 간판 — 현재 수감 인원을 표시한다.
/// JailZone.OnInmateCountChanged를 구독해 각 피어가 스스로 갱신한다.
/// </summary>
public class JailSignboard : MonoBehaviour
{
    [Header("감옥 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private JailZone m_jailZone;

    [Header("표시 대상 (비우면 자신·자식에서 자동 탐색)")]
    [SerializeField] private TMP_Text m_label;

    [Tooltip("표시 형식 — WorldTable/World.Jail.Signboard ({0}에 현재 수감 인원이 들어간다)")]
    [SerializeField] private LocalizedString m_format;

    private void Awake()
    {
        if (m_label == null)
            m_label = GetComponentInChildren<TMP_Text>();

        if (m_jailZone == null)
            m_jailZone = GetComponentInParent<JailZone>();
        if (m_jailZone == null)
            m_jailZone = App.Game.Jail;

        if (m_label == null)
            Debug.LogWarning("JailSignboard: 표시할 TMP_Text가 없다 — 간판이 갱신되지 않는다", this);
    }

    private void Start()
    {
        if (m_jailZone == null)
            Debug.LogWarning("JailSignboard: 감옥(JailZone)을 찾지 못했다", this);

        SetCount(m_jailZone != null ? m_jailZone.InmateCount : 0);

        m_format.StringChanged += HandleStringChanged;

        if (m_jailZone != null)
            m_jailZone.OnInmateCountChanged += Refresh;
    }

    private void OnDestroy()
    {
        if (m_jailZone != null)
            m_jailZone.OnInmateCountChanged -= Refresh;

        m_format.StringChanged -= HandleStringChanged;
    }

    private void Refresh(int count)
    {
        SetCount(count);
        m_format.RefreshString();
    }

    private void SetCount(int count) => m_format.Arguments = new object[] { count };

    private void HandleStringChanged(string text)
    {
        if (m_label != null)
            m_label.text = text;
    }
}
