using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어의 아이템 보유·장착·줍기·버리기를 관리한다. (#47, #46, #88)
/// 아이템은 독립 NetworkObject 프리팹이므로(#88) 서버가 스폰·소유권 부여·부착을 담당하고,
/// 오너 클라는 서버가 보낸 보유 목록으로 자기 인벤토리(Slots, 고정 5칸)를 재구성해 휠/숫자키 전환(#46, #144)·
/// 1인칭 손 표시(#45)·인벤토리 UI(#144)에 쓴다. 칸 배치(어느 칸에 뭐가 있는지)는 오너 로컬 관심사.
///
/// 권위 구분:
/// - 서버: 시작 지급 스폰, 줍기/버리기 요청 검증, 소유권 이전, 부모(부착) 변경.
/// - 오너: 줍기/버리기 입력 발신, 휠 순환 장착, 손 표시.
/// 3인칭(타 플레이어) 장착 표시는 이 이슈 범위 밖 — 후속.
/// </summary>
// TODO: 장착/사용 실행을 서버 권위로 옮긴다 (지금은 장착 상태가 오너 로컬)
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

    /// <summary>플레이어 소지 슬롯 수 — 고정 5칸 (#144, 3칸에서 확장 #793, GDD 용량 5칸).</summary>
    public const int k_maxHeldItems = 5;

    // 드롭 장애물 탐침 높이(m). 발밑에서 쏘면 바닥·문턱에 걸리므로 허리 높이에서 앞을 훑는다.
    private const float k_dropProbeHeight = 0.5f;

    // 벽에서 띄울 여유(m). 줍기 박스 최소 크기(0.5m)의 절반 — 박스가 벽을 파고들지 않을 만큼.
    private const float k_dropWallMargin = 0.3f;

    // 오너 로컬 슬롯 배치 모델 — 어느 칸에 뭐가 있는지·선택 인덱스와 그 위의 대조/순환/선택/스왑은
    // 순수 인덱스 연산이라 Netcode와 무관한 LoadoutSlots<T>로 분리했다(단위 테스트 가능). 부착·소유권·
    // 동기화는 이 컴포넌트가, 칸 배치는 모델이 담당한다. 서버 동기화는 flat list(HeldItems.BuildRefs) 그대로. (#144)
    private readonly LoadoutSlots<ItemBase> m_slotModel = new LoadoutSlots<ItemBase>(k_maxHeldItems);

    // 부착된 아이템 집합(서버 진실) — 개수·소속 판정·부착·디스폰은 전부 이쪽을 거친다.
    // 부착 지점 자식을 네 군데서 따로 순회하던 것을 하나의 접근 경로로 모은 것. 이 컴포넌트에는
    // "들 자격이 있는가"(권위·거리·가시선·용량)만 남는다.
    private HeldItems m_held;

    // 부착 지점의 자식 변화 훅 — 부착 지점에 런타임으로 붙인다. Awake에서 확정되고 바뀌지 않는다. (#487)
    private HeldItemsWatcher m_heldWatcher;

    private PlayerItemUser m_itemUser;
    private PlayerInputHandler m_inputHandler;
    private PlayerIncapacitation m_incapacitation; // 다운(무력화) 중 아이템 전환·버리기 차단용 (#105)
    private PlayerEscorter m_escorter; // 밧줄을 묶어 둔 동안 그 밧줄 버리기 차단용 (#369)

    // 서버 줍기 거리 검증용 — PlayerInteractor의 조준 사거리·기준점을 그대로 재사용한다 (#147).
    // 값을 따로 두지 않고 여기서 읽어야 조준-줍기 사거리가 항상 정합된다.
    private PlayerInteractor m_interactor;
    private PlayerTerminalFocus m_terminalFocus;
    private float m_pickupRange;

    // 다운(무력화) 중 여부 — 무력화 컴포넌트가 없으면(테스트 구성 등) 항상 false. (PlayerMovement 관례)
    // 인벤토리 UI(#144)가 편집 모드 진입 게이트에 쓰므로 public.
    public bool IsIncapacitated => m_incapacitation != null && m_incapacitation.IsIncapacitated;

    // 본부 복구 단말을 보고 있는 중인가 (#762). 단말이 코드를 숫자 키로 받는데 슬롯 선택도 숫자키라
    // 막지 않으면 코드를 누를 때마다 손에 든 것이 바뀐다. 인벤토리 UI(#144)도 편집 모드 진입
    // 게이트에 쓰므로 public — IsIncapacitated와 같은 이유다.
    public bool IsTerminalFocused => m_terminalFocus != null && m_terminalFocus.IsFocusing;

    /// <summary>고정 5칸 슬롯 (빈 칸 = null). 인벤토리 UI(#144)·휠 전환(#46)이 사용한다. (오너 로컬)</summary>
    public IReadOnlyList<ItemBase> Slots => m_slotModel.Slots;

    /// <summary>슬롯 구성 변경 이벤트 — 줍기/버리기/초기 지급/드래그 스왑 시 발행. 인벤토리 UI(#144)가 구독.</summary>
    public event Action OnSlotsChanged;

    /// <summary>현재 선택(장착)된 슬롯 인덱스. 빈손이면 -1. 빈 칸을 선택하면 그 칸 인덱스가 된다.</summary>
    public int EquippedIndex => m_slotModel.EquippedIndex;

    /// <summary>선택 슬롯 이동 이벤트 — 빈 칸↔빈 칸 전환처럼 장착 아이템이 안 바뀌어도 발행. UI 하이라이트(#144)가 구독.</summary>
    public event Action OnEquippedSlotChanged;

    /// <summary>
    /// 부착 목록(무엇을 들고 있나)이 바뀔 때 — <b>전 피어에서</b> 발행된다. (#487)
    ///
    /// <see cref="OnSlotsChanged"/>와 혼동하지 말 것. 그쪽은 서버 동기화 RPC(<c>SendTo.Owner</c>)에서
    /// 나오는 <b>오너 로컬</b> 이벤트라 남의 화면에는 오지 않고, 대신 칸 배치까지 반영된 뒤에 온다.
    /// 자기 인벤토리 표시는 그쪽을, <b>남의 소지품</b>을 들여다보는 쪽(약탈 창)은 이쪽을 쓴다.
    /// 이쪽은 칸 배치를 모른다 — 부착 순서만 안다.
    /// </summary>
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
        // 부착 지점은 직렬화 값이라 여기서 확정된다 — 런타임에 바뀌지 않는다.
        Transform anchor = m_itemAnchor != null ? m_itemAnchor : transform;
        m_held = new HeldItems(anchor);

        // 부착 목록 변화를 '전 피어에서' 잡는 훅 (#487) — 자세한 사정은 HeldItemsWatcher 문서 주석.
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

    // ---- 형제 컴포넌트(PlayerItemSupply)와 공유하는 면 ----

    /// <summary>
    /// 부착된 아이템 집합. 라운드 경계 지급·회수를 맡는 <see cref="PlayerItemSupply"/>가 빌려 쓴다.
    /// 집합은 플레이어당 하나여야 한다 — 각자 만들면 같은 앵커를 두 객체가 따로 들여다보게 된다.
    /// </summary>
    internal HeldItems Held => m_held;

    /// <summary>
    /// 서버가 소지품을 바꾼 뒤 오너에게 알린다. 서버 전용.
    /// 지급·회수·줍기·버리기 네 경로의 공통 마무리 — 오너 인벤토리 재구성의 유일한 통로다.
    /// </summary>
    internal void ServerNotifyHeldItemsChanged() => SyncHeldItemsRpc(m_held.BuildRefs());

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

        // 소지 5칸 제한 (#144/#793, GDD 용량 5칸) — 꽉 차면 줍기 거부. 서버 권위 검증.
        // 개수만 필요하므로 무할당 Count 사용 (동기화용 refs는 부착 후 아래에서 1회 빌드).
        if (m_held.Count >= k_maxHeldItems)
        {
            return;
        }

        ulong requester = rpcParams.Receive.SenderClientId;
        itemNetworkObject.ChangeOwnership(requester);
        m_held.Attach(itemNetworkObject);

        ServerNotifyHeldItemsChanged();
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
        if (!m_held.Holds(itemNetworkObject))
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
        Vector3 dropPosition = ResolveDropPosition();
        itemNetworkObject.transform.SetPositionAndRotation(dropPosition, Quaternion.identity);
        WorldItemPickup.SettleOnGround(itemNetworkObject.gameObject, dropPosition.y);
        itemNetworkObject.TrySetParent((Transform)null, true);
        itemNetworkObject.RemoveOwnership();

        ServerNotifyHeldItemsChanged();
    }

    // ---- 소모 (아이템이 서버에서 스스로 호출) ----

    /// <summary>
    /// 소지품 하나를 소모한다 — 디스폰하고 오너 인벤토리를 다시 맞춘다. 서버(또는 오프라인) 전용. (#613)
    ///
    /// 버리기(<see cref="DropRpc"/>)와 다른 점은 <b>월드에 남기지 않는다</b>는 것뿐이라 위치 보정도
    /// 소유권 반납도 없다. 부르는 쪽은 일회용 아이템 자신이고(<see cref="ItemBase.ServerConsume"/>),
    /// 구매 목록 정리는 거기서 이미 끝났다.
    ///
    /// 서버가 소지품을 바꾸는 경로가 하나 늘었다(지급·회수·줍기·버리기·소모) — 다섯 경로 모두
    /// <see cref="ServerNotifyHeldItemsChanged"/>로 수렴한다는 규칙은 그대로다.
    /// </summary>
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

        // 이 플레이어가 실제로 들고 있는 것만 소모할 수 있다 — 버리기와 같은 권한 검증.
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
            Destroy(item.gameObject); // 비네트워크 Play 테스트 폴백
        }

        // 파괴된 참조가 오너 슬롯에 남지 않게 한다 — 회수(#370)가 빈 목록을 보내는 것과 같은 이유.
        // 디스폰은 즉시지만 GameObject 파괴는 프레임 끝이라, BuildRefs가 스폰 여부로 걸러 준다.
        ServerNotifyHeldItemsChanged();
    }

    /// <summary>
    /// 손에서 떼어 낼 수 있는 소지품을 <paramref name="into"/>에 담는다(기존 내용은 지운다).
    /// 기준은 버리기와 같다 — 묶어 둔 밧줄은 빠진다(손을 떠나면 묶인 NPC가 주인 없이 남는다, #369).
    ///
    /// <b>목록만 돌려준다</b> — 무엇을 어떻게 가져가는지는 부르는 쪽의 행동이다.
    /// 지금 읽는 쪽은 소매치기 탈취(<see cref="Pickpocket.ServerStealFrom"/>, #303) 하나다.
    /// </summary>
    public void CollectDetachableItems(List<ItemBase> into)
    {
        m_held.CollectInto(into);
        into.RemoveAll(IsTetheredRope);
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

        // 높이는 아직 발밑 y다 — 앞쪽 지면이 높으면 파묻히므로(#937) 하향 레이캐스트로 실제 지면을 구한다.
        Vector3 candidate = transform.position + transform.forward * distance;
        return DeliveryScatter.SnapToGround(candidate, m_groundMask);
    }

    // ---- 밧줄 자원 게이트 (#269) ----

    /// <summary>이 플레이어가 밧줄을 보유 중인가 — 풀기 등 "한 개라도 있으면 되는" 게이트. (#269)</summary>
    public bool HasRope => RopeCount > 0;

    /// <summary>
    /// 보유 밧줄 개수 — 동시에 묶을 수 있는 인원의 상한이다 (밧줄 1개당 NPC 1명, #390).
    /// 부착된 자식 기준이라 서버·오너 양쪽에서 같은 값이 나온다(부착은 NGO가 복제한다).
    /// 밧줄끼리는 구별하지 않는다 — 어느 줄이 어느 대상에 걸렸는지는 추적하지 않고 개수만 센다.
    /// </summary>
    public int RopeCount => m_held.CountOf<Rope>();

    // ---- 부활 키트 게이트 (#820) ----

    /// <summary>
    /// 소지 중인 부활 키트(없으면 null) — 자가 부활 게이트. <b>장착 여부와 무관하다</b> — 5칸 어디에
    /// 있어도 유효해야 한다(무력화 중엔 슬롯 전환이 막혀 장착 아이템으로 제한하면 운이 갈린다).
    /// HasRope/RopeCount와 같은 부착 자식 기준이라 Die 중 소유권 이관(#763)에도 영향받지 않는다.
    /// </summary>
    public ReviveKit HeldReviveKit => m_held.FirstOf<ReviveKit>();

    // ---- 서버 → 오너: 보유 목록 동기화 ----

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
        // 대기 중 디스폰(퇴장 등)되면 중단 — 파괴된 객체 접근 방지.
        EResolveResult result = await NetworkRefResolver.WaitAsync(
            itemRefs,
            () => this != null && IsSpawned
        );

        if (result == EResolveResult.Aborted)
        {
            return;
        }

        // 상한을 넘겨도(TimedOut) 재구성은 진행한다 — 해석된 것만 반영되고, 못 푼 참조는
        // RebuildHeldItems가 걸러 낸다. 다음 동기화 RPC가 다시 채워 줄 기회가 있다.
        RebuildHeldItems(itemRefs);
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

        // 단말 입력 중에도 막는다 (#762) — 숫자키만 막으면 휠로 바뀌어 같은 사고가 남는다
        if (IsTerminalFocused)
        {
            return;
        }

        // 빈손이면 첫 칸 기준으로 순환한다 — 인덱스 계산(음수 보정 포함)은 슬롯 모델이 담당.
        EquipSlot(m_slotModel.NextIndex(direction));
    }

    /// <summary>슬롯을 직접 선택해 장착한다 (숫자키 1~5, #144/#793). 빈 칸이면 빈손이 된다.</summary>
    public void SelectSlot(int index)
    {
        // 다운(무력화) 중에는 아이템 전환 차단 (#105)
        if (IsIncapacitated)
        {
            return;
        }

        // 복구 단말에 코드를 넣는 동안은 숫자키가 그쪽 것이다 (#762)
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

    // 선택 슬롯을 index로 바꾸고 그 칸 아이템(빈 칸이면 null=빈손)을 장착한다. index -1이면 빈손.
    // 빈 칸↔빈 칸이면 SetEquippedItem이 이벤트를 안 내므로, 인덱스 변경 이벤트를 따로 발행해 UI 하이라이트를 갱신한다.
    private void EquipSlot(int index)
    {
        // 전환 전 아이템 — 게이지 소유자 판별 기준 (#725).
        ItemBase previousEquipped = m_itemUser.EquippedItem;

        m_slotModel.SetEquippedIndex(index);
        m_itemUser.SetEquippedItem(m_slotModel.Equipped);

        // 장착을 바꾸면 진행 중이던 게이지를 내린다 (#455). SetEquippedItem이 내려놓는 아이템의
        // CancelUse로 채널링 자체는 끊지만, 테이저 충전처럼 채널링이 아닌 게이지는 그 경로로 지워지지
        // 않는다 — 손에 없는 아이템의 진행도가 화면에 남는다. 채널링 쪽도 서버 통지를 기다리지 않고
        // 즉시 사라져 반응이 또렷해진다.
        //
        // 게이지는 아이템·구조·포박이 공유하므로 이전 아이템 자신의 ChannelGauge로 내린다 — 구조 채널링
        // 게이지처럼 이전 아이템이 띄운 게 아니면 토큰이 달라 무동작으로 넘어간다. (#725)
        // 게이지를 안 쓰는 아이템(밧줄 등)은 컴포넌트가 없어 ?.에서 그대로 넘어간다.
        //
        // 오너에서만: 이 메서드는 서버 목록 동기화(RebuildHeldItems) 경로로도 불려 비오너 피어에서
        // 실행되므로, 가드가 없으면 남의 아이템 정리가 내 화면 게이지를 지운다.
        if (IsOwner)
        {
            if (previousEquipped != null)
                previousEquipped.GetComponent<ChannelGauge>()?.HideLocal();

            // 새로 든 아이템이 진행 중인 것을 갖고 있으면 다시 띄운다 — 테이저 충전 중에 다른 걸 들었다가
            // 돌아온 경우 (#455). 순서가 중요하다: 먼저 내려서 이전 아이템 게이지를 확실히 지운 뒤,
            // 새 아이템이 자기 것을 올린다.
            // EquippedItem 프로퍼티를 쓰는 이유는 Unity 파괴 참조(가짜 null) 걸러내기 — 라운드 종료
            // 회수(#370)로 아이템이 디스폰된 직후 이 경로가 돌 수 있다.
            ItemBase equipped = m_itemUser.EquippedItem;
            if (equipped != null)
                equipped.OnEquipped();
        }

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
}
