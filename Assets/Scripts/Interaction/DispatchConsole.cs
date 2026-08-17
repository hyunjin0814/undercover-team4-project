using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// Shop 씬 '출동' 콘솔 (#326) — 호스트가 다가가 E로 게임 씬 전환을 시작한다.
/// 구 OnGUI 출동 버튼을 대체한다: 크로스헤어+E 상호작용이라 커서가 필요 없어, ESC 커서 토글 제거(#326)와 무관하게 동작한다.
/// 호스트 전용 — 클라가 눌러도 무동작(3중 방어: CanInteract 피드백 차단 · Interact 가드 · Dispatch 내부 IsServer 가드).
/// </summary>
public class DispatchConsole : MonoBehaviour, IInteractable
{
    private static bool IsHost =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

    // 조준 피드백 게이팅 — 클라에는 윤곽선/크로스헤어 반응이 뜨지 않는다 (누를 유도 자체가 없음).
    public bool CanInteract(GameObject interactor) => IsHost;

    // 조준 안내 (#664). 클라는 위 게이팅에 걸려 윤곽선도 안내도 뜨지 않는다 — 사유를 따로 두지 않는
    // 이유가 그것이다. "누를 유도 자체가 없음"이라는 종전 방침을 안내에도 그대로 적용한다.
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.Dispatch;

    // PlayerInteractor는 CanInteract 확인 없이 Interact를 호출하므로(피드백 전용), 여기서도 호스트만 통과시킨다.
    public void Interact(GameObject interactor)
    {
        if (!IsHost)
            return;
        App.SceneFlow.Shop?.Dispatch();
    }
}
