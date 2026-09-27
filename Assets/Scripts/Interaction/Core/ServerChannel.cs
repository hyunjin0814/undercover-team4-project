using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 서버 권위 채널링(홀드형 대기)의 CTS 소유·재진입 가드·취소 정리를 담당하는 컴포지션 헬퍼.
/// </summary>
public class ServerChannel
{
    public enum Result
    {
        Completed,

        Canceled,

        OutOfRange,
    }

    private CancellationTokenSource m_cts;

    public bool IsActive { get; private set; }

    /// <summary>seconds초 동안 채널링한다. keepAlive가 false가 되면 중단하고, progressPoint를 넘는 순간 onProgressPoint를 한 번 호출한다.</summary>
    public async UniTask<Result> RunAsync(
        float seconds,
        Func<bool> keepAlive = null,
        float progressPoint = -1f,
        Action onProgressPoint = null)
    {
        IsActive = true;
        m_cts = new CancellationTokenSource();

        try
        {
            if (keepAlive == null)
            {
                await UniTask.Delay(TimeSpan.FromSeconds(seconds), cancellationToken: m_cts.Token);
                return Result.Completed;
            }

            bool pointFired = onProgressPoint == null || progressPoint < 0f;
            float pointSeconds = seconds * Mathf.Clamp01(progressPoint);

            float elapsed = 0f;
            while (elapsed < seconds)
            {
                if (!keepAlive())
                {
                    return Result.OutOfRange;
                }

                if (!pointFired && elapsed >= pointSeconds)
                {
                    pointFired = true;
                    onProgressPoint();
                }

                await UniTask.Yield(PlayerLoopTiming.Update, m_cts.Token);
                elapsed += Time.deltaTime;
            }

            if (!pointFired)
                onProgressPoint();

            return Result.Completed;
        }
        catch (OperationCanceledException)
        {
            return Result.Canceled;
        }
        finally
        {
            IsActive = false;
            m_cts?.Dispose();
            m_cts = null;
        }
    }

    /// <summary>진행 중인 채널링을 취소한다. 채널링 중이 아니면 무동작.</summary>
    public void Cancel() => m_cts?.Cancel();

    /// <summary>보유 컴포넌트의 OnDestroy에서 호출 — 취소 후 CTS를 정리한다.</summary>
    public void Dispose()
    {
        m_cts?.Cancel();
        m_cts?.Dispose();
        m_cts = null;
    }
}
