using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;

public enum EResolveResult
{
    Resolved,

    TimedOut,

    Aborted,
}

/// <summary>
/// NetworkObjectReference가 이 클라에서 해석될 때까지 몇 프레임 기다리는 공용 대기 정책.
/// </summary>
public static class NetworkRefResolver
{
    public const int k_maxWaitFrames = 120;

    /// <summary>참조 하나가 해석될 때까지 기다린다.</summary>
    public static UniTask<EResolveResult> WaitAsync(
        NetworkObjectReference itemRef,
        Func<bool> isCallerAlive
    ) => WaitCoreAsync(() => itemRef.TryGet(out _), isCallerAlive);

    /// <summary>참조 여러 개가 전부 해석될 때까지 기다린다.</summary>
    public static UniTask<EResolveResult> WaitAsync(
        IReadOnlyList<NetworkObjectReference> itemRefs,
        Func<bool> isCallerAlive
    ) => WaitCoreAsync(() => AllResolved(itemRefs), isCallerAlive);

    private static async UniTask<EResolveResult> WaitCoreAsync(
        Func<bool> isResolved,
        Func<bool> isCallerAlive
    )
    {
        for (int frame = 0; frame < k_maxWaitFrames && !isResolved(); frame++)
        {
            await UniTask.Yield(PlayerLoopTiming.Update);

            if (!isCallerAlive())
            {
                return EResolveResult.Aborted;
            }
        }

        return isResolved() ? EResolveResult.Resolved : EResolveResult.TimedOut;
    }

    private static bool AllResolved(IReadOnlyList<NetworkObjectReference> itemRefs)
    {
        for (int i = 0; i < itemRefs.Count; i++)
        {
            if (!itemRefs[i].TryGet(out _))
            {
                return false;
            }
        }

        return true;
    }
}
