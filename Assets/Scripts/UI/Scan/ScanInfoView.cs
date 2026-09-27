using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// NPC 머리 위 월드공간 스캔 정보 카드 — 각 클라가 로컬로 실제 값 또는 ??를 표시한다.
/// </summary>
public class ScanInfoView : NpcWorldCard
{
    [Header("배경")]
    [Tooltip("카드 배경 — 마스킹 여부에 따라 색이 바뀐다(미스캔 시 반투명 노이즈로 마스킹)")]
    [SerializeField]
    private Image m_background;

    [SerializeField]
    private Color m_maskedColor = Color.black;

    [SerializeField]
    private Color m_scannedColor = new Color(0f, 0.05f, 0.08f, 0.72f);

    [Header("마스킹")]
    [Tooltip("미스캔 시 뜨는 노이즈 오버레이 — 스캔 완료 시 꺼진다")]
    [SerializeField]
    private GameObject m_maskedRoot;

    [Tooltip("이름+아이콘 컨테이너 — 스캔 완료 시에만 켜진다")]
    [SerializeField]
    private GameObject m_contentRoot;

    [Header("텍스트")]
    [SerializeField]
    private TMP_Text m_nameText;

    [Header("타입 아이콘")]
    [Tooltip("스캔된 NPC의 타입(인간/안드로이드) 아이콘")]
    [SerializeField]
    private Image m_typeIcon;

    [SerializeField]
    private Sprite m_humanIcon;

    [SerializeField]
    private Sprite m_androidIcon;

    [Header("생사 상태 아이콘")]
    [SerializeField]
    private Image m_statusIcon;

    [SerializeField]
    private Sprite m_aliveIcon;

    [SerializeField]
    private Sprite m_deadIcon;

    private const string k_hudTable = "HudTable";
    private const string k_nameKey = "Hud.Scan.FieldName";

    /// <summary>스캔한 NPC의 실제 이름과 타입·생사 아이콘을 표시하고 카드를 켠다.</summary>
    public void ShowReal(string citizenName, OfficialRecords.CitizenType typeView, bool isDead)
    {
        m_nameText.text = LocalizedStrings.Get(k_hudTable, k_nameKey, citizenName);
        SetTypeIcon(typeView);
        SetStatusIcon(isDead);
        SetMasked(false);
        SetCardActive(true);
    }

    /// <summary>미스캔 NPC — 카드를 노이즈로 마스킹한다(이름·아이콘은 통째로 숨김).</summary>
    public void ShowMasked()
    {
        SetMasked(true);
        SetCardActive(true);
    }

    private void SetMasked(bool masked)
    {
        if (m_background != null)
            m_background.color = masked ? m_maskedColor : m_scannedColor;

        if (m_maskedRoot != null)
            m_maskedRoot.SetActive(masked);

        if (m_contentRoot != null)
            m_contentRoot.SetActive(!masked);
    }

    private void SetTypeIcon(OfficialRecords.CitizenType type)
    {
        if (m_typeIcon == null) return;

        Sprite sprite = type == OfficialRecords.CitizenType.Android ? m_androidIcon : m_humanIcon;
        m_typeIcon.sprite = sprite;
        m_typeIcon.enabled = sprite != null;
    }

    private void SetStatusIcon(bool isDead)
    {
        if (m_statusIcon == null) return;

        Sprite sprite = isDead ? m_deadIcon : m_aliveIcon;
        m_statusIcon.sprite = sprite;
        m_statusIcon.enabled = sprite != null;
    }
}
