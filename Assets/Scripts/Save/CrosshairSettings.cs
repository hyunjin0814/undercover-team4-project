using System;

/// <summary>
/// 크로스헤어 커스터마이징 값 한 벌 — 계정 클라우드 저장 경로로 저장된다.
/// </summary>
[Serializable]
public class CrosshairSettings
{
    public ECrosshairShape Shape;

    public int ColorIndex;

    public float Size;

    public float Thickness;

    public static CrosshairSettings Default() =>
        new CrosshairSettings
        {
            Shape = ECrosshairShape.Cross,
            ColorIndex = 0,
            Size = 8f,
            Thickness = 2f,
        };
}
