using UnityEngine;

/// <summary>
/// 휴대용 미니맵 HUD 위젯을 켜고 끈다.
/// </summary>
public class PortableMinimapHud : CommonManagerBase
{
    [SerializeField]
    private GameObject m_widgetRoot;

    protected override void Awake()
    {
        base.Awake();
        SetVisible(false);
    }

    public void SetVisible(bool visible)
    {
        if (m_widgetRoot != null)
            m_widgetRoot.SetActive(visible);
    }
}
