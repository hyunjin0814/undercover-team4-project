using TMPro;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 라운드 목표 금액 진행도(유치장 현상금 합 / 목표 금액)를 표시하는 본부 게시판.
/// </summary>
public class RoundFundBoard : MonoBehaviour
{
    private RoundManager Round => App.Game.Round;

    [Header("표시")]
    [Tooltip("목표 진행도를 표시할 TextMeshPro (UGUI, 3D)")]
    [SerializeField] private TMP_Text m_fundText;

    [Tooltip("표시 형식 — Hud.Round.FundProgress ({0}=지금 벌어둔 금액, {1}=목표 금액)")]
    [SerializeField] private LocalizedString m_fundFormat;

    [Tooltip("목표를 채웠을 때 입힐 색 — 본부 종료 버튼이 켜졌다는 신호와 같은 의미다")]
    [SerializeField] private Color m_metColor = new Color(0.36f, 0.85f, 0.44f);

    private Color m_defaultColor;
    private bool m_defaultColorCached;

    private int m_lastCurrent = int.MinValue;
    private int m_lastTarget = int.MinValue;

    private JailZone m_jail;

    private bool m_bound;

    private void OnEnable()
    {
        if (m_fundText == null)
        {
            Debug.LogWarning("RoundFundBoard: 금액 텍스트가 연결되지 않아 표시할 수 없다", this);
            return;
        }

        if (!m_defaultColorCached)
        {
            m_defaultColor = m_fundText.color;
            m_defaultColorCached = true;
        }

        m_jail = App.Game.Jail;
        if (m_jail != null)
            m_jail.OnBountyTotalChanged += HandleBountyChanged;

        Refresh();
    }

    private void OnDisable()
    {
        if (m_jail != null)
            m_jail.OnBountyTotalChanged -= HandleBountyChanged;
        m_jail = null;

        Unbind();
        m_lastCurrent = int.MinValue;
        m_lastTarget = int.MinValue;
    }

    private void HandleBountyChanged(int _) => Refresh();

    private void Refresh()
    {
        if (m_fundText == null)
            return;

        RoundManager round = Round;
        if (round == null)
        {
            SetVisible(false);
            return;
        }

        int target = round.TargetFund;
        if (target <= 0)
        {
            SetVisible(false);
            m_lastCurrent = int.MinValue;
            m_lastTarget = int.MinValue;
            return;
        }

        SetVisible(true);

        int current = round.CurrentFund;
        if (current == m_lastCurrent && target == m_lastTarget)
            return;

        m_lastCurrent = current;
        m_lastTarget = target;
        Bind(current, target);
        m_fundText.color = current >= target ? m_metColor : m_defaultColor;
    }

    private void Bind(int current, int target)
    {
        if (m_fundFormat == null || m_fundFormat.IsEmpty)
        {
            Debug.LogWarning("RoundFundBoard: 금액 문구가 연결되지 않았다", this);
            return;
        }

        Unbind();

        m_fundFormat.Arguments = new object[] { current, target };
        m_fundFormat.StringChanged += HandleStringChanged;
        m_bound = true;
    }

    private void HandleStringChanged(string localized)
    {
        if (m_fundText != null)
            m_fundText.text = localized;
    }

    private void Unbind()
    {
        if (!m_bound)
            return;

        m_fundFormat.StringChanged -= HandleStringChanged;
        m_bound = false;
    }

    private void SetVisible(bool visible)
    {
        if (m_fundText != null && m_fundText.enabled != visible)
            m_fundText.enabled = visible;
    }
}
