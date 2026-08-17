using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 기능 정지(Die)된 동료 몸의 상호작용키(E) 반응 — 약탈 창을 연다. (#487)
///
/// 두 역할을 이어 주기만 한다: 겨냥당한 몸(<see cref="PlayerLootable"/>)과 누른 사람
/// (<see cref="PlayerLooter"/>)을 짝지어 요청을 넘긴다. 판정은 양쪽이 각자 갖는다.
///
/// <b>E가 비어 있어서 쓸 수 있었다.</b> 쓰러진 동료에 대한 조작 중 밧줄 좌클릭은 운반(#365),
/// 운반 중 E는 내려놓기인데, <b>쓰러진 몸 자체를 겨냥한 E</b>는 아무도 쓰지 않았다 — 현장 구조
/// (<see cref="PlayerReviver"/>)가 그 자리에 있었지만 #524로 <c>Down</c>이 사라지며 휴면이 됐다.
/// 그래서 #487이 걱정한 "일으키기와 털기가 같은 키를 다툰다"는 지금 성립하지 않는다.
///
/// <b>운반 중에는 내려놓기가 앞선다</b> — 업은 몸을 겨냥한 E는 <see cref="PlayerInteractor"/>가
/// 내려놓기로 소비한다. 털려면 먼저 내려놓게 두는 편이 규칙을 하나 더 만드는 것보다 낫다.
///
/// 플레이어 <b>루트</b>에 붙인다. 몸통(CharacterController)은 Default 레이어라 조준 마스크에 걸리지
/// 않고, <c>Interactable</c> 레이어인 조준 히트박스는 쓰러져 있는 동안만 켜지므로
/// (<see cref="PlayerIncapacitation"/>) "살아 있는 동료에게는 E가 아예 닿지 않는다"가 배치로 보장된다.
/// 그 히트박스는 자식이고 <see cref="PlayerInteractor"/>는 <c>GetComponentInParent</c>로 찾으므로
/// 루트에 있어도 잡힌다 (<see cref="NpcSubdueInteractable"/>과 같은 관례).
/// </summary>
[RequireComponent(typeof(PlayerLootable))]
public class LootableBodyInteractable : MonoBehaviour, IInteractable
{
    private PlayerLootable m_body;

    private void Awake()
    {
        m_body = GetComponent<PlayerLootable>();
    }

    /// <summary>E가 실제로 동작하는 상태인지 — 조준 피드백(윤곽선) 판정용. 서버 가드와 같은 기준 (#184).</summary>
    public bool CanInteract(GameObject interactor)
    {
        if (!m_body.CanBeLooted)
            return false;

        PlayerLooter looter = FindLooter(interactor);
        return looter != null && looter.gameObject != gameObject;
    }

    // 조준 안내 (#664). 위 CanInteract가 약탈 가능 상태와 자기 자신을 이미 걸러내므로 사유는 두지 않는다.
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.Loot;

    public void Interact(GameObject interactor)
    {
        PlayerLooter looter = FindLooter(interactor);
        if (looter == null || looter.gameObject == gameObject)
            return; // 자기 자신 제외 — 최종 판정은 서버(PlayerLooter.CanLoot)가 한다

        looter.RequestOpenLoot(m_body);
    }

    private static PlayerLooter FindLooter(GameObject interactor) =>
        interactor != null ? interactor.GetComponentInParent<PlayerLooter>() : null;
}
