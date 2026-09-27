using UnityEngine;
using UnityEngine.UI;

public struct CrosshairVisualRefs
{
    public RectTransform Up;
    public RectTransform Down;
    public RectTransform Left;
    public RectTransform Right;
    public RectTransform Dot;
    public RectTransform CircleRing;
    public Image CircleImage;
}

/// <summary>
/// 크로스헤어 모양·크기·굵기·색 계산 정적 함수 모음 — 게임 HUD와 설정 미리보기가 공유한다.
/// </summary>
public static class CrosshairRenderer
{
    public const float k_minSize = 4f;
    public const float k_maxSize = 24f;
    public const float k_minThickness = 1f;
    public const float k_maxThickness = 6f;

    private const float k_lineGap = 4f;

    /// <summary>모양·크기·굵기를 반영해 각 조각의 표시 여부·RectTransform 크기를 다시 잡는다.</summary>
    public static void ApplyShape(CrosshairVisualRefs refs, CrosshairSettings settings)
    {
        float size = Mathf.Clamp(settings.Size, k_minSize, k_maxSize);
        float thickness = Mathf.Clamp(settings.Thickness, k_minThickness, k_maxThickness);

        bool showLines =
            settings.Shape == ECrosshairShape.Cross || settings.Shape == ECrosshairShape.CrossDot;
        bool showDot =
            settings.Shape == ECrosshairShape.Dot || settings.Shape == ECrosshairShape.CrossDot;
        bool showCircle = settings.Shape == ECrosshairShape.Circle;

        SetLine(refs.Up, showLines, vertical: true, direction: 1f, size, thickness, k_lineGap);
        SetLine(refs.Down, showLines, vertical: true, direction: -1f, size, thickness, k_lineGap);
        SetLine(refs.Left, showLines, vertical: false, direction: -1f, size, thickness, k_lineGap);
        SetLine(refs.Right, showLines, vertical: false, direction: 1f, size, thickness, k_lineGap);

        if (refs.Dot != null)
        {
            refs.Dot.gameObject.SetActive(showDot);
            if (showDot)
                refs.Dot.sizeDelta = new Vector2(size + thickness * 2f, size + thickness * 2f);
        }

        if (refs.CircleRing != null)
        {
            refs.CircleRing.gameObject.SetActive(showCircle);
            if (showCircle)
                refs.CircleRing.sizeDelta = new Vector2(size * 2f, size * 2f);
        }
    }

    private static void SetLine(
        RectTransform line,
        bool visible,
        bool vertical,
        float direction,
        float size,
        float thickness,
        float gap
    )
    {
        if (line == null)
            return;

        line.gameObject.SetActive(visible);
        if (!visible)
            return;

        line.sizeDelta = vertical ? new Vector2(thickness, size) : new Vector2(size, thickness);

        float offset = gap + size * 0.5f;
        line.anchoredPosition = vertical
            ? new Vector2(0f, direction * offset)
            : new Vector2(direction * offset, 0f);
    }

    /// <summary>보이는 크로스헤어 조각에 색을 칠한다. colorOverride가 없으면 설정 팔레트 색을 쓴다.</summary>
    public static void ApplyColor(
        CrosshairVisualRefs refs,
        CrosshairSettings settings,
        Color? colorOverride,
        PlayerColorPalette palette
    )
    {
        Color color =
            colorOverride ?? (palette != null ? palette.Get(settings.ColorIndex) : Color.white);

        SetGraphicColor(refs.Up, color);
        SetGraphicColor(refs.Down, color);
        SetGraphicColor(refs.Left, color);
        SetGraphicColor(refs.Right, color);
        SetGraphicColor(refs.Dot, color);

        if (refs.CircleImage != null)
            refs.CircleImage.color = color;
    }

    private static void SetGraphicColor(RectTransform target, Color color)
    {
        if (target == null)
            return;

        Graphic graphic = target.GetComponent<Graphic>();
        if (graphic != null)
            graphic.color = color;
    }
}
