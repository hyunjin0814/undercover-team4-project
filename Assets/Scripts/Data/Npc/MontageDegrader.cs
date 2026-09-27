using UnityEngine;

/// <summary>
/// 몽타주 화질 저하(RGB 블록 평균 + fade)를 계산한다. 런타임 표시와 에디터 굽기 툴이 같은 함수를 쓴다.
/// </summary>
public static class MontageDegrader
{
    public static Color[] Apply(Color[] src, int srcSize, in MontageClarityStep step)
    {
        int targetSize = Mathf.Clamp(step.PixelSize, 1, srcSize);
        int blockSize = Mathf.Max(1, Mathf.RoundToInt((float)srcSize / targetSize));
        var result = new Color[src.Length];

        for (int by = 0; by < srcSize; by += blockSize)
        {
            int blockH = Mathf.Min(blockSize, srcSize - by);
            for (int bx = 0; bx < srcSize; bx += blockSize)
            {
                int blockW = Mathf.Min(blockSize, srcSize - bx);
                Color avg = BlockAverage(src, srcSize, bx, by, blockW, blockH);
                Color faded = Fade(avg, step.Fade);

                for (int y = by; y < by + blockH; y++)
                    for (int x = bx; x < bx + blockW; x++)
                        result[y * srcSize + x] = faded;
            }
        }

        return result;
    }

    private static Color BlockAverage(Color[] src, int srcSize, int startX, int startY, int width, int height)
    {
        float r = 0f, g = 0f, b = 0f, a = 0f;
        int count = width * height;

        for (int y = startY; y < startY + height; y++)
        {
            for (int x = startX; x < startX + width; x++)
            {
                Color c = src[y * srcSize + x];
                r += c.r;
                g += c.g;
                b += c.b;
                a += c.a;
            }
        }

        return new Color(r / count, g / count, b / count, a / count);
    }

    private static Color Fade(Color c, float amount)
    {
        if (amount <= 0f)
            return c;

        const float k_gray = 0.5f;
        return new Color(
            Mathf.Lerp(c.r, k_gray, amount),
            Mathf.Lerp(c.g, k_gray, amount),
            Mathf.Lerp(c.b, k_gray, amount),
            c.a
        );
    }
}
