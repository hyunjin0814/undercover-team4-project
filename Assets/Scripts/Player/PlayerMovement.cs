using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerMovement : NetworkBehaviour
{
    [Header("이동")]
    [SerializeField]
    private float m_moveSpeed = 5f;

    [SerializeField]
    private float m_sprintSpeed = 8f;

    [SerializeField]
    private float m_gravity = -9.81f;

    // PlayerAnimationDriver가 속도 정규화에 사용 (실제 속도 ↔ 블렌드 트리 좌표 분리)
    public float MoveSpeed => m_moveSpeed;
    public float SprintSpeed => m_sprintSpeed;

    [Header("1인칭 시점")]
    [SerializeField]
    private Camera playerCamera;

    [SerializeField]
    private float m_mouseSensitivity = 1f;

    [SerializeField]
    private float m_minPitch = -80f;

    [SerializeField]
    private float m_maxPitch = 80f;

    [SerializeField]
    private Transform m_ownBodyRoot; // 내 카메라에서만 안 보이게 할 캐릭터 몸(머리) 루트

    [Header("다운(무력화) 시점")]
    [Tooltip("다운 중 카메라를 낮출 바닥 근처 높이(m)")]
    [SerializeField] private float m_downCamHeight = 0.35f;

    [Tooltip("다운 중 카메라 피치(양수=아래, 음수=위). 바닥에서 살짝 위를 보게 함")]
    [SerializeField] private float m_downCamPitch = -20f;

    [Tooltip("서기↔다운 시점 전환 보간 속도")]
    [SerializeField] private float m_camPoseLerpSpeed = 8f;

    // 서버가 Connection Approval에서 지정한 스폰 포즈. 프리팹의 NetworkTransform이 Owner 권한이라,
    // 씬 동기화를 거쳐 접속하면 오너 로컬 인스턴스가 프리팹 원점에 생성된 채 권한을 잡고 원점
    // 위치를 역전파해 스폰 위치를 덮어쓴다 — 오너가 이 값을 읽어 스스로 스폰 포즈로 이동해 바로잡는다.
    private readonly NetworkVariable<Vector3> m_serverSpawnPosition = new NetworkVariable<Vector3>();
    private readonly NetworkVariable<Quaternion> m_serverSpawnRotation = new NetworkVariable<Quaternion>(
        Quaternion.identity
    );

    private CharacterController m_controller;
    private PlayerInputHandler m_inputHandler;
    private PlayerIncapacitation m_incapacitation; // 다운(무력화) 중 이동·시점 차단용 (#105)
    private RoundManager m_roundManager; // 라운드 종료 시 이동·시점 차단용 (라운드 종료 freeze)
    private float m_pitch;
    private float m_standCamHeight; // 평소(서기) 카메라 높이 — 프리팹 초기값에서 캡처 (#105)
    private float m_verticalVelocity;
    private bool m_cursorUnlocked; // 임시: OnGUI 버튼 조작용 커서 해제 상태

    // 다운(무력화) 중 여부 — 무력화 컴포넌트가 없으면(테스트 구성 등) 항상 false
    private bool IsIncapacitated => m_incapacitation != null && m_incapacitation.IsIncapacitated;

    // 라운드 종료로 정지(freeze)됐는지 — RoundManager가 없으면(단독 테스트 씬) 항상 false
    private bool IsRoundOver => m_roundManager != null && m_roundManager.GameplayFrozen;

    // 이동·시점을 막아야 하는 상태 — 다운(무력화) 또는 라운드 종료
    private bool IsMovementLocked => IsIncapacitated || IsRoundOver;

    private void Awake()
    {
        m_controller = GetComponent<CharacterController>();
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_roundManager = FindFirstObjectByType<RoundManager>(); // 씬에 하나 — 없으면 단독 테스트 씬

        if (playerCamera != null)
        {
            m_standCamHeight = playerCamera.transform.localPosition.y; // 서기 시점 높이 기준값
        }
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            // 서버 인스턴스는 Approval이 지정한 위치에 생성된다 — 이 포즈가 오너에게 초기 동기화된다
            m_serverSpawnPosition.Value = transform.position;
            m_serverSpawnRotation.Value = transform.rotation;
        }

        if (!IsOwner)
        {
            playerCamera.gameObject.SetActive(false);
            enabled = false;
            return;
        }

        ApplyServerSpawnPose();

        if (m_ownBodyRoot != null)
        {
            SetLayerRecursively(m_ownBodyRoot, LayerMask.NameToLayer("OwnBody")); // 내 카메라에서만 안 보이게
        }

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    // 오너 로컬 인스턴스를 서버가 지정한 스폰 포즈로 이동시킨다. CharacterController가 켜진
    // 상태에서 transform을 직접 옮기면 내부 캐시가 위치를 되돌릴 수 있어 잠시 끄고 옮긴다.
    private void ApplyServerSpawnPose()
    {
        m_controller.enabled = false;
        transform.SetPositionAndRotation(m_serverSpawnPosition.Value, m_serverSpawnRotation.Value);
        m_controller.enabled = true;

        Debug.Log(
            $"[PlayerMovement] 서버 지정 스폰 포즈 적용 — Owner {OwnerClientId}, 위치 {transform.position}"
        );
    }

    private void SetLayerRecursively(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        foreach (Transform child in root)
        {
            SetLayerRecursively(child, layer);
        }
    }

    private void Update()
    {
        // 임시: ESC로 커서 잠금/해제 토글 — OnGUI 버튼 조작용.
        // lockState를 명시적으로 None으로 바꿔야 클릭 시 엔진이 재잠금하지 않는다.
        // 정식 UI(메뉴/로비)가 들어오면 그쪽 시스템으로 옮기고 이 블록은 제거할 것.
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            m_cursorUnlocked = !m_cursorUnlocked;
            Cursor.lockState = m_cursorUnlocked ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = m_cursorUnlocked;
        }

        if (!m_cursorUnlocked)
        {
            HandleLook(); // 커서 해제 중에는 시점 회전 정지 (마우스 이동이 화면을 돌리지 않게)
        }

        UpdateCameraPose(); // 카메라 높이/피치를 매 프레임 적용 (다운 시 바닥 시점) (#105)
        HandleMove();
    }

    private void HandleLook()
    {
        if (IsMovementLocked) return; // 다운 중·라운드 종료 시 시점 회전 차단 — 카메라 적용은 UpdateCameraPose가 담당

        Vector2 look = m_inputHandler.LookInput * m_mouseSensitivity;

        transform.Rotate(Vector3.up * look.x);

        m_pitch = Mathf.Clamp(m_pitch - look.y, m_minPitch, m_maxPitch);
    }

    // 카메라 위치(높이)와 피치를 적용한다. 다운 중에는 바닥 근처 높이 + 상방 시선으로 부드럽게 눕히고,
    // 평소에는 서기 높이에서 시선 입력(m_pitch)을 그대로 반영한다. 구조되면 원위치로 복귀한다. (#105)
    private void UpdateCameraPose()
    {
        if (playerCamera == null) return;

        float lerp = m_camPoseLerpSpeed * Time.deltaTime;
        bool downed = IsIncapacitated;

        Vector3 localPos = playerCamera.transform.localPosition;
        localPos.y = Mathf.Lerp(localPos.y, downed ? m_downCamHeight : m_standCamHeight, lerp);
        playerCamera.transform.localPosition = localPos;

        // 다운 중엔 시선 입력이 멈추므로(HandleLook 차단) 피치를 바닥 시점으로 눕힌다.
        // (m_pitch를 함께 옮겨두면 구조 후에도 그 각도에서 자연스럽게 이어진다)
        if (downed)
        {
            m_pitch = Mathf.Lerp(m_pitch, m_downCamPitch, lerp);
        }
        playerCamera.transform.localEulerAngles = new Vector3(m_pitch, 0f, 0f);
    }

    private void HandleMove()
    {
        // 다운 중·라운드 종료 시 이동 입력 차단 — 단 중력·접지는 유지해 바닥에 서 있게 한다 (#105, 라운드 종료 freeze)
        Vector2 input = IsMovementLocked ? Vector2.zero : m_inputHandler.MoveInput;
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
