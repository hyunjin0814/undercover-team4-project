using UnityEngine;
using UnityEngine.Localization;

public interface IInteractable
{
    void Interact(GameObject interactor);

    /// <summary>
    /// 지금 이 대상에 E 상호작용이 실제로 동작하는지 — 조준 피드백(윤곽선·크로스헤어) 판정용. (#184)
    /// 기본 구현은 항상 true. 상태에 따라 상호작용이 막히는 구현체만 재정의한다.
    /// Interact()가 내부에서 거르는 조건과 같은 기준을 유지해야
    /// "윤곽선이 떴는데 눌러도 반응 없음"이 안 생긴다.
    /// </summary>
    bool CanInteract(GameObject interactor) => true;

    /// <summary>
    /// 조준 안내에 띄울 동작 문구 — "문 열기"처럼 동사로. 키는 표시 쪽이 붙이므로 적지 않는다. (#664)
    /// null이면 안내가 뜨지 않는다. 상태로 동작이 갈리면 여기서 갈라 준다(열기/닫기).
    /// 조준 중 매 프레임 불리니 인스턴스는 캐시해서 돌려줄 것 — <see cref="InteractPrompts"/>.
    /// </summary>
    LocalizedString PromptLabel(GameObject interactor) => null;

    /// <summary>
    /// 그 동작이 막힌 이유 — null이면 정상, 값이 있으면 회색 + 사유가 붙는다("… — 잠김"). (#664)
    /// <see cref="CanInteract"/>와 별개다: 잠긴 문처럼 윤곽선은 띄우면서 눌러도 거부되는 대상이
    /// 있고, 그 자리가 이슈가 지목한 "눌러도 반응 없음"이다. CanInteract가 false면 안내 자체가 없다.
    /// </summary>
    LocalizedString BlockedReason(GameObject interactor) => null;

    // 끌기 중 E가 '놓기'보다 우선하는지를 여는 TakesPriorityOverRelease(#414)는 제거됐다 (#492) —
    // 유일한 재정의자였던 인계 단말이 사라져 아무도 true를 돌려주지 않는 죽은 확장점이 됐다.
    // 같은 취지가 다시 필요하면 운반 쪽 ICarriedBodyReceiver(PlayerInteractor)가 살아 있는 선례다.
}
