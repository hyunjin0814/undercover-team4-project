using UnityEngine;

/// <summary>
/// 오너 로컬 1인칭 시점 — 마우스 입력으로 몸통 yaw와 카메라 pitch를 돌리고 카메라 높이·자세를 잡는다.
/// PlayerMovement가 매 프레임 호출한다(자체 Update 없음).
/// </summary>
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerLook : MonoBehaviour
{
    [Header("1인칭 시점")]
    [SerializeField]
    private Camera m_playerCamera;

    [SerializeField]
    private Transform m_ownBodyRoot;

    [Header("튜닝")]
    [Tooltip("시점 수치 모음 — 감도·피치 범위·다운/감정표현 카메라 (#967)")]
    [SerializeField] private PlayerLookConfig m_config;

    private PlayerLookConfig m_fallbackConfig;

    private PlayerLookConfig Config
    {
        get
        {
            if (m_config != null)
                return m_config;

            if (m_fallbackConfig == null)
            {
                m_fallbackConfig = ScriptableObject.CreateInstance<PlayerLookConfig>();
                m_fallbackConfig.hideFlags = HideFlags.HideAndDontSave;
                Debug.LogError("PlayerLook: 시점 Config가 연결되지 않았다 — 코드 기본값으로 대체한다", this);
            }

            return m_fallbackConfig;
        }
    }

    [Tooltip("3인칭 카메라 충돌 판정에 쓸 레이어 — 플레이어·트리거는 빼 둘 것")]
    [SerializeField] private LayerMask m_emoteCamCollision = ~0;

    private PlayerMovement m_movement;
    private PlayerInputHandler m_inputHandler;
    private PlayerIncapacitation m_incapacitation;
    private PlayerCrouch m_crouch;
    private PlayerJump m_jump;
    private PlayerHandView m_handView;
    private PlayerSpectateCamera m_spectate;
    private PlayerTerminalFocus m_terminalFocus;

    private float m_pitch;
    private Vector2 m_smoothedLook;
    private float m_standCamHeight;
    private Vector2 m_camBaseLateral;
    private float m_camCrouchDrop;
    private float m_downCamBlend;
    private float m_downYaw;
    private bool m_downLookTaken;
    private int m_lookSuspendCount;
    private bool m_emoteView;
    private float m_emoteCamBlend;
    private float m_emoteYaw;
    private bool m_spectateView;
    private bool m_spectateShown;
    private int m_ownBodyLayer = -1;

    public float Pitch => m_pitch;

    private bool IsProne => m_incapacitation != null && m_incapacitation.IsProne;

    private float CrouchHeadDrop => m_crouch != null ? m_crouch.HeadDrop : 0f;

    private void Awake()
    {
        m_movement = GetComponent<PlayerMovement>();
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_crouch = GetComponent<PlayerCrouch>();
        m_jump = GetComponent<PlayerJump>();
        m_handView = GetComponent<PlayerHandView>();
        m_spectate = GetComponent<PlayerSpectateCamera>();
        m_terminalFocus = GetComponent<PlayerTerminalFocus>();

        if (m_playerCamera != null)
        {
            m_standCamHeight = m_playerCamera.transform.localPosition.y;
            m_camBaseLateral = new Vector2(
                m_playerCamera.transform.localPosition.x,
                m_playerCamera.transform.localPosition.z
            );
        }
    }

    /// <summary>소유권에 따라 카메라를 켜고 끄며, 내 몸을 내 카메라에서 숨긴다.</summary>
    public void ApplyOwnerView(bool isOwner)
    {
        if (!isOwner)
        {
            if (m_playerCamera != null)
            {
                m_playerCamera.gameObject.SetActive(false);
            }

            return;
        }

        if (m_ownBodyRoot != null)
        {
            SetLayerRecursively(m_ownBodyRoot, LayerMask.NameToLayer("OwnBody"));
        }
    }

    /// <summary>감정표현 재생 중 3인칭 시점을 켜고 끈다.</summary>
    public void SetEmoteView(bool active)
    {
        if (m_emoteView == active)
            return;

        m_emoteView = active;
        ApplyThirdPersonView();
    }

    private void SetSpectateView(bool active)
    {
        if (m_spectate == null || m_spectateView == active)
            return;

        m_spectateView = active;

        m_spectate.SetSpectating(
            active,
            m_playerCamera != null ? m_playerCamera.transform.eulerAngles.y : transform.eulerAngles.y
        );
    }

    private const float k_spectateShowBlend = 0.2f;

    /// <summary>블렌드 진행도에 맞춰 관전 표시를 켜고 끈다.</summary>
    private void ShowSpectateView(bool shown)
    {
        if (m_spectateShown == shown)
            return;

        m_spectateShown = shown;
        ApplyThirdPersonView();
    }

    /// <summary>3인칭에 딸린 표시(내 몸 컬링 복원·1인칭 팔 숨김)를 함께 맞춘다.</summary>
    private void ApplyThirdPersonView()
    {
        bool thirdPerson = m_emoteView || m_spectateShown;

        m_handView?.SetViewmodelVisible(!thirdPerson);

        if (m_playerCamera == null)
            return;

        if (m_ownBodyLayer < 0)
            m_ownBodyLayer = LayerMask.NameToLayer("OwnBody");

        if (m_ownBodyLayer < 0)
            return;

        int mask = 1 << m_ownBodyLayer;
        if (thirdPerson)
            m_playerCamera.cullingMask |= mask;
        else
            m_playerCamera.cullingMask &= ~mask;
    }

    /// <summary>시점 보간을 끊고 다음 프레임에 현재 상태를 즉시 반영한다.</summary>
    public void SnapViewBlend() => m_spectate?.SnapNextTick();

    /// <summary>시점 회전만 잠시 멈춘다(감정표현 휠 등).</summary>
    public void PushLookSuspend() => m_lookSuspendCount++;

    /// <summary>시점 회전 정지 요청을 하나 거둔다 — <see cref="PushLookSuspend"/>와 반드시 짝을 지어 부른다.</summary>
    public void PopLookSuspend() => m_lookSuspendCount = Mathf.Max(0, m_lookSuspendCount - 1);

    /// <summary>프레임률 독립적인 보간 계수를 계산한다. rate가 0이면 즉시.</summary>
    private static float Damp(float rate) =>
        rate <= 0f ? 1f : 1f - Mathf.Exp(-rate * Time.deltaTime);

    /// <summary>마우스 입력으로 시점을 돌린다 — 평상시엔 몸통 yaw + 카메라 pitch, 쓰러진 동안엔 카메라 로컬만.</summary>
    public void HandleLook()
    {
        if ((m_movement != null && m_movement.IsRoundOver) || CursorLock.IsUnlocked || m_lookSuspendCount > 0)
        {
            m_smoothedLook = Vector2.zero;
            return;
        }

        Vector2 look = m_inputHandler.LookInput * Config.MouseSensitivity * GameSettings.MouseSensitivity;

        m_smoothedLook = Vector2.Lerp(m_smoothedLook, look, Damp(GameSettings.LookSmoothingRate));

        if (m_spectateView)
        {
            m_spectate.AddLook(m_smoothedLook);
            return;
        }

        if (IsProne)
        {
            if (m_smoothedLook.sqrMagnitude > 0.0001f)
                m_downLookTaken = true;

            m_downYaw = Mathf.Clamp(
                m_downYaw + m_smoothedLook.x, -Config.DownYawRange, Config.DownYawRange);
            m_pitch = Mathf.Clamp(m_pitch - m_smoothedLook.y, Config.DownMinPitch, Config.DownMaxPitch);
            return;
        }

        if (m_emoteView)
        {
            m_emoteYaw += m_smoothedLook.x;
            m_pitch = Mathf.Clamp(m_pitch - m_smoothedLook.y, Config.MinPitch, Config.MaxPitch);
            return;
        }

        transform.Rotate(Vector3.up * m_smoothedLook.x);

        m_pitch = Mathf.Clamp(m_pitch - m_smoothedLook.y, Config.MinPitch, Config.MaxPitch);
    }

    /// <summary>카메라 높이·피치를 매 프레임 적용한다 — 다운 시 바닥 시점, 앉기 시 하강.</summary>
    public void UpdateCameraPose()
    {
        if (m_playerCamera == null) return;

        if (!Mathf.Approximately(m_playerCamera.fieldOfView, GameSettings.Fov))
            m_playerCamera.fieldOfView = GameSettings.Fov;

        float lerp = Damp(Config.CamPoseLerpSpeed);
        bool downed = IsProne;

        bool dead = m_incapacitation != null && m_incapacitation.IsDead;

        if (!dead && !downed)
            m_spectate?.ClearPivotOverride();

        SetSpectateView(dead || (m_spectate != null && m_spectate.HasPivotOverride));

        m_downCamBlend = Mathf.Lerp(m_downCamBlend, downed ? 1f : 0f, lerp);

        if (m_crouch == null)
        {
            m_camCrouchDrop = 0f;
        }
        else if (m_jump == null || !m_jump.IsAirborne)
        {
            m_camCrouchDrop = Mathf.MoveTowards(
                m_camCrouchDrop,
                CrouchHeadDrop,
                m_crouch.HeadDropRate * Time.deltaTime
            );
        }

        float uprightHeight = m_standCamHeight - m_camCrouchDrop;

        Vector3 localPos = new Vector3(
            m_camBaseLateral.x,
            Mathf.Lerp(uprightHeight, Config.DownCamHeight, m_downCamBlend),
            m_camBaseLateral.y
        );

        if (downed && !m_downLookTaken)
        {
            m_pitch = Mathf.Lerp(m_pitch, Config.DownCamPitch, lerp);
        }

        if (!downed)
        {
            m_downYaw = Mathf.Lerp(m_downYaw, 0f, lerp);
            m_downLookTaken = false;
            m_pitch = Mathf.Clamp(m_pitch, Config.MinPitch, Config.MaxPitch);
        }

        m_emoteCamBlend = Mathf.Lerp(m_emoteCamBlend, m_emoteView && !downed ? 1f : 0f, Damp(Config.EmoteCamLerpSpeed));

        if (!m_emoteView && m_emoteCamBlend < 0.01f)
        {
            m_emoteYaw = 0f;
        }
        else if (m_emoteCamBlend > 0.001f)
        {
            Vector3 boom = Quaternion.Euler(0f, m_emoteYaw, 0f)
                * new Vector3(0f, Config.EmoteCamHeight, -Config.EmoteCamDistance);

            Vector3 pivot = transform.TransformPoint(localPos);
            Vector3 direction = transform.TransformDirection(boom);
            float distance = direction.magnitude;
            if (distance > 0.001f)
            {
                direction /= distance;
                if (Physics.SphereCast(pivot, Config.EmoteCamProbeRadius, direction, out RaycastHit hit,
                        distance, m_emoteCamCollision, QueryTriggerInteraction.Ignore))
                {
                    boom = boom.normalized * Mathf.Max(hit.distance - Config.EmoteCamProbeRadius, 0f);
                }
            }

            localPos += boom * m_emoteCamBlend;
        }

        Vector3 euler = new Vector3(m_pitch, m_downYaw + m_emoteYaw * m_emoteCamBlend, 0f);

        if (GameSettings.ScreenShake && m_shakeIntensity > 0.001f)
        {
            EvaluateShake(out Vector3 shakeEuler, out Vector3 shakeOffset);
            localPos += shakeOffset;
            euler += shakeEuler;
        }

        Quaternion localRot = Quaternion.Euler(euler);

        if (m_terminalFocus != null)
        {
            float focusBlend = m_terminalFocus.Tick();

            if (focusBlend > 0.001f
                && m_terminalFocus.TryGetPose(out Vector3 focusPos, out Quaternion focusRot))
            {
                localPos = Vector3.Lerp(localPos, transform.InverseTransformPoint(focusPos), focusBlend);
                localRot = Quaternion.Slerp(localRot, Quaternion.Inverse(transform.rotation) * focusRot, focusBlend);
            }
        }

        if (m_spectate != null)
        {
            float spectateBlend = m_spectate.Tick();
            ShowSpectateView(spectateBlend > k_spectateShowBlend);

            if (spectateBlend > 0.001f
                && m_spectate.TryGetPose(out Vector3 spectatePos, out Quaternion spectateRot))
            {
                localPos = Vector3.Lerp(localPos, transform.InverseTransformPoint(spectatePos), spectateBlend);
                localRot = Quaternion.Slerp(localRot, Quaternion.Inverse(transform.rotation) * spectateRot, spectateBlend);
            }
        }

        m_playerCamera.transform.localPosition = localPos;
        m_playerCamera.transform.localRotation = localRot;
    }

    private const float k_shakeDegrees = 1.6f;
    private const float k_shakeOffset = 0.012f;

    private float m_shakeIntensity;

    /// <summary>카메라 흔들림 강도를 설정한다. 0이면 흔들지 않는다.</summary>
    public void SetShakeIntensity(float intensity) =>
        m_shakeIntensity = Mathf.Clamp01(intensity);

    private void EvaluateShake(out Vector3 euler, out Vector3 offset) =>
        ShockShake.Evaluate(m_shakeIntensity, k_shakeDegrees, k_shakeOffset, out euler, out offset);

    /// <summary>하위 전체의 레이어를 바꾼다.</summary>
    public static void SetLayerRecursively(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        foreach (Transform child in root)
        {
            SetLayerRecursively(child, layer);
        }
    }

    private void OnDestroy()
    {
        if (m_fallbackConfig != null)
            Destroy(m_fallbackConfig);
    }
}
