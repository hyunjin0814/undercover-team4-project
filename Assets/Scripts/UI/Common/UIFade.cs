using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// CanvasGroup 알파를 실시간 기준으로 페이드한다. 시작 전 프레임을 흘리고 프레임당 시간에 상한을 둬 로드 히치를 흡수한다.
/// </summary>
public static class UIFade
{
    private const int k_settleFrames = 2;

    private const float k_maxStep = 1f / 30f;

    /// <summary><paramref name="group"/>의 알파를 옮긴다. null이거나 시간이 0이면 즉시 끝낸다.</summary>
    public static async UniTask ToAsync(
        CanvasGroup group,
        float from,
        float to,
        float seconds,
        CancellationToken token
    )
    {
        if (group == null)
            return;

        group.alpha = from;

        if (seconds <= 0f)
        {
            group.alpha = to;
            return;
        }

        await UniTask.DelayFrame(k_settleFrames, PlayerLoopTiming.Update, token);

        float elapsed = 0f;
        while (elapsed < seconds)
        {
            elapsed += Mathf.Min(Time.unscaledDeltaTime, k_maxStep);
            group.alpha = Mathf.Lerp(from, to, elapsed / seconds);
            await UniTask.Yield(PlayerLoopTiming.Update, token);
        }

        group.alpha = to;
    }
}
