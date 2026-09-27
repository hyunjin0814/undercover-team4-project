using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 남은 범죄자 수 / 전체 범죄자 수를 수배 리스트 기준으로 표시한다.
/// </summary>
public class RemainingCriminalsHud : MonoBehaviour
{
    private WantedListManager WantedList => App.Game.WantedList;

    [Header("표시")]
    [Tooltip("남은/전체 범죄자 수를 표시할 TextMeshProUGUI")]
    [SerializeField]
    private TextMeshProUGUI m_countText;

    [Tooltip("표시 형식 — Hud.Criminals.Count ({0}=잡은 수, {1}=수배된 진범 수(TotalWanted)). 라운드 목표는 금액이므로(#395) 이 표시는 목표 진행도가 아니라 검거 현황이다 — 목표 진행도는 본부 게시판 RoundFundBoard가 담당")]
    [SerializeField]
    private LocalizedString m_countFormat;

    private int m_lastCaught = int.MinValue;
    private int m_lastTotal = int.MinValue;

    private bool m_bound;

    private void OnEnable()
    {
        if (m_countText == null)
        {
            Debug.LogWarning("RemainingCriminalsHud: 카운트 텍스트가 연결되지 않아 표시할 수 없다", this);
            return;
        }

        if (WantedList == null)
        {
            Debug.LogWarning("RemainingCriminalsHud: WantedListManager를 찾지 못해 표시할 수 없다", this);
            SetVisible(false);
            return;
        }

        WantedList.Wanted.OnListChanged += HandleListChanged;
        WantedList.OnTotalWantedChanged += Refresh;
        WantedList.OnListReady += Refresh;

        if (WantedList.IsSpawned)
            Refresh();
        else
            SetVisible(false);
    }

    private void OnDisable()
    {
        if (WantedList != null)
        {
            WantedList.Wanted.OnListChanged -= HandleListChanged;
            WantedList.OnTotalWantedChanged -= Refresh;
            WantedList.OnListReady -= Refresh;
        }

        Unbind();
        m_lastCaught = int.MinValue;
        m_lastTotal = int.MinValue;
    }

    private void HandleListChanged(NetworkListEvent<WantedEntry> _) => Refresh();

    private void Refresh()
    {
        if (m_countText == null || WantedList == null)
            return;

        int total = WantedList.TotalWanted;
        if (total <= 0)
        {
            SetVisible(false);
            m_lastCaught = int.MinValue;
            m_lastTotal = int.MinValue;
            return;
        }

        SetVisible(true);

        int caught = total - WantedList.Wanted.Count;
        if (caught == m_lastCaught && total == m_lastTotal)
            return;

        m_lastCaught = caught;
        m_lastTotal = total;
        Bind(caught, total);
    }

    private void Bind(int caught, int total)
    {
        if (m_countFormat == null || m_countFormat.IsEmpty)
        {
            Debug.LogWarning("RemainingCriminalsHud: 검거 현황 문구가 연결되지 않았다", this);
            return;
        }

        Unbind();

        m_countFormat.Arguments = new object[] { caught, total };
        m_countFormat.StringChanged += HandleStringChanged;
        m_bound = true;
    }

    private void HandleStringChanged(string localized)
    {
        if (m_countText != null)
            m_countText.text = localized;
    }

    private void Unbind()
    {
        if (!m_bound)
            return;

        m_countFormat.StringChanged -= HandleStringChanged;
        m_bound = false;
    }

    private void SetVisible(bool visible)
    {
        if (m_countText != null && m_countText.enabled != visible)
            m_countText.enabled = visible;
    }
}
