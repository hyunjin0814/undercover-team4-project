using TMPro;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 아직 준비 보고를 하지 않은 동료가 있는 동안 "대기 중 (2/4)"를 표시한다.
/// </summary>
public class ReadyWaitHud : MonoBehaviour
{
    private SceneReadyGate Gate => App.Game.ReadyGate;

    [Header("표시")]
    [Tooltip("대기 상태를 표시할 TextMeshProUGUI")]
    [SerializeField]
    private TextMeshProUGUI m_waitText;

    [Tooltip("표시 루트 — 판·테두리·문구를 함께 켜고 끈다 (LocalizedMessageView와 같은 구조, #894)")]
    [SerializeField]
    private CanvasGroup m_group;

    [Tooltip("표시 형식 — Hud.Ready.Waiting ({0}=준비된 인원, {1}=전체 인원)")]
    [SerializeField]
    private LocalizedString m_waitFormat;

    private int m_lastReady = int.MinValue;
    private int m_lastExpected = int.MinValue;

    private bool m_bound;

    private void OnEnable()
    {
        if (m_waitText == null)
        {
            Debug.LogWarning("[ReadyWaitHud] 대기 텍스트가 연결되지 않아 표시할 수 없다.", this);
            return;
        }

        SetVisible(false);
    }

    private void OnDisable()
    {
        Unbind();
        m_lastReady = int.MinValue;
        m_lastExpected = int.MinValue;
    }

    private void Update()
    {
        SceneReadyGate gate = Gate;

        if (gate == null || !gate.IsSpawned || gate.IsOpen || gate.ExpectedCount <= 0)
        {
            SetVisible(false);
            return;
        }

        SetVisible(true);

        int ready = gate.ReadyCount;
        int expected = gate.ExpectedCount;
        if (ready == m_lastReady && expected == m_lastExpected)
            return;

        m_lastReady = ready;
        m_lastExpected = expected;
        Bind(ready, expected);
    }

    private void Bind(int ready, int expected)
    {
        if (m_waitFormat == null || m_waitFormat.IsEmpty)
        {
            Debug.LogWarning("[ReadyWaitHud] 대기 문구가 연결되지 않았다.", this);
            return;
        }

        Unbind();

        m_waitFormat.Arguments = new object[] { ready, expected };
        m_waitFormat.StringChanged += HandleStringChanged;
        m_bound = true;
    }

    private void HandleStringChanged(string localized)
    {
        if (m_waitText != null)
            m_waitText.text = localized;
    }

    private void Unbind()
    {
        if (!m_bound)
            return;

        m_waitFormat.StringChanged -= HandleStringChanged;
        m_bound = false;
    }

    private void SetVisible(bool visible)
    {
        if (m_group == null)
            return;

        if (m_group.gameObject.activeSelf != visible)
            m_group.gameObject.SetActive(visible);
    }
}
