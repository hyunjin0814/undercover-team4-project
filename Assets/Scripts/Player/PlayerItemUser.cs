using System;
using UnityEngine;

[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerItemUser : MonoBehaviour
{
    [Header("장착 아이템")]
    [SerializeField] private ItemBase m_equippedItem;

    private PlayerInputHandler m_inputHandler;
    private PlayerInteractor m_interactor;

    /// <summary>현재 장착 중인 아이템. 없으면 null. (#45 — PlayerHandView가 초기 표시에 사용)</summary>
    public ItemBase EquippedItem => m_equippedItem;

    /// <summary>장착 아이템 변경 이벤트 — 실제로 값이 바뀔 때만 발행. 1인칭 손 표시(#45)·UI 등이 구독한다.</summary>
    public event Action<ItemBase> OnEquippedItemChanged;

    private void Awake()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
        // 사용 시점에 겨냥 중인 대상을 아이템에 넘기기 위한 참조 (#33).
        // 테스트 구성 등 인터랙터가 없으면 null — 이때는 대상 없이(null) 사용된다.
        m_interactor = GetComponent<PlayerInteractor>();
    }

    private void OnEnable()
    {
        m_inputHandler.OnAttackStarted += HandleUseItem;
        m_inputHandler.OnAttackCanceled += HandleCancelItem;
    }

    private void OnDisable()
    {
        m_inputHandler.OnAttackStarted -= HandleUseItem;
        m_inputHandler.OnAttackCanceled -= HandleCancelItem;
    }

    public void SetEquippedItem(ItemBase item)
    {
        if (m_equippedItem == item)
        {
            return;
        }

        m_equippedItem = item;
        OnEquippedItemChanged?.Invoke(item);
    }

    private void HandleUseItem()
    {
        if (m_equippedItem == null)
        {
            return;
        }

        // CanUse() 게이트는 각 아이템의 Use() 내부에서 수행한다 — 사용 불가 사유
        // (배터리 부족 등) 피드백을 아이템이 직접 낼 수 있게 하기 위함. (#34/#35)
        // 겨냥한 대상을 함께 넘긴다 — 각 아이템이 대상에서 필요한 정보를 조회한다.
        GameObject target = m_interactor != null ? m_interactor.CurrentTarget : null;
        m_equippedItem.Use(target);
    }

    private void HandleCancelItem()
    {
        // 채널링 중이 아니면 CancelUse는 무동작이라 항상 호출해도 안전하다.
        // CanUse() 체크 금지 — 채널링 중엔 false라서 취소가 막힌다 (#91)
        m_equippedItem?.CancelUse();
    }
}
