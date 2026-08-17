using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 본부 스캐너 충전기 — 상호작용 시 상대의 장착 아이템에 붙은 배터리(IChargeable)를 충전한다. (이슈 #60)
/// GDD 5-2: 스캐너는 배터리 충전식이며 본부 충전기에서만 재충전 가능.
/// 네트워킹은 IChargeable.Charge()(ItemBattery)가 서버 권한으로 처리하므로
/// 이 컴포넌트 자체는 NetworkBehaviour일 필요가 없다.
/// </summary>
[RequireComponent(typeof(Collider))]
public class ScannerCharger : MonoBehaviour, IInteractable
{
    [Header("충전 방식")]
    [SerializeField] private bool m_chargeToFull = true;
    [Tooltip("m_chargeToFull이 false일 때 1회 상호작용당 충전량")]
    [SerializeField] private int m_chargeAmount = 1;

    /// <summary>장착 아이템에 배터리가 있으면 상호작용 의미가 있다 — 조준 피드백(윤곽선) 판정용. (#184)
    /// 완충이어도 윤곽선을 띄운다 — E로 "이미 가득 참" 토스트 피드백을 주기 위함 (#309).</summary>
    public bool CanInteract(GameObject interactor) => FindBattery(interactor) != null;

    // 조준 안내 (#664)
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.Charge;

    public void Interact(GameObject interactor)
    {
        IChargeable chargeable = FindBattery(interactor);
        if (chargeable == null)
            return;

        // 완충이어도 Charge를 호출한다 — 서버가 완충을 감지해 "가득 참" 토스트를 오너에게 띄운다 (#309).
        // (값이 안 바뀌므로 실제 충전은 없고 피드백만 나간다.)
        int amount = m_chargeToFull ? chargeable.MaxBattery : m_chargeAmount;
        chargeable.Charge(amount);
        Debug.Log($"충전기 상호작용 — {amount} 충전 요청");
    }

    // 배터리는 아이템 본체가 아니라 같은 오브젝트의 ItemBattery 컴포넌트가 들고 있다 — 장착 아이템에서 찾아온다.
    private static IChargeable FindBattery(GameObject interactor)
    {
        ItemBase equipped = interactor.GetComponentInParent<PlayerItemUser>()?.EquippedItem;
        return equipped != null ? equipped.GetComponent<IChargeable>() : null;
    }
}
