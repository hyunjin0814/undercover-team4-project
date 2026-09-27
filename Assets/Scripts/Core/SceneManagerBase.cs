using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 씬당 하나 배치되는 씬 매니저 베이스. 씬 진행·전환의 진입점.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public abstract class SceneManagerBase : CommonManagerBase
{
    public virtual void MoveToNextScene(EScene nextScene) => App.LoadScene(nextScene);

    /// <summary>씬 고유 초기화가 끝날 때까지 대기한다. 기본은 즉시 완료이며, 필요한 씬만 override한다.</summary>
    public virtual UniTask WaitUntilReadyAsync(CancellationToken token) => UniTask.CompletedTask;

    /// <summary>씬의 로컬 준비 조건을 상한 시간까지 기다린다. 초과하면 경고만 남기고 통과한다.</summary>
    protected async UniTask WaitUntilLocallyReadyAsync(
        CancellationToken token, Func<bool> isReady, float timeoutSeconds, string label)
    {
        float deadline = Time.realtimeSinceStartup + timeoutSeconds;
        await UniTask.WaitUntil(
            () => isReady() || Time.realtimeSinceStartup >= deadline,
            cancellationToken: token
        );

        if (!isReady())
            Debug.LogWarning(
                $"[{GetType().Name}] {label}를 {timeoutSeconds}초 내에 확인하지 못했다 — 그대로 진행한다",
                this
            );
    }
}
