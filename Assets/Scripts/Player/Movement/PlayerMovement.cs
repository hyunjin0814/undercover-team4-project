using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerMovement : NetworkBehaviour
{
    [Header("튜닝")]
    [Tooltip("이동 수치 모음 — 속도·중력·넉백·마찰 (#967)")]
    [SerializeField] private PlayerMovementConfig m_config;

    // 배선이 빠져도 굴러가게 코드 기본값 인스턴스로 대신한다 — 속도 0으로 얼어붙는 것보다 낫고,
    // 기본값의 정본은 여전히 Config 클래스 하나뿐이라 값이 두 곳으로 갈리지 않는다. (#967)
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
                m_fallbackConfig.hideFlags = HideFlags.HideAndDontSave; // 씬 로드로 안 지워지므로 직접 정리한다
                Debug.LogError("PlayerMovement: 이동 Config가 연결되지 않았다 — 코드 기본값으로 대체한다", this);
            }

            return m_fallbackConfig;
        }
    }

    // 접지 중 유지하는 하향 속도(m/s). 0으로 두면 CharacterController가 경사·계단에서 지면을 놓쳐
    // 접지 판정이 깜빡인다 — 살짝 눌러 붙여 둔다. 천장 상쇄(0으로 죽이기)의 반대쪽 짝이다. (#189)
    private const float k_groundedStickVelocity = -2f;

    // 설 수 없는 가파른 면에서 밀어내는 속도(m/s). 면에서 0.15m만 떨어지면 정상 낙하로 돌아온다
    // (격리벽 실측) — 3m/s면 세 프레임 남짓. 왜 필요한지는 IsStablyGrounded 참고.
    private const float k_steepSlideSpeed = 3f;

    // PlayerAnimationDriver가 속도 정규화에 사용 (실제 속도 ↔ 블렌드 트리 좌표 분리)
    // 실제 이동(HandleMove)도 같은 프로퍼티를 쓴다 — 배율이 걸린 값을 한 곳에서만 내야
    // 애니메이션 블렌드가 실제 속도와 어긋나지 않는다. (#398)
    public float MoveSpeed => Config.MoveSpeed * SpeedFactor;
    public float SprintSpeed => Config.SprintSpeed * SpeedFactor;
    public float CrouchSpeed => Config.CrouchSpeed * SpeedFactor;

    /// <summary>
    /// 이동 속도에 걸린 외부 배율 — 밧줄로 끌고 있는 무게(<see cref="RopeDragLoad.DragSpeedFactor"/>)와
    /// 낙뢰 버프(<see cref="BuffSpeedFactor"/>)를 곱해 합성한다. 연행 컴포넌트가 없으면(단독 테스트 씬)
    /// 무게는 1. 소스가 더 늘어도 여기서 곱하면 된다 — 이 프로퍼티를 거치는 한 애니메이션 정합은
    /// 따라온다. (#398/#227)
    /// </summary>
    public float SpeedFactor =>
        (m_dragLoad != null ? m_dragLoad.DragSpeedFactor : 1f) * BuffSpeedFactor;

    // 낙뢰 속도 버프 (#227) — 서버가 만료를 관리하고 배율만 동기화한다(RopeDragLoad와 같은 구조).
    // 남의 화면 애니메이션도 같은 배율을 써야 걸음이 실제 속도와 어긋나지 않아 NetworkVariable이다.
    private readonly NetworkVariable<float> m_buffSpeedFactorSynced = new NetworkVariable<float>(1f);
    private float m_buffSpeedFactor = 1f;
    private float m_buffEndTime;
    private bool m_buffTickHooked; // 서버가 오너 아닌 캐릭터의 만료를 대신 틱하는 중인가 (#706)

    /// <summary>속도 버프 배율 — 걸려 있지 않으면 1. (#227)</summary>
    public float BuffSpeedFactor =>
        IsSpawned && !IsServer ? m_buffSpeedFactorSynced.Value : m_buffSpeedFactor;

    /// <summary>
    /// 속도 버프를 건다 — 서버(또는 오프라인) 전용. 낙뢰(<see cref="LightningEvent"/>)가 부른다. (#227)
    /// 겹쳐 걸리면 배율은 <b>큰 쪽</b>을, 만료는 <b>늦은 쪽</b>을 남긴다 — 연달아 맞은 사람의 버프가
    /// 약한 값으로 덮이거나 먼저 끊기지 않게 한다.
    /// </summary>
    public void ServerApplySpeedBuff(float multiplier, float seconds)
    {
        if (IsSpawned && !IsServer)
            return;
        if (multiplier <= 0f || seconds <= 0f)
            return;

        SetBuffSpeedFactor(Mathf.Max(m_buffSpeedFactor, multiplier));
        m_buffEndTime = Mathf.Max(m_buffEndTime, Time.time + seconds);
    }

    // 만료를 서버가 센다 — Update 맨 앞이라 래그돌·피견인 분기에 걸려도 버프는 제때 풀린다.
    private void TickSpeedBuff()
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_buffEndTime <= 0f || Time.time < m_buffEndTime)
            return;

        m_buffEndTime = 0f;
        SetBuffSpeedFactor(1f);
    }

    // 오너 아닌 캐릭터용 서버 틱 경로 (#706) — enabled=false라 안 도는 Update() 대신 여기서 만료를 센다.
    private void OnServerBuffTick() => TickSpeedBuff();

    private void SetBuffSpeedFactor(float factor)
    {
        m_buffSpeedFactor = factor;
        if (IsSpawned && IsServer)
            m_buffSpeedFactorSynced.Value = factor;
    }

    // 서버가 Connection Approval에서 지정한 스폰 포즈. 프리팹의 NetworkTransform이 Owner 권한이라,
    // 씬 동기화를 거쳐 접속하면 오너 로컬 인스턴스가 프리팹 원점에 생성된 채 권한을 잡고 원점
    // 위치를 역전파해 스폰 위치를 덮어쓴다 — 오너가 이 값을 읽어 스스로 스폰 포즈로 이동해 바로잡는다.
    private readonly NetworkVariable<Vector3> m_serverSpawnPosition = new NetworkVariable<Vector3>();
    private readonly NetworkVariable<Quaternion> m_serverSpawnRotation = new NetworkVariable<Quaternion>(
        Quaternion.identity
    );

    // 재배치 회차 (#656) — 서버가 ServerReposition마다 올리고 오너가 적용하며 맞춘다.
    // 좌표 비교로는 안 된다: 도착점이 인원수만큼 벌어지고(GetSpreadOffset) 접지 보정으로도 흔들린다.
    private readonly NetworkVariable<int> m_repositionEpoch = new NetworkVariable<int>();
    private int m_appliedRepositionEpoch;

    /// <summary>서버가 지시한 재배치를 적용했는가 — 씬 진입 로딩 화면이 기다린다. 세션 밖이면 항상 참. (#656)</summary>
    public bool IsRepositionApplied => !IsSpawned || m_appliedRepositionEpoch >= m_repositionEpoch.Value;

    private CharacterController m_controller;
    private PlayerInputHandler m_inputHandler;
    private PlayerIncapacitation m_incapacitation; // 다운(무력화) 중 이동·시점 차단용 (#105)
    private PlayerCrouch m_crouch; // 앉기 중 이동 속도·카메라 높이 조정용 (#236)
    private PlayerJump m_jump; // 점프 입력 수집·공중 상태 전파 (#189)
    private RopeDragLoad m_dragLoad; // 끌고 있는 무게로 깎인 이동속도 배율·목줄 제한을 읽는다 (#398)
    private PlayerTowedMotion m_towed; // 남이 내 몸을 옮기는 동안의 추종 — 입력 이동을 대신한다 (#279, #365)
    private PlayerLook m_look; // 시점 회전·카메라 자세 — 몸통 yaw가 이동 방향의 기준이라 여기서 순서를 잡는다
    private PlayerRagdoll m_ragdoll; // 사망 래그돌 — 켜져 있는 동안 외력(넉백)을 삼킨다 (#506)

    // 재배치를 보간 없이 원격에 알리는 통로 — SetPose가 Teleport를 부른다(근거는 저쪽 주석).
    private Unity.Netcode.Components.NetworkTransform m_netTransform;

    private RoundManager Round => App.Game.Round; // 라운드 종료 시 이동·시점 차단용 (라운드 종료 freeze)
    private float m_verticalVelocity;
    private Vector3 m_knockbackVelocity; // 외력으로 밀려나는 수평 속도 — 매 프레임 감쇠 (#232 폭발 넉백)
    private Vector3 m_currentHorizontalVelocity; // 미끄러짐(관성) 구현을 위한 현재 수평 속도 (#227)
    private bool m_ignoreRoundEndFreeze; // 정산 화면을 닫은 로컬 플레이어는 라운드 종료 freeze를 무시하고 움직인다 (#107)

    private SnowEvent m_snowEvent; // 눈 날씨 이벤트 캐싱용 (#227)

    /// <summary>눈 이벤트 — 없을 때만 다시 찾는다. 맵 씬에만 있는데 플레이어는 씬을 넘어 산다 (#864).</summary>
    private SnowEvent Snow =>
        m_snowEvent != null ? m_snowEvent : m_snowEvent = App.Game.SuddenEvent?.GetEvent<SnowEvent>();

    // 이번 Move에서 밟은 면 중 법선이 가장 선 것 — OnControllerColliderHit이 채운다
    private Vector3 m_groundNormal = Vector3.up;
    private bool m_hasGroundContact;

    // 다운(무력화) 중 여부 — 무력화 컴포넌트가 없으면(테스트 구성 등) 항상 false
    private bool IsIncapacitated => m_incapacitation != null && m_incapacitation.IsIncapacitated;

    /// <summary>
    /// 딛고 선 면이 <see cref="CharacterController.slopeLimit"/>보다 가파른가 — 밟은 면이 없으면 false.
    /// </summary>
    private bool IsOnSteepSurface =>
        m_hasGroundContact && Vector3.Angle(m_groundNormal, Vector3.up) > m_controller.slopeLimit;

    /// <summary>
    /// 실제로 딛고 설 수 있는 지면 위인가 — <b>접지를 묻는 곳은 전부 이쪽을 쓴다</b>
    /// (점프 자격·접지 클램프·애니메이션이 갈라지면 "설 수 없는 곳에서 뛰는" 경계가 생긴다).
    ///
    /// <see cref="CharacterController.isGrounded"/>는 캡슐 아랫반구 접촉의 법선에 위쪽 성분이 조금이라도
    /// 있으면 <see cref="CharacterController.slopeLimit"/>과 무관하게 접지로 친다. 맵 콜리전 껍질은
    /// 렌더 메시에서 구운 볼록 껍질이라 벽의 '평평한' 면조차 눕어 있어(격리벽 하단 패널 실측 88.0도,
    /// 법선 y=+0.036) 그 2도로 <b>수직 벽면 위에 서게 된다</b> — 낙하가 통째로 막히고, 접지로 잡히니
    /// 거기서 또 뛰어 점프마다 더 높이 얹힌다. 이 프로젝트의 "벽 타기"가 정확히 이것이다.
    /// 에셋 팩 프리팹의 80~95%가 같은 성질이라 맵을 갈아도 사라지지 않는다.
    /// </summary>
    private bool IsStablyGrounded => m_controller.isGrounded && !IsOnSteepSurface;

    /// <summary>
    /// 라운드 종료로 정지(freeze)됐는지 — RoundManager가 없으면(단독 테스트 씬) 항상 false.
    /// 단 정산 화면을 닫은 로컬 플레이어는 예외 — 남은 카운트다운 동안 자유롭게 움직인다 (#107).
    /// 시점 차단 판정도 같은 값을 써야 해서(<see cref="PlayerLook"/>) 이 컴포넌트가 단독으로 들고 빌려준다 —
    /// 예외 플래그를 켜는 <see cref="SetIgnoreRoundEndFreeze"/>가 여기 있기 때문.
    /// </summary>
    internal bool IsRoundOver => Round != null && Round.GameplayFrozen && !m_ignoreRoundEndFreeze;

    /// <summary>
    /// 라운드 종료 freeze를 이 플레이어에 한해 무시할지 설정한다 — 정산 화면(SettlementPanel)을 닫으면 켜진다.
    /// 다운(무력화) 잠금은 별개라 이 값과 무관하게 유지된다(전원 다운 종료 시 다운 플레이어는 그대로 못 움직임).
    /// </summary>
    public void SetIgnoreRoundEndFreeze(bool ignore) => m_ignoreRoundEndFreeze = ignore;

    // 카메라를 뺏는 연출 동안의 이동 잠금 — 본부 단말 포커스(#689)가 켠다.
    // 시점 정지(PlayerLook.PushLookSuspend)는 말 그대로 시점만 멈추므로 이동은 여기서 따로 막아야 한다.
    private bool m_viewLocked;

    // "내 플레이어인가"를 스폰 시점에 굳힌 값 — 사망 중 소유권이 서버로 넘어가 뒤집힌다 (#774)
    private bool m_isLocalOwner;

    /// <summary>카메라를 뺏는 연출 동안 이동을 잠근다 — <see cref="PlayerTerminalFocus"/>가 짝을 맞춰 부른다. (#689)</summary>
    public void SetViewLocked(bool locked) => m_viewLocked = locked;

    // 이동·시점을 막아야 하는 상태 — 다운(무력화) · 라운드 종료 · 카메라를 뺏긴 연출
    private bool IsMovementLocked => IsIncapacitated || IsRoundOver || m_viewLocked;

    // 앉기 중 여부 — 앉기 컴포넌트가 없으면(테스트 구성 등) 항상 false (#236)
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
            // 서버 인스턴스는 Approval이 지정한 위치에 생성된다 — 이 포즈가 오너에게 초기 동기화된다
            m_serverSpawnPosition.Value = transform.position;
            m_serverSpawnRotation.Value = transform.rotation;
        }

        // 남의 카메라 끄기·내 몸 숨기기는 시점 담당(PlayerLook)이 든다 — 카메라와 몸 루트 참조가 그쪽에 있다.
        m_look?.ApplyOwnerView(IsOwner);

        m_isLocalOwner = IsOwner; // 여기서 굳힌다 — 사망 중 뒤집히는 값이다 (#774, 아래 OnNetworkDespawn)

        if (!IsOwner)
        {
            // 서버가 오너 아닌 캐릭터의 버프 만료를 대신 센다 (#706) — 아래서 컴포넌트를 끄면
            // TickSpeedBuff가 도는 Update()도 함께 멈춘다. 오너 쪽은 Update()가 이미 커버한다.
            if (IsServer && NetworkManager != null)
            {
                NetworkManager.NetworkTickSystem.Tick += OnServerBuffTick;
                m_buffTickHooked = true;
            }

            enabled = false; // 이동·시점 갱신은 오너만 — PlayerLook·PlayerTowedMotion도 이 Update가 돌린다
            return;
        }

        ApplyServerSpawnPose();

        // 게임플레이 시작 — 커서를 푸는 UI가 없으면 잠긴다. 실제 Cursor 조작은 CursorLock만 한다. (#352)
        CursorLock.SetGameplayActive(true);
    }

    public override void OnDestroy()
    {
        if (m_fallbackConfig != null) // 배선이 빠졌을 때만 만들어진다
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

        // ⚠ <b>IsOwner를 묻지 않는다</b> — 사망하면 소유권이 서버로 넘어가므로(#763 A-1) 호스트에서는
        // 남의 시체가 자기 것으로 보인다. 그 시체가 디스폰될 때 아래가 돌면 <b>살아 있는 호스트의 커서가
        // 풀리고 입력이 꺼진다</b>(#774 — 여기와 PlayerInputHandler가 짝이다). 반대로 내가 죽은 채
        // 디스폰되면 IsOwner가 false라 정리가 아예 안 돌아 #188이 되살아난다. 스폰 때 굳힌 값이 둘을 다 잡는다.
        if (m_isLocalOwner)
        {
            // 오너 로컬 플레이어가 사라지면(라운드 종료 리셋·연결 종료 등) 게임플레이가 끝난 것으로 보고 커서를 푼다.
            // Cursor.lockState는 전역 상태라 씬을 재로드해도 유지되는데, 재로드된 로비 씬에는 이 커서를 풀어 줄
            // PlayerMovement가 없어 커서가 잠긴 채 고착된다 — 마우스로 로비 UI를 못 누르는 원인. (#188)
            // 커서를 푼 UI가 아직 열려 있어도(디스폰 경합) CursorLock이 최종 상태를 단독으로 정한다. (#352)
            CursorLock.SetGameplayActive(false);

            // 끌려가는 도중 정리(라운드 리셋·연결 종료)되면 서버의 종료 지시(StopCarried·내려놓기)가
            // 못 올 수 있다 — CharacterController 비활성 + 추종 상태가 남지 않게 여기서 안전하게 푼다.
            // (#279 리뷰 반영, #365도 같은 사정)
            m_towed?.StopAll();
        }
    }

    private void ApplyServerSpawnPose()
    {
        SetPose(m_serverSpawnPosition.Value, m_serverSpawnRotation.Value);

        // 이 경로도 서버가 지정한 자리다 — 회차를 안 맞추면 로딩 화면이 이미 끝난 재배치를 기다린다 (#656)
        m_appliedRepositionEpoch = m_repositionEpoch.Value;

        Debug.Log($"[PlayerMovement] 서버 지정 스폰 포즈 적용 — Owner {OwnerClientId}, 위치 {transform.position}");
    }

    /// <summary>
    /// 서버 전용 — 플레이어를 지정 스폰 포인트로 재배치한다. (#214 §8-2)
    /// 세션 유지 루프(Shop↔Game)에서 플레이어는 씬을 넘어 이월되므로, 각 씬 진입 시 재배치가 필요하다.
    /// 호스트(서버=오너)는 즉시 SetPose, 원격 클라이언트는 NetworkTransform이 오너 권한이라
    /// ApplyPoseRpc(SendTo.Owner)로 넘겨 오너가 스스로 적용해야 전 피어에 전파된다.
    /// </summary>
    public void ServerReposition(Vector3 position, Quaternion rotation)
    {
        if (!IsServer)
            return;

        m_serverSpawnPosition.Value = position;
        m_serverSpawnRotation.Value = rotation;

        // 회차를 올린 뒤 그 값을 실어 보낸다 — 오너가 적용하면서 같은 값을 기록해야 짝이 맞는다.
        int epoch = m_repositionEpoch.Value + 1;
        m_repositionEpoch.Value = epoch;

        if (IsOwner)
        {
            EndRagdollForReposition(); // SetPose보다 먼저 — 아래 주석 참고
            SetPose(position, rotation); // 호스트(서버=오너): 즉시 적용
            m_appliedRepositionEpoch = epoch;
        }
        else
        {
            // 원격 클라: 오너가 스스로 적용 (NetworkTransform 오너 권한)
            ApplyPoseRpc(position, rotation, epoch, endRagdoll: true);
        }
    }

    /// <summary>
    /// 서버 전용 — 게임 도중 플레이어를 지정 위치로 순간이동한다. (오검거 광장 매달기 #101 등)
    /// NetworkTransform이 오너 권한이라 서버가 원격 클라 위치를 직접 못 바꾼다 —
    /// 오너에게 RPC로 넘겨 오너가 스스로 SetPose하게 한다(호스트 오너는 로컬로 즉시 적용).
    /// ServerReposition은 스폰 직후 재배치(#247) 전용이라, 도중 텔레포트는 이 경로를 쓴다.
    /// </summary>
    public void ServerTeleport(Vector3 position, Quaternion rotation)
    {
        if (IsSpawned && !IsServer)
            return;

        // 늦게 접속하거나 재스폰되는 피어를 위해 서버 지정 포즈도 함께 갱신해 둔다.
        if (IsSpawned)
        {
            m_serverSpawnPosition.Value = position;
            m_serverSpawnRotation.Value = rotation;
            // 회차를 올리지도, 래그돌을 끝내지도 않는다 — 도중 순간이동은 로딩 화면이 기다릴 대상이
            // 아니고, 시체를 옮기는 데도 쓰인다(오검거 매달기).
            ApplyPoseRpc(position, rotation, m_repositionEpoch.Value, endRagdoll: false); // 오너(호스트 포함)가 스스로 적용
        }
        else
        {
            SetPose(position, rotation); // 오프라인 Play 테스트
        }
    }

    // 오너에서만 실행 — NetworkTransform 오너 권한이라 위치 변경은 오너가 해야 전 피어에 전파된다.
    [Rpc(SendTo.Owner)]
    private void ApplyPoseRpc(Vector3 position, Quaternion rotation, int epoch, bool endRagdoll)
    {
        if (endRagdoll)
            EndRagdollForReposition();

        SetPose(position, rotation);
        m_appliedRepositionEpoch = epoch;
    }

    /// <summary>
    /// 재배치 직전에 래그돌을 끝낸다 (#656). 죽어 있으면 몸의 주인이 캡슐이 아니라 시체라
    /// <c>PlayerRagdoll</c>의 캡슐 추종이 매 프레임 루트를 골반으로 되돌린다 — 옮겨 놔도
    /// 끌려가고, 뼈는 리지드바디라 그 좌표가 <b>직전 맵에서 죽은 자리</b>다.
    ///
    /// <b>SetPose보다 먼저</b> 불러야 한다 — 일으키는 쪽이 캡슐을 켜며 시체 자리를 그대로 써서,
    /// 뒤집으면 순간이동이 지워진다. 살아 있으면 무동작이다. 사망 해제가 재배치보다 늦게 와도
    /// 다시 눕지 않는 이유는 <see cref="PlayerRagdoll.ExitForReposition"/>에 있다.
    /// </summary>
    private void EndRagdollForReposition() => m_ragdoll?.ExitForReposition();

    // ---- 추종 컴포넌트(PlayerTowedMotion)와 공유하는 면 ----
    // 수직 속도와 CharacterController의 소유자는 이 컴포넌트다 — 중력·점프·넉백이 모두 같은 채널을
    // 쓰기 때문. 추종 쪽이 직접 만지면 같은 값을 두 컴포넌트가 따로 적분하게 되므로 연산만 빌려준다.

    /// <summary>
    /// 수평 이동만 받아 중력과 함께 적용한다 — 운반 추종(<see cref="PlayerTowedMotion"/>, #365)이 쓴다.
    /// 접지 클램프·중력 적분은 <see cref="HandleMove"/>와 같은 경로(<see cref="IntegrateGravity"/>)를 쓴다.
    /// </summary>
    internal void MoveWithGravity(Vector3 horizontalStep)
    {
        IntegrateGravity();
        MoveAndTrackGround(horizontalStep + Vector3.up * m_verticalVelocity * Time.deltaTime);
    }

    /// <summary>
    /// Move하면서 밟은 면을 기록한다 — 접지 판정(<see cref="IsStablyGrounded"/>)의 재료다.
    /// <b>Move 호출은 이 경로 하나로 모은다</b> — 지난 프레임 기록을 비우지 않으면 묵은 면을 보고 판정한다.
    /// </summary>
    private void MoveAndTrackGround(Vector3 displacement)
    {
        m_hasGroundContact = false;
        m_controller.Move(displacement);
    }

    /// <summary>
    /// 부딪힌 면 중 이번 프레임의 <b>지면 후보</b>를 고른다 — 벽과 바닥에 동시에 닿으면 법선이 가장 선
    /// 쪽(= 진짜 바닥)을 남긴다. 그래야 벽에 붙어 서 있어도 발밑에 바닥이 있으면 정상 접지로 잡힌다.
    /// </summary>
    private void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (hit.normal.y <= 0f)
        {
            return; // 천장이거나 완전 수직 — 지면 후보가 아니다
        }

        if (!m_hasGroundContact || hit.normal.y > m_groundNormal.y)
        {
            m_groundNormal = hit.normal;
            m_hasGroundContact = true;
        }
    }

    // 접지 유지 클램프 + 중력 적분 — 수직 속도의 유일한 적분 지점이다.
    // 입력 이동(HandleMove)과 운반 추종(MoveWithGravity)이 같은 규칙을 써야 하므로 여기 하나만 둔다.
    // 점프 임펄스는 이 뒤에 덮어써야 한다 — 클램프에 잡아먹히지 않게. (#189, HandleMove 참고)
    private void IntegrateGravity()
    {
        // isGrounded가 아닌 이유 — 벽면에 얹힌 채로 클램프가 걸리면 하향 속도가 -2로 고정돼 영원히 매달린다.
        if (IsStablyGrounded && m_verticalVelocity < 0f)
        {
            m_verticalVelocity = k_groundedStickVelocity;
        }

        m_verticalVelocity += Config.Gravity * Time.deltaTime;
    }

    /// <summary>
    /// 쌓인 외력(넉백)과 수직 속도를 지운다 — <b>몸의 위치 권한이 넘어가는 순간</b> 부른다.
    /// 지금 부르는 곳은 둘이다: 래그돌 진입(#506)과 추종 진입(<see cref="PlayerTowedMotion"/>, #279).
    /// </summary>
    internal void ClearExternalVelocity()
    {
        m_knockbackVelocity = Vector3.zero;
        m_verticalVelocity = 0f;
        m_currentHorizontalVelocity = Vector3.zero; // 추종/래그돌 시 미끄러짐 관성(눈 효과 등) 완전히 제거
    }

    /// <summary>
    /// CharacterController를 껐다 켠다 — transform을 직접 옮기는 호송 추종(#279)이 쓴다.
    /// 켠 채로 transform을 옮기면 CC 내부 캐시가 위치를 되돌린다 (<see cref="SetPose"/>와 동일 사정).
    /// </summary>
    internal void SetControllerEnabled(bool value) => SetCapsuleEnabled(value);

    /// <summary>
    /// ⚠ <b>캡슐 enable을 만지는 유일한 통로다 — 다른 데서 <c>m_controller.enabled</c>에 직접 쓰지 말 것.</b>
    ///
    /// 콜라이더를 껐다 켜면 <c>Physics.IgnoreCollision</c> 상태가 <b>초기화된다</b>(Unity 사양).
    /// 사망 중에 그 일이 일어나면 시체 뼈가 자기 캡슐 <b>안에서</b> 출발하게 되고, 그걸 밀어내는
    /// 힘에 몸이 발작처럼 튄다. 그래서 켜는 쪽에서 래그돌에게 무시를 다시 걸게 한다
    /// (<see cref="PlayerRagdoll.ReapplyCapsuleIgnore"/>에 사정이 적혀 있다).
    ///
    /// ⚠ <b>같은 프레임 안에서 껐다 켜는 <see cref="SetPose"/>도 여기를 지난다</b> — 그쪽은 폴링으로는
    /// 전이를 볼 수 없어서, 예전에 재적용이 통째로 빠져 있던 자리다 (#759 §5).
    /// </summary>
    private void SetCapsuleEnabled(bool value)
    {
        m_controller.enabled = value;
        if (value)
            m_ragdoll?.ReapplyCapsuleIgnore();
    }

    // CharacterController가 켜진 상태에서 transform을 직접 옮기면 내부 캐시가 위치를 되돌릴 수 있어 잠시 끄고 옮긴다.
    private void SetPose(Vector3 pos, Quaternion rot)
    {
        // 뼈에 실어 줄 델타는 <b>루트를 옮기기 전에</b> 재야 한다 (#614 — 아래 PlaceBodyBy).
        Vector3 bodyDelta = pos - transform.position;

        SetCapsuleEnabled(false);
        transform.SetPositionAndRotation(pos, rot);

        // ⚠ <b>캡슐을 되켜기 전에 뼈를 옮긴다.</b> 죽은 몸은 위 루트 대입만으로는 안 따라온다 — 뼈가
        // 동적 리지드바디라 계층을 따르지 않고, 그대로 두면 TickCapsuleFollow가 다음 물리 스텝에
        // 루트를 도로 시체 자리로 끌어간다. 살아 있으면 무동작이다.
        //
        // 순서가 사양이다: CharacterController를 켜는 것은 트랜스폼 동기화를 부를 수 있고, 그러면
        // 위 루트 대입이 물리 포즈로 flush돼 뼈 델타가 <b>두 번</b> 실린다(NPC 쪽이
        // Physics.autoSyncTransforms = 0에 기대는 것과 같은 사정 — NpcRagdoll.ServerPlaceCorpse).
        m_ragdoll?.PlaceBodyBy(bodyDelta);

        SetCapsuleEnabled(true);

        // 원격에 "이건 순간이동이다"를 알린다 — 안 보내면 각 피어가 이 거리를 보간해 걸어·달려온다
        // (프리팹 설정 Interpolate=1 · PositionMaxInterpolationTime=0.1). 시체 쪽과 같은 수단이고
        // (NpcRagdoll.ServerTeleportNetTransforms) 권위만 오너로 갈린다 — 이 함수가 도는 곳이 곧 오너다.
        if (IsSpawned && IsOwner)
            m_netTransform?.Teleport(transform.position, transform.rotation, transform.localScale);

        // 낙하·점프 도중 텔레포트되면 쌓인 수직 속도가 그대로 남아 도착지에서 바닥을 파고들거나
        // 튀어오른다 — 도착 즉시 접지 판정으로 이어지도록 초기화한다. (#189)
        m_verticalVelocity = 0f;
        m_currentHorizontalVelocity = Vector3.zero; // 텔레포트 직후 관성에 의해 밀리는 현상 방지

        // 진행 중인 시점 보간도 같은 이유로 끊는다 (#576)
        m_look?.SnapViewBlend();
    }

    // 오너의 매 프레임 갱신 — 시점(PlayerLook)·추종(PlayerTowedMotion)도 여기서 순서를 잡아 돌린다.
    private void Update()
    {
        TickSpeedBuff(); // 만료는 아래 분기와 무관하게 흐른다 (#227)

        // 속도 비네트 — 내 화면에만 (#665). 여기 두면 아래 분기 전부를 덮는다:
        // 끌려가거나 래그돌인 동안은 이 속도가 0이라 저쪽에서 알아서 걷힌다.
        if (!IsSpawned || IsOwner)
            App.UI.SpeedVignette?.UpdateSpeed(m_currentHorizontalVelocity.magnitude, SprintSpeed);

        // 래그돌인 동안(#506) — <b>위치의 주인은 시체다.</b> 이동 입력을 받지 않는다.
        //
        // ⚠ <b>캡슐 추종은 여기서 부르지 않는다</b> — 사망 중 소유권이 서버로 넘어가면(#763 A-1)
        // 이 컴포넌트가 꺼져 있는 피어가 권위가 되므로, 추종은 PlayerRagdoll이 스스로 돈다.
        // 여기 남는 것은 <b>내 화면의 시점</b>뿐이다.
        if (m_ragdoll != null && m_ragdoll.IsCapsuleFollowingBody)
        {
            m_look?.HandleLook();
            m_look?.UpdateCameraPose();
            return;
        }

        // 남이 내 몸을 옮기는 중(#279 호송 / #365 운반) — 입력 이동 대신 추종한다.
        if (m_towed != null && m_towed.IsActive)
        {
            m_look?.HandleLook();
            m_towed.Tick();
            m_look?.UpdateCameraPose();
            return;
        }

        m_look?.HandleLook();
        m_look?.UpdateCameraPose(); // 카메라 높이/피치를 매 프레임 적용 (다운 시 바닥 시점) (#105)
        HandleMove();
    }

    /// <summary>
    /// 외력으로 밀어낸다 — 폭발 넉백 등(<see cref="BombExplosionView"/>). 세기는 m/s 단위 속도로 준다.
    /// </summary>
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
        // 다운 중·라운드 종료 시 이동 입력 차단 — 단 중력·접지는 유지해 바닥에 서 있게 한다 (#105, 라운드 종료 freeze)
        Vector2 input = IsMovementLocked ? Vector2.zero : m_inputHandler.MoveInput;
        Vector3 moveDirection = (
            transform.right * input.x + transform.forward * input.y
        ).normalized;

        // 점프 자격 판정에는 Move() 앞의 값이 맞다 — 그 시점의 마지막 확정 접지다.
        bool grounded = IsStablyGrounded;

        IntegrateGravity();

        // 점프 (#189)
        if (m_jump != null)
        {
            if (m_jump.ConsumeJumpRequest() && grounded && !IsMovementLocked)
            {
                m_verticalVelocity = Mathf.Sqrt(
                    2f * m_jump.JumpHeight * Mathf.Max(-Config.Gravity, 0.01f)
                );
            }
        }

        // 앉기가 달리기보다 우선
        float speed = IsCrouching ? CrouchSpeed
            : m_inputHandler.IsSprinting ? SprintSpeed
            : MoveSpeed;

        // 목표 수평 속도 계산 (미끄러짐 보간을 위해 inputVelocity를 targetVelocity로 취급)
        Vector3 targetVelocity = moveDirection * speed;
        
        // 팽팽해진 밧줄이 허용하는 만큼으로 목표 속도를 깎는다.
        if (m_dragLoad != null)
            targetVelocity = m_dragLoad.ConstrainByTautRopes(targetVelocity);

        // 벽면에 얹혔으면 밀어내 흘러내리게 한다.
        if (m_controller.isGrounded && IsOnSteepSurface)
        {
            Vector3 awayFromSurface = new Vector3(m_groundNormal.x, 0f, m_groundNormal.z).normalized;
            targetVelocity =
                Vector3.ProjectOnPlane(targetVelocity, awayFromSurface)
                + awayFromSurface * k_steepSlideSpeed;
        }

        // 마찰력은 <b>쌓인 빙판만큼</b> 낮아진다 (#227) — 켜짐/꺼짐이 아니라 비율이다.
        //
        // 예전에는 IsSnow 이진 스위치라 눈이 내리는 순간 바로 미끄럽고 그치는 순간 바로 정상이었다.
        // 그러면 "오래 내려서 길이 얼었다"가 아니라 "눈 파티클이 보이면 미끄럽다"가 되어, 누적이라는
        // 규칙이 몸으로 읽히지 않는다. 지금은 SnowEvent가 굴리는 누적 비율(IceRatio)로 보간한다:
        // 내리기 시작해도 한동안은 평소와 같고, 그친 뒤에도 녹을 때까지는 미끄럽다.
        //
        // 비율은 <b>내가 선 자리</b>로 묻는다 (#699) — 지붕 아래에는 빙판이 없다. 눈 표현은 이미 실내에서
        // 그치므로 여기서 전역 값을 읽으면 눈이 안 오는 실내에서 바닥만 어는다.
        SnowEvent snow = Snow;
        float iceRatio = snow != null
            ? snow.IceRatioAt(transform.position + Vector3.up * WeatherShelter.k_bodyProbeHeight)
            : 0f;
        float currentFriction = Mathf.Lerp(Config.DefaultFriction, Config.SnowFriction, iceRatio);
        
        // 방향 전환·정지가 즉각적이지 않도록 현재 속도를 목표 속도로 부드럽게 보간 (관성/미끄러짐 구현)
        m_currentHorizontalVelocity = Vector3.Lerp(m_currentHorizontalVelocity, targetVelocity, currentFriction * Time.deltaTime);

        // 넉백은 입력 이동(보간된 속도)과 별개로 합산된다.
        Vector3 velocity = m_currentHorizontalVelocity + m_knockbackVelocity + Vector3.up * m_verticalVelocity;
        MoveAndTrackGround(velocity * Time.deltaTime);

        // 천장에 머리를 박으면 상승 속도를 즉시 죽인다.
        if ((m_controller.collisionFlags & CollisionFlags.Above) != 0 && m_verticalVelocity > 0f)
        {
            m_verticalVelocity = 0f;
        }

        // 접지 보고는 반드시 Move() 뒤의 신선한 값으로 한다.
        if (m_jump != null)
        {
            m_jump.ReportGrounded(IsStablyGrounded);
        }

        // 넉백 지수 감쇠
        m_knockbackVelocity *= Mathf.Exp(-Config.KnockbackDamping * Time.deltaTime);
        if (m_knockbackVelocity.sqrMagnitude < 0.01f)
            m_knockbackVelocity = Vector3.zero;
    }
}