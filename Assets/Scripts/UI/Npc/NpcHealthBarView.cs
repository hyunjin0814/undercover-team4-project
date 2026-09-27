using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// NPC 머리 위 월드공간 체력 바 — 조준 중에만 켜지고, 보이는 동안 동기화된 체력을 폴링해 그린다.
/// </summary>
public class NpcHealthBarView : NpcWorldCard
{
    [Header("게이지")]
    [Tooltip("체력 바 — Handle 없는 Slider(Fill Rect만 사용)로 채움 비율을 표현한다")]
    [SerializeField]
    private Slider m_slider;

    private NpcController m_controller;

    protected override void Awake()
    {
        base.Awake();
        m_controller = GetComponentInParent<NpcController>();

        if (m_controller == null)
            Debug.LogWarning("NpcHealthBarView: NpcController를 찾지 못해 체력을 읽을 수 없다", this);
    }

    /// <summary>바를 켠다 — 조준이 시작될 때 프레젠터가 부른다.</summary>
    public void Show()
    {
        Refresh();
        SetCardActive(true);
    }

    protected override void LateUpdate()
    {
        base.LateUpdate();
        Refresh();
    }

    private void Refresh()
    {
        if (m_slider == null || m_controller == null)
            return;

        int max = m_controller.Health.MaxHp;

        m_slider.value = max > 0 ? Mathf.Clamp01((float)m_controller.Health.CurrentHp / max) : 0f;
    }
}
