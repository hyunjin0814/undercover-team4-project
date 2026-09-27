using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 오너의 CharacterController 이동 — 걷기·달리기·중력·점프·넉백을 처리하고 시점·추종 이동을 구동한다.
/// 서버의 재배치·순간이동 요청을 오너에게 전달해 적용한다.
/// </summary>
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerMovement : NetworkBehaviour
{
    [Header("튜닝")]
    [Tooltip("이동 수치 모음 — 속도·중력·넉백·마찰 (#967)")]
    [SerializeField] private PlayerMovementConfig m_config;

    private PlayerMovementConfig m_fallbackConfig;

    private PlayerMovementConfig Config
    {
        get
        {
            if (m_config != null)
                return m_config;

            if (m_fallbackConfig == null)
            {
                m_fallbackConfig = ScriptableObject.CreateInstance<PlayerMovementConfig>();
                m_fallbackConfig.hideFlags = HideFlags.HideAndDontSave;
                Debug.LogError("PlayerMovement: 이동 Config가 연결되지 않았다 — 코드 기본값으로 대체한다", this);
            }

            return m_fallbackConfig;
        }
    }

    private const float k_groundedStickVelocity = -2f;

    private const float k_steepSlideSpeed = 3f;

    public float MoveSpeed => Config.MoveSpeed * SpeedFactor;
    public float SprintSpeed => Config.SprintSpeed * SpeedFactor;
    public float CrouchSpeed => Config.CrouchSpeed * SpeedFactor;

    public float SpeedFactor =>
        (m_dragLoad != null ? m_dragLoad.DragSpeedFactor : 1f) * BuffSpeedFactor;

    private readonly NetworkVariable<float> m_buffSpeedFactorSynced = new NetworkVariable<float>(1f);
    private float m_buffSpeedFactor = 1f;
    private float m_buffEndTime;
    private bool m_buffTickHooked;

    public float BuffSpeedFactor =>
        IsSpawned && !IsServer ? m_buffSpeedFactorSynced.Value : m_buffSpeedFactor;

    /// <summary>속도 버프를 건다. 겹치면 큰 배율과 늦은 만료를 남긴다. 서버(또는 오프라인) 전용.</summary>
    public void ServerApplySpeedBuff(float multiplier, float seconds)
    {
        if (IsSpawned && !IsServer)
            return;
        if (multiplier <= 0f || seconds <= 0f)
            return;

        SetBuffSpeedFactor(Mathf.Max(m_buffSpeedFactor, multiplier));
        m_buffEndTime = Mathf.Max(m_buffEndTime, Time.time + seconds);
    }

    private void TickSpeedBuff()
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_buffEndTime <= 0f || Time.time < m_buffEndTime)
            return;

        m_buffEndTime = 0f;
        SetBuffSpeedFactor(1f);
    }

    private void OnServerBuffTick() => TickSpeedBuff();

    private void SetBuffSpeedFactor(float factor)
    {
        m_buffSpeedFactor = factor;
        if (IsSpawned && IsServer)
            m_buffSpeedFactorSynced.Value = factor;
    }

    private readonly NetworkVariable<Vector3> m_serverSpawnPosition = new NetworkVariable<Vector3>();
    private readonly NetworkVariable<Quaternion> m_serverSpawnRotation = new NetworkVariable<Quaternion>(
        Quaternion.identity
    );

    private readonly NetworkVariable<int> m_repositionEpoch = new NetworkVariable<int>();
    private int m_appliedRepositionEpoch;

    public bool IsRepositionApplied => !IsSpawned || m_appliedRepositionEpoch >= m_repositionEpoch.Value;

    private CharacterController m_controller;
    private PlayerInputHandler m_inputHandler;
    private PlayerIncapacitation m_incapacitation;
    private PlayerCrouch m_crouch;
    private PlayerJump m_jump;
    private RopeDragLoad m_dragLoad;
    private PlayerTowedMotion m_towed;
    private PlayerLook m_look;
    private PlayerRagdoll m_ragdoll;

    private Unity.Netcode.Components.NetworkTransform m_netTransform;

    private RoundManager Round => App.Game.Round;
    private float m_verticalVelocity;
    private Vector3 m_knockbackVelocity;
    private Vector3 m_currentHorizontalVelocity;
    private bool m_ignoreRoundEndFreeze;

    private SnowEvent m_snowEvent;

    private SnowEvent Snow =>
        m_snowEvent != null ? m_snowEvent : m_snowEvent = App.Game.SuddenEvent?.GetEvent<SnowEvent>();

    private Vector3 m_groundNormal = Vector3.up;
    private bool m_hasGroundContact;

    private bool IsIncapacitated => m_incapacitation != null && m_incapacitation.IsIncapacitated;

    private bool IsOnSteepSurface =>
        m_hasGroundContact && Vector3.Angle(m_groundNormal, Vector3.up) > m_controller.slopeLimit;

    private bool IsStablyGrounded => m_controller.isGrounded && !IsOnSteepSurface;

    internal bool IsRoundOver => Round != null && Round.GameplayFrozen && !m_ignoreRoundEndFreeze;

    /// <summary>이 플레이어만 라운드 종료 정지를 무시할지 설정한다.</summary>
    public void SetIgnoreRoundEndFreeze(bool ignore) => m_ignoreRoundEndFreeze = ignore;

    private bool m_viewLocked;

    private bool m_isLocalOwner;

    /// <summary>카메라를 뺏는 연출 동안 이동을 잠근다 — <see cref="PlayerTerminalFocus"/>가 짝을 맞춰 부른다.</summary>
    public void SetViewLocked(bool locked) => m_viewLocked = locked;

    private bool IsMovementLocked => IsIncapacitated || IsRoundOver || m_viewLocked;

    private bool IsCrouching => m_crouch != null && m_crouch.IsCrouching;

    private void Awake()
    {
        m_controller = GetComponent<CharacterController>();
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_crouch = GetComponent<PlayerCrouch>();
        m_jump = GetComponent<PlayerJump>();
        m_dragLoad = GetComponent<RopeDragLoad>();
        m_towed = GetComponent<PlayerTowedMotion>();
        m_look = GetComponent<PlayerLook>();
        m_ragdoll = GetComponent<PlayerRagdoll>();
        m_netTransform = GetComponent<Unity.Netcode.Components.NetworkTransform>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            m_serverSpawnPosition.Value = transform.position;
            m_serverSpawnRotation.Value = transform.rotation;
        }

        m_look?.ApplyOwnerView(IsOwner);

        m_isLocalOwner = IsOwner;

        if (!IsOwner)
        {
            if (IsServer && NetworkManager != null)
            {
                NetworkManager.NetworkTickSystem.Tick += OnServerBuffTick;
                m_buffTickHooked = true;
            }

            enabled = false;
            return;
        }

        ApplyServerSpawnPose();

        CursorLock.SetGameplayActive(true);
    }

    public override void OnDestroy()
    {
        if (m_fallbackConfig != null)
            Destroy(m_fallbackConfig);

        base.OnDestroy();
    }

    public override void OnNetworkDespawn()
    {
        if (m_buffTickHooked)
        {
            if (NetworkManager != null)
                NetworkManager.NetworkTickSystem.Tick -= OnServerBuffTick;
            m_buffTickHooked = false;
        }

        if (m_isLocalOwner)
        {
            CursorLock.SetGameplayActive(false);

            m_towed?.StopAll();
        }
    }

    private void ApplyServerSpawnPose()
    {
        SetPose(m_serverSpawnPosition.Value, m_serverSpawnRotation.Value);

        m_appliedRepositionEpoch = m_repositionEpoch.Value;

        Debug.Log($"[PlayerMovement] 서버 지정 스폰 포즈 적용 — Owner {OwnerClientId}, 위치 {transform.position}");
    }

    /// <summary>씬 진입 시 플레이어를 스폰 포인트로 재배치한다. 원격은 오너 RPC로 적용한다. 서버 전용.</summary>
    public void ServerReposition(Vector3 position, Quaternion rotation)
    {
        if (!IsServer)
            return;

        m_serverSpawnPosition.Value = position;
        m_serverSpawnRotation.Value = rotation;

        int epoch = m_repositionEpoch.Value + 1;
        m_repositionEpoch.Value = epoch;

        if (IsOwner)
        {
            EndRagdollForReposition();
            SetPose(position, rotation);
            m_appliedRepositionEpoch = epoch;
        }
        else
        {
            ApplyPoseRpc(position, rotation, epoch, endRagdoll: true);
        }
    }

    /// <summary>게임 도중 플레이어를 지정 위치로 순간이동한다. 원격은 오너 RPC로 적용한다. 서버 전용.</summary>
    public void ServerTeleport(Vector3 position, Quaternion rotation)
    {
        if (IsSpawned && !IsServer)
            return;

        if (IsSpawned)
        {
            m_serverSpawnPosition.Value = position;
            m_serverSpawnRotation.Value = rotation;
            ApplyPoseRpc(position, rotation, m_repositionEpoch.Value, endRagdoll: false);
        }
        else
        {
            SetPose(position, rotation);
        }
    }

    [Rpc(SendTo.Owner)]
    private void ApplyPoseRpc(Vector3 position, Quaternion rotation, int epoch, bool endRagdoll)
    {
        if (endRagdoll)
            EndRagdollForReposition();

        SetPose(position, rotation);
        m_appliedRepositionEpoch = epoch;
    }

    /// <summary>재배치 직전에 래그돌을 끝낸다. SetPose보다 먼저 호출해야 한다.</summary>
    private void EndRagdollForReposition() => m_ragdoll?.ExitForReposition();

    /// <summary>수평 이동에 중력을 더해 적용한다(운반 추종용).</summary>
    internal void MoveWithGravity(Vector3 horizontalStep)
    {
        IntegrateGravity();
        MoveAndTrackGround(horizontalStep + Vector3.up * m_verticalVelocity * Time.deltaTime);
    }

    /// <summary>CharacterController를 이동시키며 밟은 면을 기록한다.</summary>
    private void MoveAndTrackGround(Vector3 displacement)
    {
        m_hasGroundContact = false;
        m_controller.Move(displacement);
    }

    /// <summary>부딪힌 면 중 가장 바닥에 가까운 것을 이번 프레임 지면 후보로 기록한다.</summary>
    private void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (hit.normal.y <= 0f)
        {
            return;
        }

        if (!m_hasGroundContact || hit.normal.y > m_groundNormal.y)
        {
            m_groundNormal = hit.normal;
            m_hasGroundContact = true;
        }
    }

    private void IntegrateGravity()
    {
        if (IsStablyGrounded && m_verticalVelocity < 0f)
        {
            m_verticalVelocity = k_groundedStickVelocity;
        }

        m_verticalVelocity += Config.Gravity * Time.deltaTime;
    }

    /// <summary>쌓인 넉백과 수직 속도를 지운다.</summary>
    internal void ClearExternalVelocity()
    {
        m_knockbackVelocity = Vector3.zero;
        m_verticalVelocity = 0f;
        m_currentHorizontalVelocity = Vector3.zero;
    }

    /// <summary>CharacterController를 켜고 끈다(호송 추종용).</summary>
    internal void SetControllerEnabled(bool value) => SetCapsuleEnabled(value);

    /// <summary>캡슐 활성 상태를 바꾸는 유일한 통로 — 켜질 때 래그돌 충돌 무시를 다시 건다.</summary>
    private void SetCapsuleEnabled(bool value)
    {
        m_controller.enabled = value;
        if (value)
            m_ragdoll?.ReapplyCapsuleIgnore();
    }

    private void SetPose(Vector3 pos, Quaternion rot)
    {
        Vector3 bodyDelta = pos - transform.position;

        SetCapsuleEnabled(false);
        transform.SetPositionAndRotation(pos, rot);

        m_ragdoll?.PlaceBodyBy(bodyDelta);

        SetCapsuleEnabled(true);

        if (IsSpawned && IsOwner)
            m_netTransform?.Teleport(transform.position, transform.rotation, transform.localScale);

        m_verticalVelocity = 0f;
        m_currentHorizontalVelocity = Vector3.zero;

        m_look?.SnapViewBlend();
    }

    private void Update()
    {
        TickSpeedBuff();

        if (!IsSpawned || IsOwner)
            App.UI.SpeedVignette?.UpdateSpeed(m_currentHorizontalVelocity.magnitude, SprintSpeed);

        if (m_ragdoll != null && m_ragdoll.IsCapsuleFollowingBody)
        {
            m_look?.HandleLook();
            m_look?.UpdateCameraPose();
            return;
        }

        if (m_towed != null && m_towed.IsActive)
        {
            m_look?.HandleLook();
            m_towed.Tick();
            m_look?.UpdateCameraPose();
            return;
        }

        m_look?.HandleLook();
        m_look?.UpdateCameraPose();
        HandleMove();
    }

    /// <summary>외력으로 밀어낸다 — 폭발 넉백 등(<see cref="BombExplosionView"/>). 세기는 m/s 단위 속도로 준다.</summary>
    public void AddKnockback(Vector3 velocity)
    {
        if (IsSpawned && !IsOwner) return;

        if (m_towed != null && m_towed.IsActive) return;
        if (m_ragdoll != null && m_ragdoll.IsRagdollActive) return;

        m_knockbackVelocity += new Vector3(velocity.x, 0f, velocity.z);

        if (velocity.y > 0f)
            m_verticalVelocity = Mathf.Max(m_verticalVelocity, velocity.y);
    }

    private void HandleMove()
    {
        Vector2 input = IsMovementLocked ? Vector2.zero : m_inputHandler.MoveInput;
        Vector3 moveDirection = (
            transform.right * input.x + transform.forward * input.y
        ).normalized;

        bool grounded = IsStablyGrounded;

        IntegrateGravity();

        if (m_jump != null)
        {
            if (m_jump.ConsumeJumpRequest() && grounded && !IsMovementLocked)
            {
                m_verticalVelocity = Mathf.Sqrt(
                    2f * m_jump.JumpHeight * Mathf.Max(-Config.Gravity, 0.01f)
                );
            }
        }

        float speed = IsCrouching ? CrouchSpeed
            : m_inputHandler.IsSprinting ? SprintSpeed
            : MoveSpeed;

        Vector3 targetVelocity = moveDirection * speed;

        if (m_dragLoad != null)
            targetVelocity = m_dragLoad.ConstrainByTautRopes(targetVelocity);

        if (m_controller.isGrounded && IsOnSteepSurface)
        {
            Vector3 awayFromSurface = new Vector3(m_groundNormal.x, 0f, m_groundNormal.z).normalized;
            targetVelocity =
                Vector3.ProjectOnPlane(targetVelocity, awayFromSurface)
                + awayFromSurface * k_steepSlideSpeed;
        }

        SnowEvent snow = Snow;
        float iceRatio = snow != null
            ? snow.IceRatioAt(transform.position + Vector3.up * WeatherShelter.k_bodyProbeHeight)
            : 0f;
        float currentFriction = Mathf.Lerp(Config.DefaultFriction, Config.SnowFriction, iceRatio);

        m_currentHorizontalVelocity = Vector3.Lerp(m_currentHorizontalVelocity, targetVelocity, currentFriction * Time.deltaTime);

        Vector3 velocity = m_currentHorizontalVelocity + m_knockbackVelocity + Vector3.up * m_verticalVelocity;
        MoveAndTrackGround(velocity * Time.deltaTime);

        if ((m_controller.collisionFlags & CollisionFlags.Above) != 0 && m_verticalVelocity > 0f)
        {
            m_verticalVelocity = 0f;
        }

        if (m_jump != null)
        {
            m_jump.ReportGrounded(IsStablyGrounded);
        }

        m_knockbackVelocity *= Mathf.Exp(-Config.KnockbackDamping * Time.deltaTime);
        if (m_knockbackVelocity.sqrMagnitude < 0.01f)
            m_knockbackVelocity = Vector3.zero;
    }
}