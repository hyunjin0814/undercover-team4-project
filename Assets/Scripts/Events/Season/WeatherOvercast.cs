using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 먹구름 표현 — 씬 태양(RenderSettings.sun)을 어둡게 하는 단일 창구.
/// 요청을 참조 계수로 세어 가장 어두운 값을 적용하고, 마지막 요청이 빠질 때 원래 밝기로 되돌린다.
/// </summary>
public static class WeatherOvercast
{
    private static int s_requests;
    private static float s_scale = 1f;

    private static Light s_sun;
    private static float s_baseIntensity;
    private static bool s_hasBase;

    private static CancellationTokenSource s_fadeCts;

    /// <summary>어둡게 하기를 요청한다 — 같은 날씨가 두 번 켜지지 않는 한 뷰당 한 번.</summary>
    public static void Push(float intensityScale, float fadeSeconds)
    {
        s_requests++;
        s_scale = s_requests == 1 ? intensityScale : Mathf.Min(s_scale, intensityScale);
        Apply(fadeSeconds);
    }

    /// <summary>요청을 뺀다 — 마지막이 빠지면 원래 밝기로 돌아간다.</summary>
    public static void Pop(float fadeSeconds)
    {
        s_requests = Mathf.Max(0, s_requests - 1);
        if (s_requests == 0)
            s_scale = 1f;

        Apply(fadeSeconds);
    }

    public static float CurrentTarget => s_hasBase ? s_baseIntensity * s_scale : 0f;

    public static bool HasSun => s_hasBase;

    private static void Apply(float fadeSeconds)
    {
        if (!ResolveSun())
            return;

        s_fadeCts?.Cancel();
        s_fadeCts?.Dispose();
        s_fadeCts = new CancellationTokenSource();

        FadeToAsync(s_baseIntensity * s_scale, fadeSeconds, s_fadeCts.Token).Forget();
    }

    private static bool ResolveSun()
    {
        if (s_sun == null || !s_sun.isActiveAndEnabled)
        {
            s_sun = RenderSettings.sun;
            s_hasBase = false;
        }

        if (s_sun == null)
            return false;

        if (!s_hasBase)
        {
            s_baseIntensity = s_sun.intensity;
            s_hasBase = true;
        }

        return true;
    }

    private static async UniTaskVoid FadeToAsync(float target, float seconds, CancellationToken token)
    {
        float from = s_sun != null ? s_sun.intensity : target;

        for (float t = 0f; t < seconds && s_sun != null; t += Time.deltaTime)
        {
            s_sun.intensity = Mathf.Lerp(from, target, t / seconds);
            await UniTask.NextFrame(token);
        }

        if (s_sun != null)
            s_sun.intensity = target;
    }
}
