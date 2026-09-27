using TMPro;
using UnityEngine;
using UnityEngine.UI;

public enum ETeamMemberState
{
    Alive,
    Abducted,
    Dead,
}

/// <summary>
/// 팀 상황판의 대원 카드 — 얼굴·이름·체력 게이지(HpBarView)·상태를 표시한다.
/// </summary>
public class TeamStatusRowView : MonoBehaviour
{
    private const string k_table = "HudTable";

    private const ETeamMemberState k_noState = (ETeamMemberState)(-1);

    [Tooltip("대원 얼굴 — 상점에서 구운 것을 그대로 쓴다 (#598 · #863)")]
    [SerializeField] private RawImage m_portrait;

    [SerializeField] private TextMeshProUGUI m_nameText;

    [Tooltip("HP 게이지 — 좌하단 기름통 뷰를 그대로 쓴다. 숫자(72/100)도 이 뷰가 통 안에 그린다")]
    [SerializeField] private HpBarView m_gauge;

    [SerializeField] private TextMeshProUGUI m_stateText;

    [Header("나 / 팀원")]
    [Tooltip("내 카드는 늘 첫 칸이지만(TeamStatusPanel) 자리만으로는 읽히지 않아 칩으로 못 박는다 (#894)")]
    [SerializeField] private TextMeshProUGUI m_ownerText;

    [Tooltip("위 문구가 앉는 칩 — 비워도 된다")]
    [SerializeField] private Image m_ownerPlate;

    [SerializeField] private Color m_minePlateTone = new Color(0.878f, 0.663f, 0.290f, 1f);
    [SerializeField] private Color m_mineTextTone = new Color(0.06f, 0.08f, 0.12f, 1f);
    [SerializeField] private Color m_matePlateTone = new Color(0.24f, 0.29f, 0.36f, 0.9f);
    [SerializeField] private Color m_mateTextTone = new Color(0.82f, 0.86f, 0.91f, 1f);

    [Header("상태 색")]
    [SerializeField] private Color m_aliveTone = new Color(0.85f, 0.92f, 0.95f, 1f);
    [SerializeField] private Color m_abductedTone = new Color(0.95f, 0.72f, 0.25f, 1f);
    [SerializeField] private Color m_deadTone = new Color(0.85f, 0.30f, 0.30f, 1f);

    private ETeamMemberState m_shownState = k_noState;

    private string m_shownName;

    private int m_shownOwner = -1;

    public bool HasName => !string.IsNullOrEmpty(m_shownName);

    private void OnEnable()
    {
        m_shownState = k_noState;
        m_shownName = null;
        m_shownOwner = -1;
    }

    public bool HasPortrait => m_portrait != null && m_portrait.texture != null;

    /// <summary>얼굴을 넣는다 — 사람마다 고른 색·치장으로 상점에서 구운 그림이다.</summary>
    public void SetPortrait(Texture portrait)
    {
        if (m_portrait == null)
            return;

        m_portrait.texture = portrait;
        m_portrait.enabled = portrait != null;
    }

    /// <summary>이름을 넣는다. 같은 값이면 갱신하지 않는다.</summary>
    public void SetName(string displayName)
    {
        if (m_nameText == null || displayName == m_shownName)
            return;

        m_shownName = displayName;
        m_nameText.text = displayName;
    }

    /// <summary>이 카드가 내 것인지 표시한다.</summary>
    public void SetOwnership(bool isMine)
    {
        int owner = isMine ? 1 : 0;
        if (owner == m_shownOwner)
            return;

        m_shownOwner = owner;

        if (m_ownerText != null)
        {
            m_ownerText.text = LocalizedStrings.Get(k_table, isMine ? "Hud.Team.Owner.Me" : "Hud.Team.Owner.Mate");
            m_ownerText.color = isMine ? m_mineTextTone : m_mateTextTone;
        }

        if (m_ownerPlate != null)
            m_ownerPlate.color = isMine ? m_minePlateTone : m_matePlateTone;
    }

    /// <summary>매 프레임 값만 갈아 끼운다 — 상황판이 떠 있는 동안만 불린다.</summary>
    public void SetStatus(int hp, int maxHp, ETeamMemberState state)
    {
        if (m_gauge != null)
            m_gauge.SetHealth(hp, maxHp);

        if (m_stateText == null || state == m_shownState)
            return;

        m_shownState = state;
        m_stateText.text = LocalizedStrings.Get(k_table, StateKey(state));
        m_stateText.color = StateTone(state);
    }

    private static string StateKey(ETeamMemberState state)
    {
        switch (state)
        {
            case ETeamMemberState.Dead:
                return "Hud.Team.State.Dead";
            case ETeamMemberState.Abducted:
                return "Hud.Team.State.Abducted";
            default:
                return "Hud.Team.State.Alive";
        }
    }

    private Color StateTone(ETeamMemberState state)
    {
        switch (state)
        {
            case ETeamMemberState.Dead:
                return m_deadTone;
            case ETeamMemberState.Abducted:
                return m_abductedTone;
            default:
                return m_aliveTone;
        }
    }
}
