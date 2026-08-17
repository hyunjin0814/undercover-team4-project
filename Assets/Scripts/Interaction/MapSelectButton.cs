using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// Shop 씬 맵 선택 콘솔의 이전/다음 버튼 (#578) — 호스트가 다가가 E로 다음 라운드 맵을 바꾼다.
/// 커서가 필요 없는 물리 콘솔 방식이라 ESC 커서 토글 제거(#326)와 무관하게 동작한다.
/// 호스트 전용 — 클라가 눌러도 무동작. DispatchConsole의 3중 방어를 그대로 따른다
/// (CanInteract 피드백 차단 · Interact 가드 · <see cref="MapSelection.RequestSelectRpc"/> 내부 서버 가드).
///
/// 순수 MonoBehaviour다: Interact()는 조준한 클라이언트에서 실행되므로(PlayerInteractor 오너 전용)
/// 상주 홀더의 서버 RPC를 부르면 되고, 버튼 자체엔 NetworkObject가 필요 없다 (CCTVSwitchButton과 동일).
/// 홀더는 런타임 스폰물이라 인스펙터로 못 잡는다 — App 파사드로 조회한다.
/// </summary>
public class MapSelectButton : MonoBehaviour, IInteractable
{
    [Tooltip("체크하면 다음 맵, 해제하면 이전 맵")]
    [SerializeField]
    private bool m_next = true;

    private static bool IsHost =>
        NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

    // 윤곽선 판정과 실제 동작 조건을 일치시킨다 — 고를 맵이 한 장뿐이거나 이미 출동했으면
    // 눌러도 바뀌지 않으므로 윤곽선도 뜨지 않아야 한다 (CCTVSwitchButton과 같은 기준).
    public bool CanInteract(GameObject interactor)
    {
        MapSelection selection = App.Game.MapSelection;
        return IsHost
            && selection != null
            && selection.IsSpawned
            && selection.MapCount > 1
            && MapSelection.IsSelectable;
    }

    // 조준 안내 (#664)
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.MapSelect;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;

        App.Game.MapSelection.RequestSelectRpc(m_next ? 1 : -1);
    }
}
