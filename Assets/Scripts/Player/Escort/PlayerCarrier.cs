using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 기능 정지된 동료를 밧줄로 끄는 운반의 서버 권위 허브 — 끄는 쪽과 끌리는 쪽 역할을 함께 든다.
/// 이동은 끌리는 쪽 오너가 PlayerTowedMotion으로 따라가며, 한 몸에 여럿이 덧걸 수 있다.
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

    private const float k_fallbackRange = 3f;

    private const float k_teleportGraceSeconds = 1f;
    private float m_teleportGraceRemaining;

    private const int k_leashCarrierCount = 2;

    private PlayerInteractor m_interactor;
    private PlayerIncapacitation m_incapacitation;
    private PlayerTowedMotion m_towed;
    private PlayerEscorter m_escorter;
    private PlayerLoadout m_loadout;

    public PlayerCarrier CarriedTarget { get; private set; }

    private readonly NetworkVariable<NetworkObjectReference> m_carriedSynced = new();

    private readonly NetworkVariable<byte> m_carrierCountSynced = new NetworkVariable<byte>();

    public bool IsCarrying =>
        IsSpawned && !IsServer
            ? m_carriedSynced.Value.NetworkObjectId != 0
            : CarriedTarget != null;

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

    public PlayerCarrier CarriedBody
    {
        get
        {
            if (CarriedTarget != null)
                return CarriedTarget;

            Transform carried = CarriedTransform;
            return carried != null && carried.TryGetComponent(out PlayerCarrier body) ? body : null;
        }
    }

    public bool IsBeingCarried => CarrierCount > 0;

    public int CarrierCount =>
        IsSpawned && !IsServer ? m_carrierCountSynced.Value : m_carriedBy.Count;

    private readonly List<PlayerCarrier> m_carriedBy = new List<PlayerCarrier>();

    public bool CanBeCarried =>
        m_incapacitation != null && m_incapacitation.IsDead && !m_incapacitation.IsBodyLost;

    private void Awake()
    {
        m_interactor = GetComponent<PlayerInteractor>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_towed = GetComponent<PlayerTowedMotion>();
        m_escorter = GetComponent<PlayerEscorter>();
        m_loadout = GetComponent<PlayerLoadout>();
    }

    private bool HasRope => m_loadout == null || m_loadout.HasRope;

    /// <summary>운반 시작 요청 — 오너가 호출(E, IncapacitatedPlayerInteractable).</summary>
    public void RequestCarry(PlayerCarrier target)
    {
        if (target == null)
            return;

        if (!IsSpawned || IsServer)
        {
            ServerBeginCarry(target);
            return;
        }
        if (!IsOwner)
            return;
        if (target.NetworkObject == null || !target.NetworkObject.IsSpawned)
        {
            Debug.LogWarning($"운반 요청 무시 — 대상이 네트워크 스폰되지 않음: {target.name}", this);
            return;
        }

        CarryRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>부활 장치에 안치 요청 — 오너가 호출(운반 중 장치를 겨냥한 E).</summary>
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

    /// <summary>내려놓기 요청 — 오너가 호출(운반 중 E).</summary>
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

    private void ServerPlaceInDevice(HqRevivalDevice device)
    {
        if (IsSpawned && !IsServer)
            return;
        if (CarriedTarget == null)
            return;

        if (!device.ServerPlace(this))
            NotifyOwner("안치 실패 — 장치가 사용 중이거나 너무 멀다");
    }

    private void ServerBeginCarry(PlayerCarrier target)
    {
        if (target == null || target == this)
            return;
        if (CarriedTarget != null)
            return;
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return;
        if (!HasRope)
            return;
        if (m_escorter != null && m_escorter.IsAtRopeCapacity)
            return;
        if (!target.CanBeCarried)
            return;
        if (!IsInRange(target))
            return;

        target.m_incapacitation?.ServerCompleteOwnershipHandover();

        CarriedTarget = target;
        target.ServerAddCarrier(this);
        SetCarriedRef(target);

        App.Game.Fx?.PlayEverywhere(EFx.RopeBind, target.transform.position);

        Debug.Log($"[운반] 시작 — {name} → {target.name}");
        NotifyOwner($"밧줄로 묶어 끌기 시작: {target.name} (E로 내려놓기)");
    }

    /// <summary>순간이동 직전 운반을 잠시 거리 검사에서 제외한다. 서버(또는 오프라인) 전용.</summary>
    internal void ServerBeginTeleportGrace()
    {
        if (IsSpawned && !IsServer)
            return;

        m_teleportGraceRemaining = k_teleportGraceSeconds;
    }

    /// <summary>운반을 해제한다. 서버(또는 오프라인) 전용.</summary>
    public void ServerDrop(string reason)
    {
        if (IsSpawned && !IsServer)
            return;
        if (CarriedTarget == null)
            return;

        PlayerCarrier target = CarriedTarget;
        CarriedTarget = null;
        SetCarriedRef(null);

        if (target != null)
            target.ServerRemoveCarrier(this);

        Debug.Log($"[운반] 종료({reason}) — {name}");
        NotifyOwner($"운반 종료: {reason}");
    }

    /// <summary>나를 끄는 참가자 중 keeper만 남기고 나머지를 끊는다(null이면 전부). 서버(또는 오프라인) 전용.</summary>
    public void ServerReleaseCarriersExcept(PlayerCarrier keeper, string reason)
    {
        if (IsSpawned && !IsServer)
            return;

        for (int i = m_carriedBy.Count - 1; i >= 0; i--)
        {
            PlayerCarrier holder = m_carriedBy[i];
            if (holder == null || holder == keeper)
                continue;

            holder.ServerDrop(reason);
        }
    }

    /// <summary>나를 끌고 있는 전원을 끊는다 — <see cref="ServerReleaseCarriersExcept"/>에 keeper=null.</summary>
    public void ServerDropAllCarriers(string reason) => ServerReleaseCarriersExcept(null, reason);

    /// <summary>합류 — 참가자 한 명이 추가로 나를 끌기 시작한다. 서버(또는 오프라인) 전용.</summary>
    private void ServerAddCarrier(PlayerCarrier carrier)
    {
        m_carriedBy.Add(carrier);
        SyncCarrierCount();

        if (!IsSpawned)
        {
            m_towed?.BeginDraggedFollow(carrier.transform);
            return;
        }

        if (carrier.NetworkObject != null && carrier.NetworkObject.IsSpawned)
            BeginDraggedRpc(new NetworkObjectReference(carrier.NetworkObject));
    }

    /// <summary>참가자 한 명이 나를 끄는 것을 멈춘다(그 가닥만) — 서버(또는 오프라인) 전용.</summary>
    private void ServerRemoveCarrier(PlayerCarrier carrier)
    {
        m_carriedBy.Remove(carrier);
        SyncCarrierCount();

        if (IsSpawned && carrier.NetworkObject != null && carrier.NetworkObject.IsSpawned)
            EndDraggedRpc(new NetworkObjectReference(carrier.NetworkObject));
        else
            m_towed?.EndDraggedFollow(carrier.transform);
    }

    private void SyncCarrierCount()
    {
        if (IsSpawned && IsServer)
            m_carrierCountSynced.Value = (byte)m_carriedBy.Count;
    }

    [Rpc(SendTo.Everyone)]
    private void BeginDraggedRpc(NetworkObjectReference carrierRef)
    {
        if (m_towed == null)
            return;
        if (!carrierRef.TryGet(out NetworkObject carrierObj))
            return;

        m_towed.BeginDraggedFollow(carrierObj.transform);
    }

    [Rpc(SendTo.Everyone)]
    private void EndDraggedRpc(NetworkObjectReference carrierRef)
    {
        if (carrierRef.TryGet(out NetworkObject carrierObj))
            m_towed?.EndDraggedFollow(carrierObj.transform);
        else
            m_towed?.EndDraggedFollow();
    }

    private void SetCarriedRef(PlayerCarrier target)
    {
        if (!IsSpawned || !IsServer)
            return;

        bool syncable = target != null && target.NetworkObject != null && target.NetworkObject.IsSpawned;
        ulong desired = syncable ? target.NetworkObject.NetworkObjectId : 0;
        if (m_carriedSynced.Value.NetworkObjectId == desired)
            return;

        m_carriedSynced.Value = syncable ? new NetworkObjectReference(target.NetworkObject) : default;
    }

    private void Update()
    {
        if (IsSpawned && !IsServer)
            return;
        if (CarriedTarget == null)
        {
            if (IsSpawned && IsServer && m_carriedSynced.Value.NetworkObjectId != 0)
                SetCarriedRef(null);
            return;
        }

        if (!CarriedTarget.IsDeadTarget)
        {
            ServerDrop("대상 복구됨");
            return;
        }

        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
        {
            ServerDrop("운반자 행동불능");
            return;
        }

        if (m_teleportGraceRemaining > 0f)
        {
            m_teleportGraceRemaining -= Time.deltaTime;
            return;
        }

        if (CarriedTarget.CarrierCount >= k_leashCarrierCount)
            return;

        Vector3 delta = CarriedTarget.transform.position - transform.position;
        delta.y = 0f;
        if (delta.sqrMagnitude > m_breakDistance * m_breakDistance)
            ServerDrop("대상을 놓침 — 너무 멀어짐");
    }

    internal float BreakDistance => m_breakDistance;

    private bool IsDeadTarget => m_incapacitation != null && m_incapacitation.IsDead;

    private bool IsInRange(PlayerCarrier target) =>
        PlayerInteractor.IsWithinReach(
            m_interactor,
            target.transform,
            PlayerInteractor.RangeOf(m_interactor, k_fallbackRange),
            transform.position);

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
        ServerDrop("운반자 퇴장");

        for (int i = m_carriedBy.Count - 1; i >= 0; i--)
            m_carriedBy[i]?.ServerDrop("대상 퇴장");
    }
}
