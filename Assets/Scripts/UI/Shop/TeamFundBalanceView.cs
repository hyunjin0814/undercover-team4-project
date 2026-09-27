using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// 상점에서 팀 자금 잔액을 표시한다. 값이 바뀔 때만 갱신한다.
/// </summary>
public class TeamFundBalanceView : MonoBehaviour
{
    [Header("표시")]
    [Tooltip("잔액을 표시할 TextMeshPro (UGUI, 3D)")]
    [SerializeField] private TMP_Text m_balanceText;

    private const string k_commonTable = "CommonTable";
    private const string k_moneyKey = "Common.Unit.Money";

    private TeamFund m_teamFund;

    private void OnEnable()
    {
        if (m_balanceText == null)
        {
            Debug.LogWarning("TeamFundBalanceView: 잔액 텍스트가 연결되지 않아 표시할 수 없다", this);
            return;
        }

        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        TryBind();
    }

    private void OnDisable()
    {
        if (m_teamFund != null)
            m_teamFund.Fund.OnValueChanged -= HandleFundChanged;
        m_teamFund = null;

        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
    }

    private void HandleLocaleChanged(Locale locale)
    {
        if (m_teamFund != null)
            Refresh(m_teamFund.Balance);
    }

    private void Update()
    {
        if (m_teamFund == null && m_balanceText != null)
            TryBind();
    }

    private void TryBind()
    {
        TeamFund fund = App.Game.TeamFund;
        if (fund == null || !fund.IsSpawned)
        {
            SetVisible(false);
            return;
        }

        m_teamFund = fund;
        m_teamFund.Fund.OnValueChanged += HandleFundChanged;

        SetVisible(true);
        Refresh(m_teamFund.Balance);
    }

    private void HandleFundChanged(int previous, int current) => Refresh(current);

    private void Refresh(int balance) =>
        m_balanceText.text = LocalizedStrings.Get(k_commonTable, k_moneyKey, balance);

    private void SetVisible(bool visible)
    {
        if (m_balanceText.enabled != visible)
            m_balanceText.enabled = visible;
    }
}
