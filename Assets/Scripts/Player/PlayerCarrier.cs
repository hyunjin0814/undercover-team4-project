using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 기능 정지(Die)된 동료를 끌고 가는 운반 — 서버 권위 허브. (#365, GDD 7-5)
/// 한 컴포넌트가 <b>두 역할</b>을 든다: 남을 끄는 쪽(<see cref="CarriedTarget"/>)과 끌려가는 쪽
/// (<see cref="IsBeingCarried"/>). 양쪽 다 플레이어라 같은 프리팹에 붙고, 역할을 두 컴포넌트로
/// 쪼개면 "끌면서 동시에 끌려가는" 조합을 두 곳에서 막아야 한다.
///
/// 요청·검증·상태는 서버가 갖되(PlayerEscorter 관례), <b>이동은 끌려가는 쪽 오너가 한다</b> —
/// 플레이어 위치는 NetworkTransform 오너 권한이라 서버도 운반자도 남의 몸을 직접 못 옮긴다.
/// 그래서 서버가 대상 오너에게 RPC로 추종 지시를 내리고, 오너의 PlayerMovement가
/// <see cref="PlayerMovement.BeginDraggedFollow"/>로 따라간다. (오검거 호송 #279와 같은 구조)
///
/// 복구는 운반이 아니라 본부 부활 존(<see cref="HqRevivalZone"/>)이 한다 — 여기는 옮기기만 한다.
/// </summary>
public class PlayerCarrier : NetworkBehaviour
{
    [Header("운반 (서버 권위)")]
    [Tooltip(
        "이 거리(m)를 넘게 벌어지면 놓친다 — 추종 실패의 안전장치다. 정상 이동으로는 닿지 않는 값으로 "
        + "둘 것(전력 질주 추종 지연은 3.5m 안쪽). 몸이 문틀·기둥에 끼거나 끌려가는 쪽이 추종을 "
        + "못 할 때, 끊어주지 않으면 운반자는 끌고 있다고 믿는데 몸만 뒤에 남는다"
    )]
    [SerializeField]
    private float m_breakDistance = 8f;

    // 사거리는 조준·윤곽선과 같은 기준 — PlayerInteractor.Range 재사용 (#147 패턴, #184)
    private const float k_fallbackRange = 3f; // 테스트 구성 등 PlayerInteractor가 없을 때

    private PlayerInteractor m_interactor;
    private PlayerIncapacitation m_incapacitation;
    private PlayerMovement m_movement;
    private PlayerEscorter m_escorter; // 밧줄 끌기와 동시에 못 하게 막는 게이트. 없을 수 있다(테스트 구성)
    private PlayerLoadout m_loadout; // 밧줄 소지 검증 — 위조 RPC 방어 (#369 관례)

    /// <summary>지금 내가 끌고 가는 동료. 없으면 null. 서버(또는 오프라인)에서만 유효.</summary>
    public PlayerCarrier CarriedTarget { get; private set; }

    // 끌고 있는 대상 — 단순 bool이 아니라 참조인 이유는 밧줄 표시(RopeDragView)가 선의 양 끝점을
    // 알아야 하기 때문이다. CarriedTarget은 서버에만 있어 원격 피어는 누구와 이어졌는지 알 수 없다.
    // (PlayerEscorter.m_tetheredNpcSynced와 같은 사정)
    private readonly NetworkVariable<NetworkObjectReference> m_carriedSynced = new();

    // 끌려가는 중인지 — 다른 플레이어가 가로채지 못하게 막는 게이트. 서버가 쓰고 모두가 읽는다.
    private readonly NetworkVariable<bool> m_isBeingCarriedSynced = new NetworkVariable<bool>();

    /// <summary>운반 중 여부(끄는 쪽). 서버·오프라인은 실참조, 원격 피어는 동기화값. (PlayerEscorter.IsDragging 관례)</summary>
    public bool IsCarrying =>
        IsSpawned && !IsServer
            ? m_carriedSynced.Value.NetworkObjectId != 0
            : CarriedTarget != null;

    /// <summary>
    /// 끌고 가는 동료의 트랜스폼 — 전 피어에서 유효한 표현 계층용 접근자(밧줄 선). 없으면 null.
    /// (PlayerEscorter.TetheredNpcTransform과 동일 구조 — 세션 종료 중 NetworkManager 소멸 가드 포함)
    /// </summary>
    public Transform CarriedTransform
    {
        get
        {
            if (CarriedTarget != null)
                return CarriedTarget.transform;
            if (!IsSpawned)
                return null;

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsListening)
                return null;

            return m_carriedSynced.Value.TryGet(out NetworkObject targetObject, manager)
                ? targetObject.transform
                : null;
        }
    }

    /// <summary>누군가에게 끌려가는 중인지(끌려가는 쪽) — 이미 임자가 있는 대상을 가로채지 못하게 한다.</summary>
    public bool IsBeingCarried =>
        IsSpawned && !IsServer ? m_isBeingCarriedSynced.Value : m_carriedBy != null;

    private PlayerCarrier m_carriedBy; // 나를 끌고 있는 플레이어. 서버(또는 오프라인)에서만 유효

    /// <summary>이 플레이어가 지금 운반 대상이 될 수 있는가 — 기능 정지(Die) + 임자 없음. (#364/#365)</summary>
    public bool CanBeCarried =>
        m_incapacitation != null && m_incapacitation.IsDead && !IsBeingCarried;

    private void Awake()
    {
        m_interactor = GetComponent<PlayerInteractor>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_movement = GetComponent<PlayerMovement>();
        m_escorter = GetComponent<PlayerEscorter>();
        m_loadout = GetComponent<PlayerLoadout>();
    }

    // 밧줄을 들고 있는가 — 운반의 자원 게이트. 로드아웃이 없으면(테스트 구성) 통과. (PlayerEscorter.HasRope 관례)
    private bool HasRope => m_loadout == null || m_loadout.HasRope;

    // ---- 오너 클라 진입점 (상호작용이 호출) ----

    /// <summary>운반 시작 요청 — 오너가 호출(E, IncapacitatedPlayerInteractable). (#365)</summary>
    public void RequestCarry(PlayerCarrier target)
    {
        if (target == null)
            return;

        // 서버(호스트 포함)·오프라인은 로컬 참조로 바로 실행 (PlayerEscorter.RequestRopeDrag 관례)
        if (!IsSpawned || IsServer)
        {
            ServerBeginCarry(target);
            return;
        }
        if (!IsOwner)
            return; // 남의 플레이어 오브젝트에서 온 호출 방지
        if (target.NetworkObject == null || !target.NetworkObject.IsSpawned)
        {
            Debug.LogWarning($"운반 요청 무시 — 대상이 네트워크 스폰되지 않음: {target.name}", this);
            return;
        }

        CarryRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>부활 장치에 안치 요청 — 오너가 호출(운반 중 장치를 겨냥한 E). (#365)</summary>
    public void RequestPlaceInDevice(HqRevivalDevice device)
    {
        if (device == null)
            return;

        if (!IsSpawned || IsServer)
        {
            ServerPlaceInDevice(device);
            return;
        }
        if (!IsOwner)
            return;
        if (device.NetworkObject == null || !device.NetworkObject.IsSpawned)
        {
            Debug.LogWarning($"안치 요청 무시 — 장치가 네트워크 스폰되지 않음: {device.name}", this);
            return;
        }

        PlaceRequestRpc(new NetworkObjectReference(device.NetworkObject));
    }

    /// <summary>내려놓기 요청 — 오너가 호출(운반 중 E). (#365)</summary>
    public void RequestDrop()
    {
        if (!IsSpawned || IsServer)
        {
            ServerDrop("내려놓음");
            return;
        }
        if (!IsOwner)
            return;

        DropRequestRpc();
    }

    // ---- 서버 RPC (오너 → 서버) ----

    [Rpc(SendTo.Server)]
    private void CarryRequestRpc(NetworkObjectReference targetRef)
    {
        if (
            targetRef.TryGet(out NetworkObject targetObj)
            && targetObj.TryGetComponent(out PlayerCarrier target)
        )
        {
            ServerBeginCarry(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void DropRequestRpc() => ServerDrop("내려놓음");

    [Rpc(SendTo.Server)]
    private void PlaceRequestRpc(NetworkObjectReference deviceRef)
    {
        if (
            deviceRef.TryGet(out NetworkObject deviceObj)
            && deviceObj.TryGetComponent(out HqRevivalDevice device)
        )
        {
            ServerPlaceInDevice(device);
        }
    }

    // 안치 실행 — 판정은 장치가 갖는다(자리 하나·상태·거리). 실패하면 계속 끌고 있는 상태로 남는다.
    private void ServerPlaceInDevice(HqRevivalDevice device)
    {
        if (IsSpawned && !IsServer)
            return;
        if (CarriedTarget == null)
            return;

        if (!device.ServerPlace(this))
            NotifyOwner("안치 실패 — 장치가 사용 중이거나 너무 멀다");
    }

    // ---- 서버 실행 (권위) ----

    // 위조 RPC 방어를 겸한 진입 검증 — 클라 조기검증(IncapacitatedPlayerInteractable.CanInteract)과 같은 기준.
    private void ServerBeginCarry(PlayerCarrier target)
    {
        if (target == null || target == this)
            return; // 자기 자신은 못 든다
        if (CarriedTarget != null)
            return; // 한 번에 1명
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return; // 쓰러진 사람이 남을 옮길 수는 없다
        if (!HasRope)
            return; // 끌기 수단은 밧줄이다 — 들고 있어야 한다 (#369와 같은 자원 게이트)
        if (m_escorter != null && (m_escorter.IsBusy || m_escorter.IsTethered))
            return; // 밧줄은 하나뿐 — NPC를 끌거나 묶어 둔 상태면 그쪽을 먼저 풀어야 한다
        if (!target.CanBeCarried)
            return; // Die 상태 + 임자 없음일 때만. 다운(구조 가능)은 운반이 아니라 구조 대상이다
        if (!IsInRange(target))
            return;

        CarriedTarget = target;
        target.ServerSetCarriedBy(this);
        SetCarriedRef(target);

        Debug.Log($"[운반] 시작 — {name} → {target.name}");
        NotifyOwner($"밧줄로 묶어 끌기 시작: {target.name} (E로 내려놓기)");
    }

    /// <summary>
    /// 운반 해제 — 서버(또는 오프라인) 전용. 내려놓기·거리 이탈·부활·운반자 무력화가 모두 여기로 모인다.
    /// 부활(HqRevivalZone)처럼 끌려가는 쪽에서 끝내야 하는 경우를 위해 공개한다.
    /// </summary>
    public void ServerDrop(string reason)
    {
        if (IsSpawned && !IsServer)
            return;
        if (CarriedTarget == null)
            return;

        PlayerCarrier target = CarriedTarget;
        CarriedTarget = null;
        SetCarriedRef(null);

        // 대상이 이미 파괴됐으면(퇴장·라운드 종료) 정리할 상대가 없다 — Unity 가짜 null 가드 (#356 계열)
        if (target != null)
            target.ServerSetCarriedBy(null);

        Debug.Log($"[운반] 종료({reason}) — {name}");
        NotifyOwner($"운반 종료: {reason}");
    }

    /// <summary>
    /// 내가 끌려가는 중이라면 그 운반을 끊는다 — <b>끌려가는 쪽 사정</b>으로 끝낼 때 쓴다(본부 부활 등).
    /// 서버(또는 오프라인) 전용. 끌고 있는 쪽에서 끝내는 것은 <see cref="ServerDrop"/>다.
    /// </summary>
    public void ServerDropSelf(string reason)
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_carriedBy != null)
            m_carriedBy.ServerDrop(reason);
    }

    /// <summary>끌려가는 쪽 상태 갱신 + 오너에게 추종 지시 — 운반자(서버)가 호출한다.</summary>
    private void ServerSetCarriedBy(PlayerCarrier carrier)
    {
        m_carriedBy = carrier;
        if (IsSpawned && IsServer)
            m_isBeingCarriedSynced.Value = carrier != null;

        if (carrier == null)
        {
            if (IsSpawned)
                EndDraggedRpc();
            else
                m_movement?.EndDraggedFollow(); // 오프라인 Play 테스트 폴백
            return;
        }

        if (!IsSpawned)
        {
            m_movement?.BeginDraggedFollow(carrier.transform); // 오프라인 폴백
            return;
        }

        // 스폰된 운반자만 참조로 넘길 수 있다(NetworkObjectReference 제약)
        if (carrier.NetworkObject != null && carrier.NetworkObject.IsSpawned)
            BeginDraggedRpc(new NetworkObjectReference(carrier.NetworkObject));
    }

    // 위치 변경은 오너만 할 수 있다(NetworkTransform 오너 권한) — 서버가 시키고 오너가 움직인다 (#279 관례)
    [Rpc(SendTo.Owner)]
    private void BeginDraggedRpc(NetworkObjectReference carrierRef)
    {
        if (m_movement == null)
            return;
        if (!carrierRef.TryGet(out NetworkObject carrierObj))
            return; // 운반자가 이미 디스폰 — 서버의 거리 검사가 곧 운반을 끝낸다

        m_movement.BeginDraggedFollow(carrierObj.transform);
    }

    [Rpc(SendTo.Owner)]
    private void EndDraggedRpc() => m_movement?.EndDraggedFollow();

    // 끌고 있는 대상 참조 동기화 — 서버(또는 오프라인)에서만 호출된다. (PlayerEscorter.SetTethered 관례)
    private void SetCarriedRef(PlayerCarrier target)
    {
        if (!IsSpawned || !IsServer)
            return;

        // 스폰된 대상만 참조로 넘길 수 있다(NetworkObjectReference 제약) — 아니면 표시 없이 운반만 진행된다
        bool syncable = target != null && target.NetworkObject != null && target.NetworkObject.IsSpawned;
        ulong desired = syncable ? target.NetworkObject.NetworkObjectId : 0;
        if (m_carriedSynced.Value.NetworkObjectId == desired)
            return; // 값이 그대로면 쓰지 않는다 — 매 프레임 정리가 호출해도 대역폭을 먹지 않게

        m_carriedSynced.Value = syncable ? new NetworkObjectReference(target.NetworkObject) : default;
    }

    private void Update()
    {
        // 운반 상태는 서버 권위 — 클라에서는 아무것도 정리하지 않는다 (PlayerEscorter.Update 관례)
        if (IsSpawned && !IsServer)
            return;
        if (CarriedTarget == null)
        {
            // 대상이 파괴되면 위 가드가 Unity 가짜 null에 걸려 동기화 참조만 남는다 — 원격 오너의 E가
            // 영구히 '내려놓기'로 소비되는 사고를 막는다 (#356과 같은 사정)
            if (IsSpawned && IsServer && m_carriedSynced.Value.NetworkObjectId != 0)
                SetCarriedRef(null);
            return;
        }

        // 부활했으면(본부 존에서 30초 경과) 끌 대상이 아니다 — 일어난 사람을 계속 끌 수는 없다
        if (!CarriedTarget.IsDeadTarget)
        {
            ServerDrop("대상 복구됨");
            return;
        }

        // 내가 쓰러지면 놓친다 — 다운·기절·매달기·기능 정지 어느 쪽이든 손을 놓는다
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
        {
            ServerDrop("운반자 행동불능");
            return;
        }

        // 너무 벌어지면 놓친다 — 추종이 실패한 경우다(몸이 지형에 끼거나 오너가 추종을 못 함).
        // 달리기로는 벌어지지 않는 거리라, 여기 걸렸다는 건 정상 추종이 아니라는 뜻이다. (밧줄 끊김과 같은 처리)
        Vector3 delta = CarriedTarget.transform.position - transform.position;
        delta.y = 0f; // 계단·경사에서 높이차로 오작동하지 않게 수평 거리만 본다
        if (delta.sqrMagnitude > m_breakDistance * m_breakDistance)
            ServerDrop("대상을 놓침 — 너무 멀어짐");
    }

    // 끌려가는 쪽이 여전히 기능 정지 상태인가 — Update의 부활 감지용(서버·오프라인 실참조).
    private bool IsDeadTarget => m_incapacitation != null && m_incapacitation.IsDead;

    private bool IsInRange(PlayerCarrier target)
    {
        float range = m_interactor != null ? m_interactor.Range : k_fallbackRange;
        Vector3 origin = m_interactor != null ? m_interactor.AimOrigin.position : transform.position;

        // 사거리 + 가시선 — 거리만 보면 위조 RPC로 벽 너머 운반이 된다 (#360, PlayerReviver.IsInRange와 동일)
        return (target.transform.position - origin).sqrMagnitude <= range * range
            && (m_interactor == null || m_interactor.HasLineOfSightTo(target.transform));
    }

    // 판정 로그는 서버에서 찍히므로 원격 클라 오너는 결과를 볼 수 없다 — 오너 콘솔에도 전달한다 (#109 관례)
    private void NotifyOwner(string message)
    {
        Debug.Log(message);
        if (IsSpawned && IsServer && !IsOwner)
            OwnerLogRpc(message);
    }

    [Rpc(SendTo.Owner)]
    private void OwnerLogRpc(string message) => Debug.Log($"[서버 판정] {message}");

    public override void OnNetworkDespawn()
    {
        // 운반 중 퇴장·라운드 종료 — 끌려가던 쪽 추종을 남기지 않는다
        ServerDrop("운반자 퇴장");

        // 내가 끌려가던 쪽이었다면 상대의 운반도 끊는다 (반대 방향 정리)
        if (m_carriedBy != null)
            m_carriedBy.ServerDrop("대상 퇴장");
    }
}
