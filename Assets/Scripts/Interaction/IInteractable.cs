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
}
