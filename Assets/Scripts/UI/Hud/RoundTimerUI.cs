using TMPro;
using UnityEngine;

/// <summary>
/// 화면 상단에 라운드 남은 시간(mm:ss)을 표시한다. 온라인은 RoundTimerSync, 그 외는 RoundManager 값을 쓴다.
/// </summary>
public class RoundTimerUI : MonoBehaviour
{
    [Header("참조 (비우면 씬에서 자동 탐색)")]
    [SerializeField]
    private RoundTimerSync m_timerSync;

    private RoundManager Round => App.Game.Round;

    [Header("표시")]
    [Tooltip("mm:ss를 표시할 TextMeshProUGUI — 화면 중앙 상단에 앵커해 배치")]
    [SerializeField]
    private TextMeshProUGUI m_timerText;

    [Tooltip("표시 루트 — 판·테두리·숫자를 함께 켜고 끈다 (LocalizedMessageView와 같은 구조, #894)")]
    [SerializeField]
    private CanvasGroup m_group;

    private int m_lastShownSeconds = int.MinValue;

    private void Awake()
    {
        if (m_timerSync == null)
            m_timerSync = FindFirstObjectByType<RoundTimerSync>();

        if (m_timerText == null)
            Debug.LogWarning("RoundTimerUI: 타이머 텍스트가 연결되지 않아 표시할 수 없다", this);

        SetVisible(false);
    }

    private void Update()
    {
        if (m_timerText == null)
            return;

        if (!TryGetRemainingSeconds(out float remaining))
        {
            SetVisible(false);
            m_lastShownSeconds = int.MinValue;
            return;
        }

        SetVisible(true);

        int shown = Mathf.CeilToInt(remaining);
        if (shown == m_lastShownSeconds)
            return;

        m_lastShownSeconds = shown;
        m_timerText.text = $"{shown / 60:00}:{shown % 60:00}";
    }

    private bool TryGetRemainingSeconds(out float seconds)
    {
        if (m_timerSync != null && m_timerSync.TryGetRemainingSeconds(out seconds))
            return true;

        seconds = 0f;
        if (
            Round == null
            || !Round.IsPhaseAuthority
            || Round.Phase != RoundPhase.InProgress
            || float.IsPositiveInfinity(Round.RemainingSeconds)
        )
            return false;

        seconds = Mathf.Max(0f, Round.RemainingSeconds);
        return true;
    }

    private void SetVisible(bool visible)
    {
        if (m_group == null)
            return;

        if (m_group.gameObject.activeSelf != visible)
            m_group.gameObject.SetActive(visible);
    }
}
