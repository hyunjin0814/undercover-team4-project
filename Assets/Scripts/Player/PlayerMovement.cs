using UnityEngine;
using UnityEngine.InputSystem;

// using Unity.Netcode; // TODO: 네트워크 테스트 시 주석 해제

[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerMovement : MonoBehaviour // TODO: 네트워크 테스트 시 NetworkBehaviour로 복구
{
    [Header("이동")]
    [SerializeField]
    private float m_moveSpeed = 5f;

    [SerializeField]
    private float m_sprintSpeed = 8f;

    [SerializeField]
    private float m_gravity = -9.81f;

    [Header("1인칭 시점")]
    [SerializeField]
    private Camera playerCamera;

    [SerializeField]
    private float m_mouseSensitivity = 1f;

    [SerializeField]
    private float m_minPitch = -80f;

    [SerializeField]
    private float m_maxPitch = 80f;

    // [SerializeField] private Transform m_ownBodyRoot; // TODO: 네트워크 테스트 시 주석 해제, 캐릭터 몸(머리) 루트 연결

    private CharacterController m_controller;
    private PlayerInputHandler m_inputHandler;
    private float m_pitch;
    private float m_verticalVelocity;

    private void Awake() // TODO: 네트워크 테스트 시 OnNetworkSpawn으로 복구
    {
        m_controller = GetComponent<CharacterController>();
        m_inputHandler = GetComponent<PlayerInputHandler>();

        // if (!IsOwner)
        // {
        //     playerCamera.gameObject.SetActive(false);
        //     enabled = false;
        //     return;
        // }
        //
        // SetLayerRecursively(m_ownBodyRoot, LayerMask.NameToLayer("OwnBody")); // 내 카메라에서만 안 보이게

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    // TODO: 네트워크 테스트 시 주석 해제
    // private void SetLayerRecursively(Transform root, int layer)
    // {
    //     root.gameObject.layer = layer;
    //     foreach (Transform child in root)
    //     {
    //         SetLayerRecursively(child, layer);
    //     }
    // }

    private void Update()
    {
        HandleLook();
        HandleMove();
        HandleCursorToggle();
    }

    private void HandleCursorToggle()
    {
        if (Keyboard.current == null || !Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            return;
        }

        bool isLocked = Cursor.lockState == CursorLockMode.Locked;
        Cursor.lockState = isLocked ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = isLocked;
    }

    private void HandleLook()
    {
        Vector2 look = m_inputHandler.LookInput * m_mouseSensitivity;

        transform.Rotate(Vector3.up * look.x);

        m_pitch = Mathf.Clamp(m_pitch - look.y, m_minPitch, m_maxPitch);
        playerCamera.transform.localEulerAngles = new Vector3(m_pitch, 0f, 0f);
    }

    private void HandleMove()
    {
        Vector2 input = m_inputHandler.MoveInput;
        Vector3 moveDirection = (
            transform.right * input.x + transform.forward * input.y
        ).normalized;

        if (m_controller.isGrounded && m_verticalVelocity < 0f)
        {
            m_verticalVelocity = -2f;
        }
        m_verticalVelocity += m_gravity * Time.deltaTime;

        float speed = m_inputHandler.IsSprinting ? m_sprintSpeed : m_moveSpeed;
        Vector3 velocity = moveDirection * speed + Vector3.up * m_verticalVelocity;
        m_controller.Move(velocity * Time.deltaTime);
    }
}
