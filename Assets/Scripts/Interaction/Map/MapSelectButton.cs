using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// Shop 씬 맵 선택 콘솔의 이전/다음 버튼 — 호스트가 E로 다음 라운드 맵을 바꾼다.
/// 호스트 전용이며, 상주 홀더(MapSelection)의 서버 RPC를 호출한다.
/// </summary>
public class MapSelectButton : MonoBehaviour, IInteractable
{
    [Tooltip("체크하면 다음 맵, 해제하면 이전 맵")]
    [SerializeField]
    private bool m_next = true;

    private static bool IsHost =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

    public bool CanInteract(GameObject interactor)
    {
        MapSelection selection = App.Game.MapSelection;
        return IsHost
            && selection != null
            && selection.IsSpawned
            && selection.MapCount > 1
            && MapSelection.IsSelectable;
    }

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.MapSelect;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;

        App.Game.MapSelection.RequestSelectRpc(m_next ? 1 : -1);
    }
}
