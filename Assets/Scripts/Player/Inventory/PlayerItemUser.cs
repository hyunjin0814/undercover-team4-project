using System;
using UnityEngine;

/// <summary>
/// 장착 아이템을 들고 사용 입력을 아이템에 전달한다. 무력화·밧줄 끌기·단말 입력 중에는 사용을 막는다.
/// </summary>
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerItemUser : MonoBehaviour
{
    [Header("장착 아이템")]
    [SerializeField] private ItemBase m_equippedItem;

    private PlayerInputHandler m_inputHandler;
    private PlayerInteractor m_interactor;
    private PlayerIncapacitation m_incapacitation;
    private PlayerEscorter m_escorter;
    private PlayerTerminalFocus m_terminalFocus;

    public ItemBase EquippedItem => m_equippedItem != null ? m_equippedItem : null;

    public event Action<ItemBase> OnEquippedItemChanged;

    private void Awake()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_interactor = GetComponent<PlayerInteractor>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_escorter = GetComponent<PlayerEscorter>();
        m_terminalFocus = GetComponent<PlayerTerminalFocus>();
    }

    private void OnEnable()
    {
        m_inputHandler.OnUseItemStarted += HandleUseItem;
        m_inputHandler.OnUseItemCanceled += HandleCancelItem;
    }

    private void OnDisable()
    {
        m_inputHandler.OnUseItemStarted -= HandleUseItem;
        m_inputHandler.OnUseItemCanceled -= HandleCancelItem;
    }

    public void SetEquippedItem(ItemBase item)
    {
        if (m_equippedItem == item)
        {
            return;
        }

        if (m_equippedItem != null)
        {
            m_equippedItem.CancelUse();
        }

        m_equippedItem = item;
        OnEquippedItemChanged?.Invoke(item);
    }

    private void HandleUseItem()
    {
        if (CursorLock.IsUnlocked)
        {
            return;
        }

        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
        {
            return;
        }

        if (m_terminalFocus != null && m_terminalFocus.IsFocusing)
        {
            return;
        }

        if (m_equippedItem == null)
        {
            return;
        }

        GameObject target = m_interactor != null ? m_interactor.CurrentTarget : null;
        m_equippedItem.Use(target);
    }

    /// <summary>진행 중인 아이템 채널링을 취소한다.</summary>
    public void CancelUse()
    {
        if (m_equippedItem != null)
        {
            m_equippedItem.CancelUse();
        }
    }

    private void HandleCancelItem() => CancelUse();
}
