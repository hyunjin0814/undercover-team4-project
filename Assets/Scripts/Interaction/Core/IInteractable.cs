using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// E 키 상호작용 대상 인터페이스 — 상호작용, 가능 여부, 조준 안내 문구, 막힌 사유를 정의한다.
/// </summary>
public interface IInteractable
{
    void Interact(GameObject interactor);

    /// <summary>지금 이 대상에 E 상호작용이 동작하는지 판정한다(조준 피드백용). 기본은 true.</summary>
    bool CanInteract(GameObject interactor) => true;

    /// <summary>조준 안내에 띄울 동작 문구를 돌려준다. null이면 안내하지 않는다.</summary>
    LocalizedString PromptLabel(GameObject interactor) => null;

    /// <summary>동작이 막힌 이유를 돌려준다. null이면 정상.</summary>
    LocalizedString BlockedReason(GameObject interactor) => null;
}
