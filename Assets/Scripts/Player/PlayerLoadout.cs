using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어의 아이템 보유·장착·줍기·버리기를 관리한다. (#47, #46, #88)
/// 아이템은 독립 NetworkObject 프리팹이므로(#88) 서버가 스폰·소유권 부여·부착을 담당하고,
/// 오너 클라는 서버가 보낸 보유 목록으로 자기 인벤토리(Slots, 고정 3칸)를 재구성해 휠/숫자키 전환(#46, #144)·
/// 1인칭 손 표시(#45)·인벤토리 UI(#144)에 쓴다. 칸 배치(어느 칸에 뭐가 있는지)는 오너 로컬 관심사.
///
/// 권위 구분:
/// - 서버: 시작 지급 스폰, 줍기/버리기 요청 검증, 소유권 이전, 부모(부착) 변경.
/// - 오너: 줍기/버리기 입력 발신, 휠 순환 장착, 손 표시.
/// 3인칭(타 플레이어) 장착 표시는 이 이슈 범위 밖 — 후속.
/// </summary>
// TODO: #55 서버권위 전환 시 장착/사용 실행도 서버 기준으로 (지금은 장착 상태가 오너 로컬)
[RequireComponent(typeof(PlayerItemUser))]
[RequireComponent(typeof(PlayerInteractor))]
public class PlayerLoadout : NetworkBehaviour
{
    [Header("기본 지급 장비")]
    [Tooltip("게임 시작 시 순서대로 지급할 아이템 프리팹(NetworkObject). 첫 항목이 기본 장착된다.")]
    [SerializeField]
    private List<ItemBase> m_startingGear = new List<ItemBase>();

    [Header("장착 위치 (비우면 플레이어 루트에 부착)")]
    [Tooltip("지급·주운 아이템 인스턴스를 붙일 부모. 비우면 이 GameObject 하위에 붙는다.")]
    [SerializeField]
    private Transform m_itemAnchor;

    [Header("버리기")]
    [Tooltip("버릴 때 플레이어 정면으로 내려놓는 거리(m)")]
    [SerializeField]
    private float m_dropDistance = 1.2f;

    /// <summary>플레이어 소지 슬롯 수 — 고정 3칸 (#144, GDD 용량 3칸).</summary>
    public const int k_maxHeldItems = 3;

    // 드롭 장애물 탐침 높이(m). 발밑에서 쏘면 바닥·문턱에 걸리므로 허리 높이에서 앞을 훑는다.
    private const float k_dropProbeHeight = 0.5f;

    // 벽에서 띄울 여유(m). 줍기 박스 최소 크기(0.5m)의 절반 — 박스가 벽을 파고들지 않을 만큼.
    private const float k_dropWallMargin = 0.3f;

    // 오너 로컬 슬롯 배치 모델 — 어느 칸에 뭐가 있는지·선택 인덱스와 그 위의 대조/순환/선택/스왑은
    // 순수 인덱스 연산이라 Netcode와 무관한 LoadoutSlots<T>로 분리했다(단위 테스트 가능). 부착·소유권·
    // 동기화는 이 컴포넌트가, 칸 배치는 모델이 담당한다. 서버 동기화는 flat list(BuildHeldItemRefs) 그대로. (#144)
    private readonly LoadoutSlots<ItemBase> m_slotModel = new LoadoutSlots<ItemBase>(k_maxHeldItems);
    private PlayerItemUser m_itemUser;
    private PlayerInputHandler m_inputHandler;
    private PlayerIncapacitation m_incapacitation; // 다운(무력화) 중 아이템 전환·버리기 차단용 (#105)
    private PlayerEscorter m_escorter; // 밧줄을 묶어 둔 동안 그 밧줄 버리기 차단용 (#369)

    // 서버 줍기 거리 검증용 — PlayerInteractor의 조준 사거리·기준점을 그대로 재사용한다 (#147).
    // 값을 따로 두지 않고 여기서 읽어야 조준-줍기 사거리가 항상 정합된다.
    private PlayerInteractor m_interactor;
    private float m_pickupRange;

    // 다운(무력화) 중 여부 — 무력화 컴포넌트가 없으면(테스트 구성 등) 항상 false. (PlayerMovement 관례)
    // 인벤토리 UI(#144)가 편집 모드 진입 게이트에 쓰므로 public.
    public bool IsIncapacitated => m_incapacitation != null && m_incapacitation.IsIncapacitated;

    /// <summary>고정 3칸 슬롯 (빈 칸 = null). 인벤토리 UI(#144)·휠 전환(#46)이 사용한다. (오너 로컬)</summary>
    public IReadOnlyList<ItemBase> Slots => m_slotModel.Slots;

    /// <summary>슬롯 구성 변경 이벤트 — 줍기/버리기/초기 지급/드래그 스왑 시 발행. 인벤토리 UI(#144)가 구독.</summary>
    public event Action OnSlotsChanged;

    /// <summary>현재 선택(장착)된 슬롯 인덱스. 빈손이면 -1. 빈 칸을 선택하면 그 칸 인덱스가 된다.</summary>
    public int EquippedIndex => m_slotModel.EquippedIndex;

    /// <summary>선택 슬롯 이동 이벤트 — 빈 칸↔빈 칸 전환처럼 장착 아이템이 안 바뀌어도 발행. UI 하이라이트(#144)가 구독.</summary>
    public event Action OnEquippedSlotChanged;

    private Transform ItemParent => m_itemAnchor != null ? m_itemAnchor : transform;

    private void Awake()
    {
        m_itemUser = GetComponent<PlayerItemUser>();
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_escorter = GetComponent<PlayerEscorter>();
        m_interactor = GetComponent<PlayerInteractor>();
        m_pickupRange = m_interactor.Range;
    }

    public override void OnNetworkSpawn()
    {
        // 게임 씬에서 플레이어가 처음 만들어지는 경우만 여기서 지급한다 — 직접 Play(DevAutoHost)처럼
        // NGO 연결 승인이 플레이어를 만드는 흐름. 정식 루프(상점→게임)의 매 라운드 지급은
        // PlayerSpawnManager가 호출한다(둘이 겹쳐도 GrantStartingGear의 보유 검사가 막는다). (#370)
        if (App.CurrentScene == EScene.Game)
        {
            ServerGrantStartingGear();
        }

        // 오너만 입력을 받는다 (휠 순환·버리기). 입력 핸들러는 오너 외엔 비활성.
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
        // 서버: 들고 있던 아이템도 함께 내린다. 아이템은 플레이어에 부모로 붙은 독립 NetworkObject라
        // 플레이어만 디스폰하면 부모만 떨어져 나가고 아이템은 살아남는다 — 플레이어를 정리하는
        // 씬(로비·타이틀)에서 원점에 뜬 채로 그대로 보인다. (#395)
        if (IsServer)
        {
            int despawned = DespawnHeldItems();
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

    /// <summary>
    /// 손에 든 아이템을 전부 디스폰한다 — 아이템의 수명을 플레이어와 묶는다. 서버 전용. (#395)
    /// 월드에 버린 아이템은 대상이 아니다 — 이미 부모가 해제돼 이 밑에 없다.
    /// 상점 복귀 회수(<see cref="ServerClearHeldItems"/>)도 이 경로를 쓴다. (#370)
    /// </summary>
    /// <returns>디스폰한 아이템 수.</returns>
    private int DespawnHeldItems()
    {
        // 세션이 통째로 내려가는 중이면 NGO가 알아서 정리한다 — 그 와중에 Despawn을 부르면 경고만 남는다
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening)
            return 0;

        Transform parent = ItemParent;
        if (parent == null)
            return 0;

        // 디스폰하면 자식 목록이 바뀌므로 먼저 모아 둔다 (BuildHeldItemRefs와 같은 열거 방식)
        List<NetworkObject> held = new List<NetworkObject>();
        for (int i = 0; i < parent.childCount; i++)
        {
            ItemBase item = parent.GetChild(i).GetComponent<ItemBase>();
            if (item != null && item.NetworkObject != null)
            {
                // 채널링 중이면 먼저 끊는다 — 드롭과 같은 이유(배터리 낭비·오완료 방지). (#370)
                item.ServerCancelActiveUse();
                held.Add(item.NetworkObject);
            }
        }

        foreach (NetworkObject item in held)
        {
            if (item != null && item.IsSpawned)
            {
                item.Despawn(destroy: true);
            }
        }

        return held.Count;
    }

    // ---- 서버: 시작 지급 (게임 씬 진입, #370) ----

    /// <summary>
    /// 기본 장비를 지급한다 — 게임 씬 진입 시 서버(PlayerSpawnManager)가 클라별로 호출한다. (#370)
    /// 상점 복귀 때 <see cref="ServerClearHeldItems"/>로 전량 회수되므로 매 라운드 같은 구성으로 시작한다.
    /// 서버 판정은 한 프레임 뒤 GrantStartingGearAsync가 한다 — 클라 호출은 거기서 걸러진다.
    /// </summary>
    public void ServerGrantStartingGear() => GrantStartingGearAsync().Forget();

    // 호출 지점(스폰 처리·씬 로드 완료 콜백) 밖으로 한 프레임 미뤄 지급한다 — NGO 메시지 처리 중
    // 스폰하면 후속 접속 클라의 씬 동기화가 중복 스폰(같은 NetworkObjectId 재생성)으로 깨진다.
    private async UniTaskVoid GrantStartingGearAsync()
    {
        await UniTask.NextFrame();

        // 대기 중 디스폰됐거나 더 이상 서버가 아니면 중단.
        if (this == null || !IsSpawned || !IsServer)
        {
            return;
        }

        GrantStartingGear();
    }

    // 기본 장비 프리팹을 NetworkObject로 스폰해 오너 소유로 만들고 플레이어에 부착한 뒤,
    // 오너에게 보유 목록을 동기화한다.
    private void GrantStartingGear()
    {
        Transform parent = ItemParent;

        // 이미 뭔가 들고 있으면 지급하지 않는다 — 게임 씬 재진입·중복 호출로 같은 장비가 겹쳐 스폰되면
        // 슬롯(3칸)이 헛되이 차 이후 줍기가 전부 거부된다. 정상 흐름에서는 상점 복귀 때 전량 회수돼 빈손이다. (#370)
        if (CountHeldItems() > 0)
        {
            Debug.LogWarning("[PlayerLoadout] 이미 아이템을 보유 중이라 기본 장비 지급을 건너뛴다.", this);
            return;
        }

        int granted = 0;
        foreach (ItemBase gearPrefab in m_startingGear)
        {
            if (gearPrefab == null)
            {
                continue;
            }

            // 소지 3칸 초과분은 스폰하지 않는다 (#144) — 캡을 안 두면 초과 아이템이 부착되지만
            // 슬롯에 안 들어가 장착·드롭 불가 상태로 남고, BuildHeldItemRefs 카운트가 영구히 꽉 차
            // 이후 모든 줍기가 거부된다.
            if (granted >= k_maxHeldItems)
            {
                Debug.LogWarning(
                    $"[PlayerLoadout] 시작 지급이 소지 한도({k_maxHeldItems})를 초과 — 초과분 무시. m_startingGear 설정 확인."
                );
                break;
            }

            ItemBase item = Instantiate(gearPrefab);
            NetworkObject itemNetworkObject = item.GetComponent<NetworkObject>();
            itemNetworkObject.SpawnWithOwnership(OwnerClientId);
            AttachToParent(itemNetworkObject, parent);
            granted++;
        }

        Debug.Log($"[PlayerLoadout] 기본 장비 지급 — client {OwnerClientId}, {granted}개 ({App.CurrentScene})");
        SyncHeldItemsRpc(BuildHeldItemRefs());
    }

    // ---- 서버: 회수 (상점 복귀, #370) ----

    /// <summary>
    /// 보유 아이템을 전량 회수(디스폰)한다 — 상점 복귀 시 서버(ShopManager)가 클라별로 호출한다. (#370)
    /// 아이템은 destroyWithScene:false로 스폰돼 씬을 넘어도 살아남으므로, 회수하지 않으면 다음 라운드
    /// 지급분과 겹쳐 슬롯이 찬다. 라운드 사이 이월은 오브젝트 생존이 아니라 상점 구매 목록(#182)이 맡는다.
    /// </summary>
    public void ServerClearHeldItems()
    {
        if (!IsServer)
        {
            return;
        }

        // 디스폰 자체는 플레이어 정리(#395)와 같은 경로 — 여기서는 그 뒤 오너 동기화까지 한다.
        // 플레이어는 살아 남아 다음 라운드에 다시 지급받으므로 슬롯을 비워 줘야 하기 때문.
        int cleared = DespawnHeldItems();
        Debug.Log($"[PlayerLoadout] 보유 아이템 회수 — client {OwnerClientId}, {cleared}개");

        // 오너 슬롯 모델에 파괴된 참조가 남지 않도록 빈 목록으로 재구성시킨다 — 안 보내면 인벤토리 UI가
        // 죽은 아이템 칸을 그대로 들고 있어 다음 라운드 지급분이 들어갈 칸이 없다.
        SyncHeldItemsRpc(BuildHeldItemRefs());
    }

    // ---- 줍기 (오너 요청 → 서버 실행) ----

    /// <summary>월드 아이템 줍기 요청. WorldItemPickup이 상호작용한 플레이어에게 호출한다. (오너에서만 유효)</summary>
    public void RequestPickup(NetworkObject itemNetworkObject)
    {
        if (!IsOwner || itemNetworkObject == null)
        {
            return;
        }

        // 용량 검사는 서버(PickupRpc)가 권위로 수행한다. 로컬 슬롯 배치(m_slotModel)는 드롭→동기화 RPC 왕복이
        // 끝나야 갱신되므로, 여기서 로컬로 미리 막으면 방금 버려 서버는 받아줄 줍기를 오거부한다 (#144).
        PickupRpc(new NetworkObjectReference(itemNetworkObject));
    }

    [Rpc(SendTo.Server)]
    private void PickupRpc(NetworkObjectReference itemRef, RpcParams rpcParams = default)
    {
        if (!itemRef.TryGet(out NetworkObject itemNetworkObject))
        {
            return;
        }

        // 이미 누군가 들고 있으면(부모가 있으면) 무시 — 중복 줍기·가로채기 방지.
        if (itemNetworkObject.transform.parent != null)
        {
            return;
        }

        // 서버 거리 검증 — 위조 RPC로 원격 줍기 방지 (#147). 클라 조준과 동일한 기준점(카메라)↔아이템
        // sqrMagnitude 비교 — root 기준이면 카메라 오프셋만큼 사거리 경계에서 오탐 거부될 수 있다.
        Vector3 toItem = itemNetworkObject.transform.position - m_interactor.AimOrigin.position;
        if (toItem.sqrMagnitude > m_pickupRange * m_pickupRange)
        {
            return;
        }

        // 벽 너머 줍기 차단 — 클라 조준만 막으면 위조 RPC로 그대로 뚫린다 (#360)
        if (!m_interactor.HasLineOfSightTo(itemNetworkObject.transform))
        {
            return;
        }

        // 소지 3칸 제한 (#144, GDD 용량 3칸) — 꽉 차면 줍기 거부. 서버 권위 검증.
        // 개수만 필요하므로 무할당 CountHeldItems 사용 (동기화용 refs는 부착 후 아래에서 1회 빌드).
        if (CountHeldItems() >= k_maxHeldItems)
        {
            return;
        }

        ulong requester = rpcParams.Receive.SenderClientId;
        itemNetworkObject.ChangeOwnership(requester);
        AttachToParent(itemNetworkObject, ItemParent);

        SyncHeldItemsRpc(BuildHeldItemRefs());
    }

    // ---- 버리기 (오너 요청 → 서버 실행) ----

    // 버리면 묶인 대상이 주인 없이 남는 밧줄인가 — 개수로 판정한다 (#390).
    // 여분(안 묶은) 밧줄이 있으면 버릴 수 있다: 밧줄끼리 구별이 없으므로 "어느 것을 버리느냐"는 무의미하고,
    // 사용 중인 줄 수가 소지 수와 같을 때만(=여분 0) 막으면 된다.
    // TetheredCount는 서버·오너 양쪽에서 유효해(동기화 목록) 조기검증과 서버 판정이 같은 기준을 쓴다. (#369)
    private bool IsTetheredRope(ItemBase item) =>
        item is Rope && m_escorter != null && m_escorter.TetheredCount >= RopeCount;

    // 현재 장착 아이템을 버린다 (버리기 입력 핸들러).
    private void RequestDropEquipped()
    {
        if (!IsOwner)
        {
            return;
        }

        // 다운(무력화) 중에는 아이템을 버릴 수 없다 (#105)
        if (IsIncapacitated)
        {
            return;
        }

        ItemBase equipped = m_itemUser.EquippedItem;
        if (equipped == null)
        {
            return;
        }

        // 묶어 둔 대상이 있으면 그 밧줄은 버릴 수 없다 — 최종 판정은 서버(DropRpc)가 한다 (#369)
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

        // 이 플레이어가 실제로 들고 있는(부착된) 아이템만 버릴 수 있다.
        if (itemNetworkObject.transform.parent != ItemParent)
        {
            return;
        }

        itemNetworkObject.TryGetComponent(out ItemBase droppedItem);

        // 묶어 둔 대상이 있는 밧줄은 버릴 수 없다 — 줄이 손을 떠나면 묶인 NPC가 주인 없이 남는다
        // (놓아둔 대상도 포함). 오너 조기검증과 같은 기준, 최종 판정은 여기 서버가 한다. (#369)
        if (IsTetheredRope(droppedItem))
        {
            return;
        }

        // 버리는 아이템이 채널링 중이면 서버가 직접 끊는다 — 소유권 회수(RemoveOwnership) 후엔 오너의
        // 취소 RPC가 RequireOwnership에 막혀 거부되므로, 여기서 서버 권위로 중단해야 배터리 낭비·오완료를
        // 막는다. 채널링 없는 아이템은 무동작(ItemBase 기본). (드롭 중 채널링 경합 대응)
        if (droppedItem != null)
        {
            droppedItem.ServerCancelActiveUse();
        }

        // 정면 바닥에 내려놓은 뒤 분리하고, 소유권은 서버로 되돌린다(월드 상태).
        // 순서 중요 — 아이템엔 NetworkTransform이 없어, 분리 시 나가는 ParentSyncMessage가 위치를
        // 복제하는 유일한 수단이다. 분리가 먼저면 손에 있던 옛 위치가 실려 나간다 (#361).
        itemNetworkObject.transform.SetPositionAndRotation(
            ResolveDropPosition(),
            Quaternion.identity
        );
        itemNetworkObject.TrySetParent((Transform)null, true);
        itemNetworkObject.RemoveOwnership();

        SyncHeldItemsRpc(BuildHeldItemRefs());
    }

    // 정면 드롭 지점을 구한다 — 앞이 벽이면 벽 앞으로 당긴다 (서버에서 호출).
    // 벽에 붙어 버리면 아이템이 벽 너머로 넘어가는데, 가시선 차단(#360) 이후로는 그렇게 넘어간
    // 아이템을 벽 너머로 주울 수도 없어 영영 회수 불가가 된다.
    // "벽"의 정의는 조준 쪽과 하나로 묶는다(PlayerInteractor.LosBlockMask) — 따로 두면
    // 조준은 막히는데 드롭은 통과하는 식으로 어긋난다.
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
            // 벽에 바짝 붙었으면 0까지 줄어 발밑에 떨어진다 — 벽 안쪽보다 낫다.
            distance = Mathf.Max(0f, obstacle.distance - k_dropWallMargin);
        }

        return transform.position + transform.forward * distance;
    }

    // ---- 밧줄 자원 게이트 (#269) ----
    // 수갑 소모·반환(#229: HasHandcuffs/ConsumeHandcuffsTo/TryRecoverHandcuffs)은 밧줄이 소모형이 아니게 되며 제거됐다. (#369)

    /// <summary>이 플레이어가 밧줄을 보유 중인가 — 풀기 등 "한 개라도 있으면 되는" 게이트. (#269)</summary>
    public bool HasRope => RopeCount > 0;

    /// <summary>
    /// 보유 밧줄 개수 — 동시에 묶을 수 있는 인원의 상한이다 (밧줄 1개당 NPC 1명, #390).
    /// 부착된 자식 기준이라 서버·오너 양쪽에서 같은 값이 나온다(부착은 NGO가 복제한다).
    /// 밧줄끼리는 구별하지 않는다 — 어느 줄이 어느 대상에 걸렸는지는 추적하지 않고 개수만 센다.
    /// </summary>
    public int RopeCount
    {
        get
        {
            Transform parent = ItemParent;
            int count = 0;
            for (int i = 0; i < parent.childCount; i++)
            {
                if (parent.GetChild(i).GetComponent<Rope>() != null)
                {
                    count++;
                }
            }

            return count;
        }
    }

    // ---- 서버 → 오너: 보유 목록 동기화 ----

    // 플레이어에 현재 부착된 아이템들의 참조 목록을 만든다 (서버 진실).
    // ItemParent 자식 중 실제 보유 아이템 개수 — 용량 가드용. BuildHeldItemRefs와 달리 List/배열 무할당.
    private int CountHeldItems()
    {
        Transform parent = ItemParent;
        int count = 0;
        for (int i = 0; i < parent.childCount; i++)
        {
            if (parent.GetChild(i).GetComponent<ItemBase>() != null)
            {
                count++;
            }
        }

        return count;
    }

    private NetworkObjectReference[] BuildHeldItemRefs()
    {
        Transform parent = ItemParent;
        List<NetworkObjectReference> refs = new List<NetworkObjectReference>();

        for (int i = 0; i < parent.childCount; i++)
        {
            ItemBase item = parent.GetChild(i).GetComponent<ItemBase>();

            // 디스폰된 아이템은 건너뛴다 — Despawn은 즉시지만 GameObject 파괴는 프레임 끝이라 회수
            // (ServerClearHeldItems) 직후에도 자식으로 남는다. 그대로 참조를 만들면 생성자가 던져
            // 동기화 RPC가 발송되지 않고, 오너는 파괴된 아이템을 계속 장착·표시한다. (#370)
            if (item != null && item.NetworkObject != null && item.NetworkObject.IsSpawned)
            {
                refs.Add(new NetworkObjectReference(item.NetworkObject));
            }
        }

        return refs.ToArray();
    }

    // 오너가 서버 진실 목록으로 자기 인벤토리를 재구성한다 — 줍기/버리기로 목록이 바뀌어도
    // 휠 순환이 최신 목록을 대상으로 하고, 장착 중이던 아이템은 신원으로 유지된다(클로버링 방지).
    [Rpc(SendTo.Owner)]
    private void SyncHeldItemsRpc(NetworkObjectReference[] itemRefs)
    {
        // 갓 스폰된 아이템 NetworkObject가 이 클라에 아직 도착하지 않았을 수 있다(스폰 메시지 vs RPC
        // 도착 순서 경쟁). 특히 원격 클라의 초기 지급에서 참조가 즉시 해석되지 않아 지급이 누락된다.
        // 전부 해석될 때까지 몇 프레임 기다렸다가 재구성한다.
        ResolveAndRebuildAsync(itemRefs).Forget();
    }

    private async UniTaskVoid ResolveAndRebuildAsync(NetworkObjectReference[] itemRefs)
    {
        const int k_maxWaitFrames = 120;
        for (int frame = 0; frame < k_maxWaitFrames && !AllResolved(itemRefs); frame++)
        {
            await UniTask.Yield(PlayerLoopTiming.Update);

            // 대기 중 디스폰(퇴장 등)되면 중단 — 파괴된 객체 접근 방지.
            if (this == null || !IsSpawned)
            {
                return;
            }
        }

        RebuildHeldItems(itemRefs);
    }

    private static bool AllResolved(NetworkObjectReference[] itemRefs)
    {
        foreach (NetworkObjectReference itemRef in itemRefs)
        {
            if (!itemRef.TryGet(out _))
            {
                return false;
            }
        }

        return true;
    }

    // 서버 진실 목록(flat)을 로컬 슬롯 배치와 대조(reconcile)한다 — 버린 아이템은 그 칸만 비우고,
    // 새 아이템은 첫 빈 칸에 넣어 나머지 아이템의 칸 위치를 유지한다 (#144, positional).
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

        // 서버 진실 목록으로 칸 배치를 대조한다 — 사라진 아이템은 그 칸만 비우고(버린 자리 유지),
        // 새 아이템은 첫 빈 칸에 넣는다(positional, #144). 유지할 선택 인덱스를 돌려받는다:
        //  - 장착 아이템을 버리면 그 칸이 비므로 인덱스는 그대로 두고 빈손이 된다(자동 전환 안 함).
        //  - 빈 칸을 의도적으로 선택한 빈손 상태도 그대로 유지된다.
        //  - 아직 아무것도 선택한 적 없을 때(-1, 초기 지급)만 첫 아이템 칸을 장착한다.
        int keptIndex = m_slotModel.Reconcile(incoming);
        EquipSlot(keptIndex); // 인덱스 세팅·장착·하이라이트 이벤트를 한 경로로 통일 (index -1이면 빈손)

        OnSlotsChanged?.Invoke();
    }

    // ---- 슬롯 선택/정렬 (#46, #144, 오너 로컬) ----

    // 마우스 휠 위 — 이전 슬롯의 아이템으로 순환 전환.
    private void EquipPrevious() => Cycle(-1);

    // 마우스 휠 아래 — 다음 슬롯의 아이템으로 순환 전환.
    private void EquipNext() => Cycle(1);

    // 현재 슬롯에서 direction 방향으로 한 칸 이동해 장착한다 — 빈 칸도 대상(숫자키와 동일하게 빈손). (#144)
    private void Cycle(int direction)
    {
        // 다운(무력화) 중에는 마우스 휠 아이템 전환 차단 (#105)
        if (IsIncapacitated)
        {
            return;
        }

        // 빈손이면 첫 칸 기준으로 순환한다 — 인덱스 계산(음수 보정 포함)은 슬롯 모델이 담당.
        EquipSlot(m_slotModel.NextIndex(direction));
    }

    /// <summary>슬롯을 직접 선택해 장착한다 (숫자키 1~3, #144). 빈 칸이면 빈손이 된다.</summary>
    public void SelectSlot(int index)
    {
        // 다운(무력화) 중에는 아이템 전환 차단 (#105)
        if (IsIncapacitated)
        {
            return;
        }

        if (!m_slotModel.IsValidIndex(index))
        {
            return;
        }

        EquipSlot(index);
    }

    // 선택 슬롯을 index로 바꾸고 그 칸 아이템(빈 칸이면 null=빈손)을 장착한다. index -1이면 빈손.
    // 빈 칸↔빈 칸이면 SetEquippedItem이 이벤트를 안 내므로, 인덱스 변경 이벤트를 따로 발행해 UI 하이라이트를 갱신한다.
    private void EquipSlot(int index)
    {
        m_slotModel.SetEquippedIndex(index);
        m_itemUser.SetEquippedItem(m_slotModel.Equipped);
        OnEquippedSlotChanged?.Invoke();
    }

    /// <summary>두 슬롯의 내용을 맞바꾼다 (편집 모드 드래그 정렬, #144). 순수 오너 로컬 — 서버 무관.</summary>
    public void SwapSlots(int a, int b)
    {
        // 다운(무력화) 중에는 슬롯 정렬 차단 (#105, Cycle/SelectSlot과 동일 관례)
        if (IsIncapacitated)
        {
            return;
        }

        // 스왑·장착 인덱스 추적은 순수 로직이라 모델이 담당 — 유효하지 않으면(같은 칸·범위 밖) 무동작.
        // 장착 항목 자체는 그대로라(칸 위치만 바뀜) OnEquippedSlotChanged는 불필요, OnSlotsChanged만 발행.
        if (m_slotModel.TrySwap(a, b))
        {
            OnSlotsChanged?.Invoke();
        }
    }

    // ---- 공통 ----

    // 스폰된 아이템을 플레이어 부모에 부착하고 로컬 원점에 맞춘다 (서버에서 호출).
    private static void AttachToParent(NetworkObject itemNetworkObject, Transform parent)
    {
        itemNetworkObject.TrySetParent(parent, false);
        itemNetworkObject.transform.localPosition = Vector3.zero;
        itemNetworkObject.transform.localRotation = Quaternion.identity;
    }
}
