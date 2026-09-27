using UnityEngine;

/// <summary>
/// 릴리스 빌드에서 Debug.Log/LogWarning/LogError 출력을 막는다. 에디터·Development Build는 그대로 둔다.
/// 출력만 막을 뿐 로그 인자 계산 비용은 남는다.
/// </summary>
public static class LogPolicy
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Apply()
    {
        if (Debug.isDebugBuild)
            return;

        Debug.unityLogger.logEnabled = false;
    }
}
