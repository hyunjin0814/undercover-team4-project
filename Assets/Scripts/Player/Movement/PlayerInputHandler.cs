using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

/// <summary>
/// 오너의 Input System 액션을 구독해 게임플레이 입력 이벤트로 발행한다.
/// 입력 일시 정지와 키 표기 조회를 제공한다.
/// </summary>
public class PlayerInputHandler : NetworkBehaviour
{
    [Header("Input Actions")]
    [SerializeField]
    private InputActionReference m_moveAction;

    [SerializeField]
    private InputActionReference m_lookAction;

    [SerializeField]
    private InputActionReference m_interactAction;

    [SerializeField]
    private InputActionReference m_lootAction;

    [SerializeField]
    private InputActionReference m_sprintAction;

    [SerializeField]
    private InputActionReference m_useItemAction;

    [SerializeField]
    private InputActionReference m_previousAction;

    [SerializeField]
    private InputActionReference m_nextAction;

    [SerializeField]
    private InputActionReference m_dropAction;

    [SerializeField]
    private InputActionReference m_selectSlotAction;

    [SerializeField]
    private InputActionReference m_toggleInventoryAction;

    [SerializeField]
    private InputActionReference m_crouchAction;

    [SerializeField]
    private InputActionReference m_jumpAction;

    [SerializeField]
    private InputActionReference m_emoteAction;

    [SerializeField]
    private InputActionReference m_teamStatusAction;

    public Vector2 MoveInput { get; private set; }
    public Vector2 LookInput { get; private set; }
    public bool IsSprinting { get; private set; }

    public string InteractBinding
    {
        get
        {
            if (m_interactBinding == null)
                m_interactBinding = BindingDisplay(m_interactAction);

            return m_interactBinding;
        }
    }

    public string UseItemBinding
    {
        get
        {
            if (m_useItemBinding == null)
                m_useItemBinding = BindingDisplay(m_useItemAction);

            return m_useItemBinding;
        }
    }

    public string LootBinding
    {
        get
        {
            if (m_lootBinding == null)
                m_lootBinding = BindingDisplay(m_lootAction);

            return m_lootBinding;
        }
    }

    /// <summary>키보드·마우스 스킴의 첫 바인딩 표기를 돌려준다.</summary>
    private static string BindingDisplay(InputActionReference reference)
    {
        if (reference == null || reference.action == null)
            return "(미할당)";

        InputAction action = reference.action;
        for (int i = 0; i < action.bindings.Count; i++)
        {
            InputBinding binding = action.bindings[i];
            if (binding.isComposite || binding.isPartOfComposite)
                continue;
            if (binding.groups == null || !binding.groups.Contains(k_displayScheme))
                continue;

            return action.GetBindingDisplayString(i);
        }

        return action.GetBindingDisplayString();
    }

    public event Action OnInteractStarted;
    public event Action OnInteractPerformed;
    public event Action OnInteractCanceled;
    public event Action OnLootPerformed;
    public event Action OnUseItemStarted;
    public event Action OnUseItemCanceled;
    public event Action OnPreviousItem;
    public event Action OnNextItem;
    public event Action OnDropItem;
    public event Action<int> OnSelectSlot;
    public event Action OnToggleInventory;
    public event Action<bool> OnCrouchChanged;
    public event Action OnJumpPressed;
    public event Action OnEmoteWheelOpened;
    public event Action OnEmoteWheelClosed;
    public event Action OnTeamStatusOpened;
    public event Action OnTeamStatusClosed;

    private bool m_isLocalOwner;

    private bool m_isSuspended;

    private bool m_isPeekingTeamStatus;

    private const string k_displayScheme = "Keyboard&Mouse";

    private string m_interactBinding;
    private string m_useItemBinding;
    private string m_lootBinding;

    public bool IsSuspended => m_isSuspended;

    /// <summary>게임플레이 입력 액션을 일시 정지/재개한다(구독은 유지).</summary>
    public void SetSuspended(bool suspended)
    {
        if (!m_isLocalOwner || m_isSuspended == suspended)
            return;

        m_isSuspended = suspended;
        SetActionsEnabled(!suspended);

        if (suspended)
        {
            MoveInput = Vector2.zero;
            LookInput = Vector2.zero;
            IsSprinting = false;
        }
    }

    /// <summary>상호작용 키가 이번 프레임에 눌렸는지 컨트롤을 직접 읽는다(입력 정지 중에도 동작).</summary>
    public bool WasInteractPressedThisFrame()
    {
        if (m_interactAction == null || m_interactAction.action == null)
            return false;

        foreach (InputControl control in m_interactAction.action.controls)
        {
            if (control is ButtonControl button && button.wasPressedThisFrame)
                return true;
        }

        return false;
    }

    /// <summary>뒤지기(R) 키가 이번 프레임에 눌렸는지 컨트롤을 직접 읽는다(입력 정지 중에도 동작).</summary>
    public bool WasLootPressedThisFrame()
    {
        if (m_lootAction == null || m_lootAction.action == null)
            return false;

        foreach (InputControl control in m_lootAction.action.controls)
        {
            if (control is ButtonControl button && button.wasPressedThisFrame)
                return true;
        }

        return false;
    }

    /// <summary>팀 상황판을 보는 동안 감정표현 휠·인벤토리 편집 입력 이벤트를 막는다.</summary>
    public void SetTeamStatusPeeking(bool peeking)
    {
        if (!m_isLocalOwner)
            return;

        m_isPeekingTeamStatus = peeking;
    }

    private void SetActionsEnabled(bool value)
    {
        InputActionReference[] actions =
        {
            m_moveAction,
            m_lookAction,
            m_interactAction,
            m_lootAction,
            m_sprintAction,
            m_useItemAction,
            m_previousAction,
            m_nextAction,
            m_dropAction,
            m_selectSlotAction,
            m_toggleInventoryAction,
            m_crouchAction,
            m_jumpAction,
            m_emoteAction,
            m_teamStatusAction,
        };

        foreach (InputActionReference reference in actions)
        {
            if (reference == null || reference.action == null)
                continue;

            if (value)
                reference.action.Enable();
            else
                reference.action.Disable();
        }
    }

    public override void OnNetworkSpawn()
    {
        m_isLocalOwner = IsOwner;

        if (!m_isLocalOwner)
        {
            enabled = false;
            return;
        }

        SetActionsEnabled(true);

        m_moveAction.action.performed += OnMove;
        m_moveAction.action.canceled += OnMove;
        m_lookAction.action.performed += OnLook;
        m_lookAction.action.canceled += OnLook;
        m_interactAction.action.started += OnInteractStartedHandler;
        m_interactAction.action.performed += OnInteractPerformedHandler;
        m_interactAction.action.canceled += OnInteractCanceledHandler;
        m_lootAction.action.performed += OnLootPerformedHandler;
        m_sprintAction.action.performed += OnSprintPerformed;
        m_sprintAction.action.canceled += OnSprintCanceled;
        m_useItemAction.action.started += OnUseItemStartedHandler;
        m_useItemAction.action.canceled += OnUseItemCanceledHandler;
        m_previousAction.action.performed += OnPreviousItemHandler;
        m_nextAction.action.performed += OnNextItemHandler;
        m_dropAction.action.performed += OnDropItemHandler;
        m_selectSlotAction.action.performed += OnSelectSlotHandler;
        m_toggleInventoryAction.action.performed += OnToggleInventoryHandler;
        m_crouchAction.action.started += OnCrouchStartedHandler;
        m_crouchAction.action.canceled += OnCrouchCanceledHandler;
        m_jumpAction.action.started += OnJumpStartedHandler;
        m_emoteAction.action.started += OnEmoteStartedHandler;
        m_emoteAction.action.canceled += OnEmoteCanceledHandler;
        m_teamStatusAction.action.started += OnTeamStatusStartedHandler;
        m_teamStatusAction.action.canceled += OnTeamStatusCanceledHandler;

        InputSystem.onActionChange += HandleActionChange;
    }

    public override void OnNetworkDespawn()
    {
        if (!m_isLocalOwner)
            return;

        m_moveAction.action.performed -= OnMove;
        m_moveAction.action.canceled -= OnMove;
        m_lookAction.action.performed -= OnLook;
        m_lookAction.action.canceled -= OnLook;
        m_interactAction.action.started -= OnInteractStartedHandler;
        m_interactAction.action.performed -= OnInteractPerformedHandler;
        m_interactAction.action.canceled -= OnInteractCanceledHandler;
        m_lootAction.action.performed -= OnLootPerformedHandler;
        m_sprintAction.action.performed -= OnSprintPerformed;
        m_sprintAction.action.canceled -= OnSprintCanceled;
        m_useItemAction.action.started -= OnUseItemStartedHandler;
        m_useItemAction.action.canceled -= OnUseItemCanceledHandler;
        m_previousAction.action.performed -= OnPreviousItemHandler;
        m_nextAction.action.performed -= OnNextItemHandler;
        m_dropAction.action.performed -= OnDropItemHandler;
        m_selectSlotAction.action.performed -= OnSelectSlotHandler;
        m_toggleInventoryAction.action.performed -= OnToggleInventoryHandler;
        m_crouchAction.action.started -= OnCrouchStartedHandler;
        m_crouchAction.action.canceled -= OnCrouchCanceledHandler;
        m_jumpAction.action.started -= OnJumpStartedHandler;
        m_emoteAction.action.started -= OnEmoteStartedHandler;
        m_emoteAction.action.canceled -= OnEmoteCanceledHandler;
        m_teamStatusAction.action.started -= OnTeamStatusStartedHandler;
        m_teamStatusAction.action.canceled -= OnTeamStatusCanceledHandler;

        InputSystem.onActionChange -= HandleActionChange;

        SetActionsEnabled(false);
        m_isSuspended = false;
    }

    private void HandleActionChange(object obj, InputActionChange change)
    {
        if (change != InputActionChange.BoundControlsChanged)
            return;

        m_interactBinding = null;
        m_useItemBinding = null;
        m_lootBinding = null;
    }

    private void OnMove(InputAction.CallbackContext ctx) => MoveInput = ctx.ReadValue<Vector2>();

    private void OnLook(InputAction.CallbackContext ctx) => LookInput = ctx.ReadValue<Vector2>();

    private void OnInteractStartedHandler(InputAction.CallbackContext ctx) =>
        OnInteractStarted?.Invoke();

    private void OnInteractPerformedHandler(InputAction.CallbackContext ctx) =>
        OnInteractPerformed?.Invoke();

    private void OnInteractCanceledHandler(InputAction.CallbackContext ctx) =>
        OnInteractCanceled?.Invoke();

    private void OnLootPerformedHandler(InputAction.CallbackContext ctx) =>
        OnLootPerformed?.Invoke();

    private void OnSprintPerformed(InputAction.CallbackContext ctx) => IsSprinting = true;

    private void OnSprintCanceled(InputAction.CallbackContext ctx) => IsSprinting = false;

    private void OnUseItemStartedHandler(InputAction.CallbackContext ctx) =>
        OnUseItemStarted?.Invoke();

    private void OnUseItemCanceledHandler(InputAction.CallbackContext ctx) =>
        OnUseItemCanceled?.Invoke();

    private void OnPreviousItemHandler(InputAction.CallbackContext ctx) => OnPreviousItem?.Invoke();

    private void OnNextItemHandler(InputAction.CallbackContext ctx) => OnNextItem?.Invoke();

    private void OnDropItemHandler(InputAction.CallbackContext ctx) => OnDropItem?.Invoke();

    private void OnSelectSlotHandler(InputAction.CallbackContext ctx)
    {
        if (int.TryParse(ctx.control.name, out int keyNumber))
        {
            OnSelectSlot?.Invoke(keyNumber - 1);
        }
    }

    private void OnToggleInventoryHandler(InputAction.CallbackContext ctx)
    {
        if (m_isPeekingTeamStatus)
            return;

        OnToggleInventory?.Invoke();
    }

    private void OnCrouchStartedHandler(InputAction.CallbackContext ctx) =>
        OnCrouchChanged?.Invoke(true);

    private void OnCrouchCanceledHandler(InputAction.CallbackContext ctx) =>
        OnCrouchChanged?.Invoke(false);

    private void OnJumpStartedHandler(InputAction.CallbackContext ctx) => OnJumpPressed?.Invoke();

    private void OnEmoteStartedHandler(InputAction.CallbackContext context)
    {
        if (m_isPeekingTeamStatus)
            return;

        OnEmoteWheelOpened?.Invoke();
    }

    private void OnEmoteCanceledHandler(InputAction.CallbackContext context) =>
        OnEmoteWheelClosed?.Invoke();

    private void OnTeamStatusStartedHandler(InputAction.CallbackContext context) =>
        OnTeamStatusOpened?.Invoke();

    private void OnTeamStatusCanceledHandler(InputAction.CallbackContext context) =>
        OnTeamStatusClosed?.Invoke();
}
