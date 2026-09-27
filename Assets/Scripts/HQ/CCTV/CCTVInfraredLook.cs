using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// CCTV 야시경 룩 적용 — 상태 없는 정적 헬퍼, URP 심볼을 여기에만 가둔다.
/// </summary>
public static class CCTVInfraredLook
{
    /// <summary>포스트 프로세싱 on/off — 실제 룩은 전용 Volume 프로필이 낸다.</summary>
    public static void Apply(Camera camera, bool on)
    {
        UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
        if (data != null)
            data.renderPostProcessing = on;
    }

    /// <summary>IR 볼륨 레이어를 이 카메라의 Volume Layer Mask에 영구 포함시킨다 (캐싱 시 1회).</summary>
    public static void SetVolumeLayers(Camera camera, LayerMask layers)
    {
        UniversalAdditionalCameraData data = camera.GetUniversalAdditionalCameraData();
        if (data != null)
            data.volumeLayerMask = layers;
    }

    private static readonly int s_emissionMapId = Shader.PropertyToID("_EmissionMap");
    private static readonly int s_emissionColorId = Shader.PropertyToID("_EmissionColor");
    private static MaterialPropertyBlock s_monitorBlock;

    public static void ApplyMonitorEmission(
        Renderer monitorRenderer,
        bool displaying,
        bool infrared,
        Texture content,
        Color backlightColor,
        Color infraredColor
    )
    {
        if (monitorRenderer == null)
            return;

        s_monitorBlock ??= new MaterialPropertyBlock();
        monitorRenderer.GetPropertyBlock(s_monitorBlock);

        if (!displaying)
        {
            s_monitorBlock.SetColor(s_emissionColorId, Color.black);
        }
        else if (infrared)
        {
            s_monitorBlock.SetTexture(s_emissionMapId, content);
            s_monitorBlock.SetColor(s_emissionColorId, infraredColor);
        }
        else
        {
            s_monitorBlock.SetTexture(s_emissionMapId, Texture2D.whiteTexture);
            s_monitorBlock.SetColor(s_emissionColorId, backlightColor);
        }

        monitorRenderer.SetPropertyBlock(s_monitorBlock);
    }
}
