using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 화면 전체를 덮는 HUD 오버레이 이미지의 공통 처리(DamageVignetteUI·TaserShockUI 공용).
/// </summary>
public static class OverlayImage
{
    /// <summary>이미지 알파를 적용하고, 0이면 오브젝트를 비활성화한다.</summary>
    public static void SetAlpha(Image image, float alpha)
    {
        if (image == null)
            return;

        bool visible = alpha > 0.001f;
        if (image.gameObject.activeSelf != visible)
            image.gameObject.SetActive(visible);

        if (!visible)
            return;

        Color color = image.color;
        color.a = alpha;
        image.color = color;
    }
}
