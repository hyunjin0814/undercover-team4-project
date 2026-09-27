using UnityEngine;

/// <summary>
/// ESC로 여는 메뉴(일시정지·종료 확인)를 억제하는 플래그.
/// 자체적으로 ESC를 처리하는 모달이 열려 있는 동안 매 프레임 BlockThisFrame을 호출해 pause가 겹쳐 뜨지 않게 한다.
/// </summary>
public static class EscMenuGuard
{
    private static int s_blockUntilFrame = -1;

    /// <summary>모달이 열려 있는 프레임마다 호출 — 이번 프레임과 다음 프레임의 ESC 진입 메뉴 오픈을 막는다.</summary>
    public static void BlockThisFrame() => s_blockUntilFrame = Time.frameCount + 1;

    public static bool IsBlocked => Time.frameCount <= s_blockUntilFrame;
}
