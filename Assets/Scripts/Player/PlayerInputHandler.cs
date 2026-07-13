using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

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
    private InputActionReference m_sprintAction;

    [SerializeField]
    private InputActionReference m_attackAction;

    [SerializeField]
    private InputActionReference m_previousAction;

    [SerializeField]
    private InputActionReference m_nextAction;

    public Vector2 MoveInput { get; private set; }
    public Vector2 LookInput { get; private set; }
    public bool IsSprinting { get; private set; }

    public event Action OnInteractStarted; // 상호작용 버튼 누름
    public event Action OnInteractPerformed; // 상호작용 발동 — 순수 Button이라 누르는 즉시 발화 (즉시발동)
    public event Action OnInteractCanceled; // 상호작용 버튼 뗌
    public event Action OnAttackStarted; // 아이템 사용 시작 (좌클릭 누름 — 채널링 시작, #91)
    public event Action OnAttackCanceled; // 아이템 사용 중단 (좌클릭 뗌 — 채널링 취소, #91)
    public event Action OnPreviousItem; // 마우스 휠 위 — 이전 아이템으로 전환 (#46)
    public event Action OnNextItem; // 마우스 휠 아래 — 다음 아이템으로 전환 (#46)

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        m_moveAction.action.Enable();
        m_lookAction.action.Enable();
        m_interactAction.action.Enable();
        m_sprintAction.action.Enable();
        m_attackAction.action.Enable();
        m_previousAction.action.Enable();
        m_nextAction.action.Enable();

        m_moveAction.action.performed += OnMove;
        m_moveAction.action.canceled += OnMove;
        m_lookAction.action.performed += OnLook;
        m_lookAction.action.canceled += OnLook;
        m_interactAction.action.started += OnInteractStartedHandler;
        m_interactAction.action.performed += OnInteractPerformedHandler;
        m_interactAction.action.canceled += OnInteractCanceledHandler;
        m_sprintAction.action.performed += OnSprintPerformed;
        m_sprintAction.action.canceled += OnSprintCanceled;
        m_attackAction.action.started += OnAttackStartedHandler;
        m_attackAction.action.canceled += OnAttackCanceledHandler;
        m_previousAction.action.performed += OnPreviousItemHandler;
        m_nextAction.action.performed += OnNextItemHandler;
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner)
            return;

        m_moveAction.action.performed -= OnMove;
        m_moveAction.action.canceled -= OnMove;
        m_lookAction.action.performed -= OnLook;
        m_lookAction.action.canceled -= OnLook;
        m_interactAction.action.started -= OnInteractStartedHandler;
        m_interactAction.action.performed -= OnInteractPerformedHandler;
        m_interactAction.action.canceled -= OnInteractCanceledHandler;
        m_sprintAction.action.performed -= OnSprintPerformed;
        m_sprintAction.action.canceled -= OnSprintCanceled;
        m_attackAction.action.started -= OnAttackStartedHandler;
        m_attackAction.action.canceled -= OnAttackCanceledHandler;
        m_previousAction.action.performed -= OnPreviousItemHandler;
        m_nextAction.action.performed -= OnNextItemHandler;

        m_moveAction.action.Disable();
        m_lookAction.action.Disable();
        m_interactAction.action.Disable();
        m_sprintAction.action.Disable();
        m_attackAction.action.Disable();
        m_previousAction.action.Disable();
        m_nextAction.action.Disable();
    }

    private void OnMove(InputAction.CallbackContext ctx) => MoveInput = ctx.ReadValue<Vector2>();

    private void OnLook(InputAction.CallbackContext ctx) => LookInput = ctx.ReadValue<Vector2>();

    private void OnInteractStartedHandler(InputAction.CallbackContext ctx) =>
        OnInteractStarted?.Invoke();

    private void OnInteractPerformedHandler(InputAction.CallbackContext ctx) =>
        OnInteractPerformed?.Invoke();

    private void OnInteractCanceledHandler(InputAction.CallbackContext ctx) =>
        OnInteractCanceled?.Invoke();

    private void OnSprintPerformed(InputAction.CallbackContext ctx) => IsSprinting = true;

    private void OnSprintCanceled(InputAction.CallbackContext ctx) => IsSprinting = false;

    private void OnAttackStartedHandler(InputAction.CallbackContext ctx) =>
        OnAttackStarted?.Invoke();

    private void OnAttackCanceledHandler(InputAction.CallbackContext ctx) =>
        OnAttackCanceled?.Invoke();

    private void OnPreviousItemHandler(InputAction.CallbackContext ctx) => OnPreviousItem?.Invoke();

    private void OnNextItemHandler(InputAction.CallbackContext ctx) => OnNextItem?.Invoke();
}
