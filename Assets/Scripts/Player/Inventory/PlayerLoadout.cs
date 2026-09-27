using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어의 아이템 보유·장착·줍기·버리기를 관리한다.
/// 서버가 스폰·소유권·부착을 처리하고, 오너는 받은 보유 목록으로 5칸 인벤토리를 재구성해 장착·표시한다.
/// </summary>
[RequireComponent(typeof(PlayerItemUser))]
[RequireComponent(typeof(PlayerInteractor))]
public class PlayerLoadout : NetworkBehaviour
{
    [Header("장착 위치 (비우면 플레이어 루트에 부착)")]
    [Tooltip("지급·주운 아이템 인스턴스를 붙일 부모. 비우면 이 GameObject 하위에 붙는다.")]
    [SerializeField]
    private Transform m_itemAnchor;

    [Header("버리기")]
    [Tooltip("버릴 때 플레이어 정면으로 내려놓는 거리(m)")]
    [SerializeField]
    private float m_dropDistance = 1.2f;

    [Tooltip("착지면으로 인정할 레이어 — 기본 Default")]
    [SerializeField]
    private LayerMask m_groundMask = 1;

    public const int k_maxHeldItems = 5;

    private const float k_dropProbeHeight = 0.5f;

    private const float k_dropWallMargin = 0.3f;

    private readonly LoadoutSlots<ItemBase> m_slotModel = new LoadoutSlots<ItemBase>(k_maxHeldItems);

    private HeldItems m_held;

    private HeldItemsWatcher m_heldWatcher;

    private PlayerItemUser m_itemUser;
    private PlayerInputHandler m_inputHandler;
    private PlayerIncapacitation m_incapacitation;
    private PlayerEscorter m_escorter;

    private PlayerInteractor m_interactor;
    private PlayerTerminalFocus m_terminalFocus;
    private float m_pickupRange;

    public bool IsIncapacitated => m_incapacitation != null && m_incapacitation.IsIncapacitated;

    public bool IsTerminalFocused => m_terminalFocus != null && m_terminalFocus.IsFocusing;

    public IReadOnlyList<ItemBase> Slots => m_slotModel.Slots;

    public event Action OnSlotsChanged;

    public int EquippedIndex => m_slotModel.EquippedIndex;

    public event Action OnEquippedSlotChanged;

    public event Action OnHeldItemsChangedAnyPeer
    {
        add
        {
            if (m_heldWatcher != null)
                m_heldWatcher.OnChanged += value;
        }
        remove
        {
            if (m_heldWatcher != null)
                m_heldWatcher.OnChanged -= value;
        }
    }

    private void Awake()
    {
        Transform anchor = m_itemAnchor != null ? m_itemAnchor : transform;
        m_held = new HeldItems(anchor);

        m_heldWatcher = anchor.gameObject.AddComponent<HeldItemsWatcher>();

        m_itemUser = GetComponent<PlayerItemUser>();
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_escorter = GetComponent<PlayerEscorter>();
        m_interactor = GetComponent<PlayerInteractor>();
        m_terminalFocus = GetComponent<PlayerTerminalFocus>();
        m_pickupRange = m_interactor.Range;
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            m_inputHandler.OnPreviousItem += EquipPrevious;
            m_inputHandler.OnNextItem += EquipNext;
            m_inputHandler.OnDropItem += RequestDropEquipped;
            m_inputHandler.OnSelectSlot += SelectSlot;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
        {
            int despawned = m_held.DespawnAll();
            if (despawned > 0)
            {
                Debug.Log($"[PlayerLoadout] 플레이어 정리와 함께 소지 아이템 {despawned}개 디스폰");
            }
        }

        if (IsOwner)
        {
            m_inputHandler.OnPreviousItem -= EquipPrevious;
            m_inputHandler.OnNextItem -= EquipNext;
            m_inputHandler.OnDropItem -= RequestDropEquipped;
            m_inputHandler.OnSelectSlot -= SelectSlot;
        }
    }

    internal HeldItems Held => m_held;

    /// <summary>서버가 소지품을 바꾼 뒤 오너 인벤토리 재구성을 알린다. 서버 전용.</summary>
    internal void ServerNotifyHeldItemsChanged() => SyncHeldItemsRpc(m_held.BuildRefs());

    /// <summary>월드 아이템 줍기 요청. WorldItemPickup이 상호작용한 플레이어에게 호출한다. (오너에서만 유효)</summary>
    public void RequestPickup(NetworkObject itemNetworkObject)
    {
        if (!IsOwner || itemNetworkObject == null)
        {
            return;
        }

        PickupRpc(new NetworkObjectReference(itemNetworkObject));
    }

    [Rpc(SendTo.Server)]
    private void PickupRpc(NetworkObjectReference itemRef, RpcParams rpcParams = default)
    {
        if (!itemRef.TryGet(out NetworkObject itemNetworkObject))
        {
            return;
        }

        if (itemNetworkObject.transform.parent != null)
        {
            return;
        }

        Vector3 toItem = itemNetworkObject.transform.position - m_interactor.AimOrigin.position;
        if (toItem.sqrMagnitude > m_pickupRange * m_pickupRange)
        {
            return;
        }

        if (!m_interactor.HasLineOfSightTo(itemNetworkObject.transform))
        {
            return;
        }

        if (m_held.Count >= k_maxHeldItems)
        {
            return;
        }

        ulong requester = rpcParams.Receive.SenderClientId;
        itemNetworkObject.ChangeOwnership(requester);
        m_held.Attach(itemNetworkObject);

        ServerNotifyHeldItemsChanged();
    }

    private bool IsTetheredRope(ItemBase item) =>
        item is Rope && m_escorter != null && m_escorter.TetheredCount >= RopeCount;

    private void RequestDropEquipped()
    {
        if (!IsOwner)
        {
            return;
        }

        if (IsIncapacitated)
        {
            return;
        }

        ItemBase equipped = m_itemUser.EquippedItem;
        if (equipped == null)
        {
            return;
        }

        if (IsTetheredRope(equipped))
        {
            Debug.Log("밧줄에 묶어 둔 대상이 있어 버릴 수 없음 — 먼저 풀어야 한다");
            return;
        }

        NetworkObject itemNetworkObject = equipped.GetComponent<NetworkObject>();
        if (itemNetworkObject == null)
        {
            return;
        }

        DropRpc(new NetworkObjectReference(itemNetworkObject));
    }

    [Rpc(SendTo.Server)]
    private void DropRpc(NetworkObjectReference itemRef)
    {
        if (!itemRef.TryGet(out NetworkObject itemNetworkObject))
        {
            return;
        }

        if (!m_held.Holds(itemNetworkObject))
        {
            return;
        }

        itemNetworkObject.TryGetComponent(out ItemBase droppedItem);

        if (IsTetheredRope(droppedItem))
        {
            return;
        }

        if (droppedItem != null)
        {
            droppedItem.ServerCancelActiveUse();
        }

        Vector3 dropPosition = ResolveDropPosition();
        itemNetworkObject.transform.SetPositionAndRotation(dropPosition, Quaternion.identity);
        WorldItemPickup.SettleOnGround(itemNetworkObject.gameObject, dropPosition.y);
        itemNetworkObject.TrySetParent((Transform)null, true);
        itemNetworkObject.RemoveOwnership();

        ServerNotifyHeldItemsChanged();
    }

    /// <summary>소지품 하나를 디스폰하고 오너 인벤토리를 다시 맞춘다. 서버(또는 오프라인) 전용.</summary>
    public void ServerConsumeHeldItem(ItemBase item)
    {
        if (IsSpawned && !IsServer)
        {
            return;
        }

        if (item == null)
        {
            return;
        }

        NetworkObject itemNetworkObject = item.NetworkObject;

        if (itemNetworkObject == null || !m_held.Holds(itemNetworkObject))
        {
            return;
        }

        if (itemNetworkObject.IsSpawned)
        {
            itemNetworkObject.Despawn(destroy: true);
        }
        else
        {
            Destroy(item.gameObject);
        }

        ServerNotifyHeldItemsChanged();
    }

    /// <summary>손에서 떼어 낼 수 있는 소지품(묶인 밧줄 제외)을 into에 담는다.</summary>
    public void CollectDetachableItems(List<ItemBase> into)
    {
        m_held.CollectInto(into);
        into.RemoveAll(IsTetheredRope);
    }

    private Vector3 ResolveDropPosition()
    {
        float distance = m_dropDistance;

        Vector3 probeOrigin = transform.position + Vector3.up * k_dropProbeHeight;
        if (
            Physics.Raycast(
                probeOrigin,
                transform.forward,
                out RaycastHit obstacle,
                m_dropDistance,
                m_interactor.LosBlockMask,
                QueryTriggerInteraction.Ignore
            )
        )
        {
            distance = Mathf.Max(0f, obstacle.distance - k_dropWallMargin);
        }

        Vector3 candidate = transform.position + transform.forward * distance;
        return DeliveryScatter.SnapToGround(candidate, m_groundMask);
    }

    public bool HasRope => RopeCount > 0;

    public int RopeCount => m_held.CountOf<Rope>();

    public ReviveKit HeldReviveKit => m_held.FirstOf<ReviveKit>();

    [Rpc(SendTo.Owner)]
    private void SyncHeldItemsRpc(NetworkObjectReference[] itemRefs)
    {
        ResolveAndRebuildAsync(itemRefs).Forget();
    }

    private async UniTaskVoid ResolveAndRebuildAsync(NetworkObjectReference[] itemRefs)
    {
        EResolveResult result = await NetworkRefResolver.WaitAsync(
            itemRefs,
            () => this != null && IsSpawned
        );

        if (result == EResolveResult.Aborted)
        {
            return;
        }

        RebuildHeldItems(itemRefs);
    }

    private void RebuildHeldItems(NetworkObjectReference[] itemRefs)
    {
        List<ItemBase> incoming = new List<ItemBase>(itemRefs.Length);
        foreach (NetworkObjectReference itemRef in itemRefs)
        {
            if (itemRef.TryGet(out NetworkObject itemNetworkObject)
                && itemNetworkObject.TryGetComponent(out ItemBase item))
            {
                incoming.Add(item);
            }
        }

        int keptIndex = m_slotModel.Reconcile(incoming);
        EquipSlot(keptIndex);

        OnSlotsChanged?.Invoke();
    }

    private void EquipPrevious() => Cycle(-1);

    private void EquipNext() => Cycle(1);

    private void Cycle(int direction)
    {
        if (IsIncapacitated)
        {
            return;
        }

        if (IsTerminalFocused)
        {
            return;
        }

        EquipSlot(m_slotModel.NextIndex(direction));
    }

    /// <summary>슬롯을 직접 선택해 장착한다 (숫자키 1~5, #144/#793). 빈 칸이면 빈손이 된다.</summary>
    public void SelectSlot(int index)
    {
        if (IsIncapacitated)
        {
            return;
        }

        if (IsTerminalFocused)
        {
            return;
        }

        if (!m_slotModel.IsValidIndex(index))
        {
            return;
        }

        EquipSlot(index);
    }

    private void EquipSlot(int index)
    {
        ItemBase previousEquipped = m_itemUser.EquippedItem;

        m_slotModel.SetEquippedIndex(index);
        m_itemUser.SetEquippedItem(m_slotModel.Equipped);

        if (IsOwner)
        {
            if (previousEquipped != null)
                previousEquipped.GetComponent<ChannelGauge>()?.HideLocal();

            ItemBase equipped = m_itemUser.EquippedItem;
            if (equipped != null)
                equipped.OnEquipped();
        }

        OnEquippedSlotChanged?.Invoke();
    }

    /// <summary>두 슬롯의 내용을 맞바꾼다 (편집 모드 드래그 정렬, #144). 순수 오너 로컬 — 서버 무관.</summary>
    public void SwapSlots(int a, int b)
    {
        if (IsIncapacitated)
        {
            return;
        }

        if (m_slotModel.TrySwap(a, b))
        {
            OnSlotsChanged?.Invoke();
        }
    }
}
