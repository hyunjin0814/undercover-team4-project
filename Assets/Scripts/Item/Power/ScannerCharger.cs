using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 본부 스캐너 충전기 — 상호작용한 플레이어의 장착 아이템 배터리(IChargeable)를 충전한다(GDD 5-2).
/// </summary>
[RequireComponent(typeof(Collider))]
public class ScannerCharger : MonoBehaviour, IInteractable
{
    [Header("충전 방식")]
    [SerializeField] private bool m_chargeToFull = true;
    [Tooltip("m_chargeToFull이 false일 때 1회 상호작용당 충전량")]
    [SerializeField] private int m_chargeAmount = 1;

    /// <summary>장착 아이템에 배터리가 있으면 상호작용 가능하다.</summary>
    public bool CanInteract(GameObject interactor) => FindBattery(interactor) != null;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.Charge;

    public void Interact(GameObject interactor)
    {
        IChargeable chargeable = FindBattery(interactor);
        if (chargeable == null)
            return;

        int amount = m_chargeToFull ? chargeable.MaxBattery : m_chargeAmount;
        chargeable.Charge(amount);
        Debug.Log($"충전기 상호작용 — {amount} 충전 요청");
    }

    private static IChargeable FindBattery(GameObject interactor)
    {
        ItemBase equipped = interactor.GetComponentInParent<PlayerItemUser>()?.EquippedItem;
        return equipped != null ? equipped.GetComponent<IChargeable>() : null;
    }
}
