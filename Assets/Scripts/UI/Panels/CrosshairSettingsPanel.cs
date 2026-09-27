using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 크로스헤어 커스터마이징 창 — 모양·색·크기·굵기를 즉시 저장하고 미리보기를 다시 그린다.
/// </summary>
public class CrosshairSettingsPanel : PanelBase
{
    [Header("모양")]
    [SerializeField] private Toggle m_shapeCrossToggle;
    [SerializeField] private Toggle m_shapeDotToggle;
    [SerializeField] private Toggle m_shapeCrossDotToggle;
    [SerializeField] private Toggle m_shapeCircleToggle;

    [Header("색")]
    [Tooltip("옵션(텍스트+색 아이콘)은 프리팹에 CrosshairColorPalette 순서대로 미리 채워 둔다")]
    [SerializeField] private TMP_Dropdown m_colorDropdown;

    [Header("크기·굵기")]
    [SerializeField] private Slider m_sizeSlider;
    [SerializeField] private Slider m_thicknessSlider;

    [Header("미리보기")]
    [SerializeField] private CrosshairPreviewView m_preview;

    [SerializeField] private Button m_closeButton;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    protected override void Awake()
    {
        base.Awake();

        if (m_closeButton != null)
            m_closeButton.onClick.AddListener(ClosePanel);

        m_sizeSlider.minValue = CrosshairRenderer.k_minSize;
        m_sizeSlider.maxValue = CrosshairRenderer.k_maxSize;
        m_sizeSlider.wholeNumbers = false;
        m_sizeSlider.onValueChanged.AddListener(HandleSizeChanged);

        m_thicknessSlider.minValue = CrosshairRenderer.k_minThickness;
        m_thicknessSlider.maxValue = CrosshairRenderer.k_maxThickness;
        m_thicknessSlider.wholeNumbers = false;
        m_thicknessSlider.onValueChanged.AddListener(HandleThicknessChanged);

        m_shapeCrossToggle.onValueChanged.AddListener(isOn => { if (isOn) HandleShapeChanged(ECrosshairShape.Cross); });
        m_shapeDotToggle.onValueChanged.AddListener(isOn => { if (isOn) HandleShapeChanged(ECrosshairShape.Dot); });
        m_shapeCrossDotToggle.onValueChanged.AddListener(isOn => { if (isOn) HandleShapeChanged(ECrosshairShape.CrossDot); });
        m_shapeCircleToggle.onValueChanged.AddListener(isOn => { if (isOn) HandleShapeChanged(ECrosshairShape.Circle); });

        m_colorDropdown.onValueChanged.AddListener(HandleColorChanged);
    }

    public override void OpenPanel()
    {
        base.OpenPanel();
        SyncFromSettings();
    }

    private void SyncFromSettings()
    {
        CrosshairSettings settings = CosmeticLoadout.GetCrosshairSettings();

        m_shapeCrossToggle.SetIsOnWithoutNotify(settings.Shape == ECrosshairShape.Cross);
        m_shapeDotToggle.SetIsOnWithoutNotify(settings.Shape == ECrosshairShape.Dot);
        m_shapeCrossDotToggle.SetIsOnWithoutNotify(settings.Shape == ECrosshairShape.CrossDot);
        m_shapeCircleToggle.SetIsOnWithoutNotify(settings.Shape == ECrosshairShape.Circle);

        m_sizeSlider.SetValueWithoutNotify(settings.Size);
        m_thicknessSlider.SetValueWithoutNotify(settings.Thickness);

        m_colorDropdown.SetValueWithoutNotify(settings.ColorIndex);
        m_colorDropdown.RefreshShownValue();

        RefreshPreview(settings);
    }

    private void HandleShapeChanged(ECrosshairShape shape) => ApplyChange(s => s.Shape = shape);
    private void HandleColorChanged(int index) => ApplyChange(s => s.ColorIndex = index);
    private void HandleSizeChanged(float value) => ApplyChange(s => s.Size = value);
    private void HandleThicknessChanged(float value) => ApplyChange(s => s.Thickness = value);

    private void ApplyChange(System.Action<CrosshairSettings> mutate)
    {
        CrosshairSettings settings = CosmeticLoadout.GetCrosshairSettings();
        CrosshairSettings next = new CrosshairSettings
        {
            Shape = settings.Shape,
            ColorIndex = settings.ColorIndex,
            Size = settings.Size,
            Thickness = settings.Thickness,
        };
        mutate(next);

        CosmeticLoadout.SetCrosshairSettings(next);

        RefreshPreview(next);
    }

    private void RefreshPreview(CrosshairSettings settings)
    {
        if (m_preview != null)
            m_preview.Refresh(settings);
    }
}
