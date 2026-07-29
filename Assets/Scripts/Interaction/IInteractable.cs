using UnityEngine;

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
    /// 연행·밧줄 끌기 중에도 이 대상의 E가 '놓기'보다 우선하는가 — 기본은 false. (#414)
    /// 끌기 중 E는 놓기로 소비되는 것이 원칙이라(PlayerInteractor), 인계 단말처럼
    /// <b>끌고 온 상태에서만 의미가 있는</b> 대상만 true로 재정의한다.
    /// true로 두면 <see cref="CanInteract"/>가 참일 때만 우선하므로, 조건이 어긋난 순간엔
    /// 종전대로 놓기가 동작한다 — 끌던 NPC를 놓을 방법이 사라지지 않는다.
    /// </summary>
    bool TakesPriorityOverRelease(GameObject interactor) => false;
}
