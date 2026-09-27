using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// Shop 씬 출동 콘솔 — 호스트가 E로 게임 씬 전환을 시작한다. 호스트 전용.
/// </summary>
public class DispatchConsole : MonoBehaviour, IInteractable
{
    private static bool IsHost =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

    public bool CanInteract(GameObject interactor) => IsHost;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.Dispatch;

    public void Interact(GameObject interactor)
    {
        if (!IsHost)
            return;
        App.SceneFlow.Shop?.Dispatch();
    }
}
