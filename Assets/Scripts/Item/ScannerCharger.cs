using UnityEngine;

/// <summary>
/// 본부 스캐너 충전기 — 상호작용 시 상대의 장착 아이템이 IChargeable이면 충전한다. (이슈 #60)
/// GDD 5-2: 스캐너는 배터리 충전식이며 본부 충전기에서만 재충전 가능.
/// 네트워킹은 IChargeable.Charge()(예: Scanner.Charge())가 서버 권한으로 처리하므로
/// 이 컴포넌트 자체는 NetworkBehaviour일 필요가 없다.
/// </summary>
[RequireComponent(typeof(Collider))]
public class ScannerCharger : MonoBehaviour, IInteractable
{
    [Header("충전 방식")]
    [SerializeField] private bool m_chargeToFull = true;
    [Tooltip("m_chargeToFull이 false일 때 1회 상호작용당 충전량")]
    [SerializeField] private int m_chargeAmount = 1;

    /// <summary>장착 아이템이 충전 대상(IChargeable)이고 완충이 아닐 때만 상호작용 의미가 있다 —
    /// 조준 피드백(윤곽선) 판정용. Interact()의 조기 반환 조건과 동일 기준. (#184)</summary>
    public bool CanInteract(GameObject interactor)
    {
        IChargeable chargeable = interactor.GetComponentInParent<PlayerItemUser>()?.EquippedItem as IChargeable;
        return chargeable != null && !chargeable.IsFullyCharged;
    }

    public void Interact(GameObject interactor)
    {
        IChargeable chargeable = interactor.GetComponentInParent<PlayerItemUser>()?.EquippedItem as IChargeable;
        if (chargeable == null)
            return;

        if (chargeable.IsFullyCharged)
        {
            Debug.Log("충전기 상호작용 — 이미 완충 상태");
            return;
        }

        int amount = m_chargeToFull ? chargeable.MaxBattery : m_chargeAmount;
        chargeable.Charge(amount);
        Debug.Log($"충전기 상호작용 — {amount} 충전 요청");
    }
}
