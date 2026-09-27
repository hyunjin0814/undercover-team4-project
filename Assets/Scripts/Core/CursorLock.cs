using UnityEngine;

/// <summary>
/// 마우스 커서 잠금의 단일 소유자 — UI는 잠금 해제 요청만 넣고 빼며, 요청 수로 잠금 여부를 정한다.
/// 게임플레이 중이 아니면 항상 풀어 둔다.
/// </summary>
public static class CursorLock
{
    private static int s_unlockCount;
    private static bool s_gameplayActive;

    public static bool IsUnlocked => s_unlockCount > 0 || !s_gameplayActive;

    /// <summary>커서 해제를 요청한다. <see cref="PopUnlock"/>과 반드시 1:1로 맞출 것.</summary>
    public static void PushUnlock()
    {
        s_unlockCount++;
        Apply();
    }

    /// <summary>커서 해제 요청을 거둔다. 남은 요청이 없고 게임플레이 중이면 커서가 다시 잠긴다.</summary>
    public static void PopUnlock()
    {
        if (s_unlockCount > 0)
            s_unlockCount--;

        Apply();
    }

    /// <summary>오너 로컬 플레이어의 스폰/디스폰을 알린다 — <see cref="PlayerMovement"/> 전용.</summary>
    public static void SetGameplayActive(bool active)
    {
        s_gameplayActive = active;
        Apply();
    }

    /// <summary>요청 수를 바꾸지 않고 현재 판정을 커서에 다시 적용한다(에디터 ESC 잠금 해제 보정용).</summary>
    public static void Reassert() => Apply();

    private static void Apply()
    {
        bool unlocked = IsUnlocked;
        Cursor.lockState = unlocked ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = unlocked;
    }
}
