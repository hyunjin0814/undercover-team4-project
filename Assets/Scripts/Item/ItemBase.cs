using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 모든 아이템의 공통 기반 — 표시용 공통 데이터와 사용 진입점(Use)을 정의한다.
/// 스캐너·수갑 등 하위 아이템이 이 클래스를 상속해 Use()를 구현한다.
/// 아이템은 독립 NetworkObject 프리팹이므로(#88) NetworkBehaviour 계열을 상속한다 —
/// 배터리 등 상태를 NetworkVariable로 동기화하고, 줍기 시 소유권이 이전된다.
/// 채널링 게이지·오너 피드백은 기반 ChanneledInteractionBehaviour가 제공한다 (#184/#91).
/// </summary>
public abstract class ItemBase : ChanneledInteractionBehaviour
{
    [Header("아이템 정보")]
    [SerializeField]
    private LocalizedString m_itemName;

    [SerializeField]
    private Sprite m_itemIcon;

    [SerializeField]
    private LocalizedString m_itemDescription;

    [Header("1인칭 표시")]
    [Tooltip("장착 시 1인칭 손에 표시할 모델 프리팹. 비우면 손만 표시된다 (#45)")]
    [SerializeField]
    private GameObject m_heldModelPrefab;

    [Header("3인칭 손 그립 (#151)")]
    [Tooltip("손 본 앵커 기준 위치 오프셋(m). 아이템마다 모델 피벗이 달라 개별 조정이 필요하다")]
    [SerializeField]
    private Vector3 m_heldPositionOffset;

    [Tooltip("손 본 앵커 기준 회전 오프셋(도). 실제로 손에 쥔 각도로 맞춘다")]
    [SerializeField]
    private Vector3 m_heldRotationOffset;

    [Tooltip("1인칭 손 손가락 프리셋 — 이 아이템을 들 때 손 모양 (#265)")]
    [SerializeField]
    private HandGrip m_handGrip = HandGrip.Relaxed;

    [Header("조준 피드백")]
    [Tooltip("이 아이템으로 사용 가능한 대상을 조준했을 때의 윤곽선 색 (#184)")]
    [SerializeField]
    private Color m_targetOutlineColor = new Color(1f, 0.85f, 0.2f);

    [Header("상점 (#182)")]
    [Tooltip("상점 판매가. 0이면 비매품 — 기본 지급품(스캐너·밧줄·진압봉)은 건드리지 않는다")]
    [Min(0)]
    [SerializeField]
    private int m_shopPrice;

    /// <summary>인벤토리·UI에 표시되는 아이템 이름.</summary>
    public LocalizedString ItemName => m_itemName;

    /// <summary>인벤토리·UI에 표시되는 아이템 아이콘.</summary>
    public Sprite ItemIcon => m_itemIcon;

    /// <summary>인벤토리·UI에 표시되는 아이템 설명.</summary>
    public LocalizedString ItemDescription => m_itemDescription;

    /// <summary>장착 시 1인칭 손에 들리는 모델 프리팹. 없으면 null — PlayerHandView가 표시를 생략한다. (#45)</summary>
    public GameObject HeldModelPrefab => m_heldModelPrefab;

    /// <summary>3인칭 손 앵커 기준 위치 오프셋. PlayerHeldItemView가 표시 모델에 적용한다. (#151)</summary>
    public Vector3 HeldPositionOffset => m_heldPositionOffset;

    /// <summary>3인칭 손 앵커 기준 회전 오프셋(오일러 각). PlayerHeldItemView가 표시 모델에 적용한다. (#151)</summary>
    public Vector3 HeldRotationOffset => m_heldRotationOffset;

    /// <summary>이 아이템으로 사용 가능한 대상을 조준 중일 때의 윤곽선 색. (#184)</summary>
    public Color TargetOutlineColor => m_targetOutlineColor;

    /// <summary>1인칭 손 손가락 프리셋. PlayerHandView가 장착 시 FP 손에 적용한다. (#265)</summary>
    public HandGrip HandGrip => m_handGrip;

    /// <summary>
    /// 상점 판매가 — 진열대(ShopStand)가 참조한 프리팹에서 읽는다. 0이면 비매품. (#182)
    /// 상점 전용 ScriptableObject를 따로 두지 않는 이유: 표시 이름·아이콘·설명이 이미 여기 있어
    /// 판매가만 얹으면 끝이고, SO를 만들면 그 필드를 복제하는 껍데기가 된다 (GDD 10-4 이탈 사유).
    /// </summary>
    public int ShopPrice => m_shopPrice;

    /// <summary>이 아이템을 들고 있는 플레이어의 인터랙터. 바닥에 놓여 있으면 null.</summary>
    // 줍기/버리기로 부모가 바뀌므로 캐시하지 않고 접근할 때마다 해석한다 — 캐시하면 전 주인을 가리킨다.
    protected PlayerInteractor Holder => GetComponentInParent<PlayerInteractor>();

    /// <summary>
    /// 이 아이템을 지금 저 대상에 사용할 수 있는지 — 조준 피드백(윤곽선) 판정용. 기본값 false. (#184)
    /// Use()의 조기 검증과 같은 기준을 유지해야 "윤곽선이 떴는데 사용은 안 됨"이 안 생긴다.
    /// 매 프레임 호출되므로(InteractionFeedback) 무거운 연산은 피할 것.
    /// </summary>
    public virtual bool CanTarget(GameObject aimTarget) => false;

    /// <summary>
    /// 조준 안내에 띄울 동작 문구 — "묶기"처럼 동사로. 키(좌클릭)는 표시 쪽이 붙인다. (#664)
    /// <see cref="CanTarget"/>이 true일 때만 읽힌다. null이면 안내가 뜨지 않는다.
    /// 대상에 따라 동작이 갈리면 여기서 갈라 준다 — CanTarget의 갈래와 순서를 맞출 것.
    /// </summary>
    public virtual LocalizedString TargetPromptLabel(GameObject aimTarget) => null;

    /// <summary>
    /// 현재 아이템을 사용할 수 있는지 — UI 표시(장착 아이콘 활성/비활성 등)용 힌트. 기본값 true.
    /// 사용 가능 여부의 최종 판정은 Use() 구현부가 스스로 수행한다 (아래 Use() 계약 참고).
    /// </summary>
    public virtual bool CanUse() => true;

    /// <summary>
    /// 아이템 사용 진입점. 하위 클래스가 구체 동작을 구현한다.
    /// 계약: 호출부(PlayerItemUser)는 CanUse()로 게이트하지 않고 이 메서드를 호출한다. 따라서 구현부가
    /// 진입 시 스스로 CanUse()를 확인하고, 사용 불가면 사유를 알린 뒤 반환해야 한다 —
    /// 이래야 "왜 안 되는지"를 아이템이 직접 낼 수 있다.
    /// </summary>
    /// <param name="target">
    /// 사용 대상 — PlayerInteractor가 겨냥한 오브젝트. 겨냥한 것이 없으면 null.
    /// 하위 아이템이 이 대상에서 필요한 컴포넌트를 조회한다 (Scanner→CitizenIdentity #34, Handcuffs→NpcController #35).
    /// </param>
    // 네트워크 전환 패턴: 오너 입력 → 클라에서 대상 조기 검증 → ServerRpc 요청 → 서버가 실제 효과 실행/검증 후 동기화.
    // 각 하위 구현(Scanner, Handcuffs 등)이 이 패턴을 직접 담당한다 (#55).
    public abstract void Use(GameObject target);

    /// <summary>
    /// 진행 중인 사용(채널링)을 중단한다 — 좌클릭을 떼면 PlayerItemUser가 호출한다 (#91).
    /// 오너 클라의 '의도'이므로 구현부가 서버에 취소를 요청한다 (Scanner.CancelScan 관례).
    /// 채널링이 없는 즉발 아이템은 기본 구현(무동작)을 그대로 쓴다.
    /// </summary>
    public virtual void CancelUse() { }

    /// <summary>
    /// 서버 권위로 진행 중인 사용(채널링)을 즉시 중단한다 — 소유권 이전을 동반하는 경로(버리기)에서
    /// 서버가 직접 호출한다. <see cref="CancelUse"/>는 소유권이 회수된 원격 드롭에선 취소 RPC가
    /// 거부되지만(RequireOwnership), 이건 서버가 자기 채널을 직접 끊으므로 경합이 없다.
    /// 채널링 없는 아이템은 무동작(기본).
    /// </summary>
    public virtual void ServerCancelActiveUse() { }

    /// <summary>
    /// 이 아이템을 <b>소모</b>한다 — 손에서 빼 디스폰하고, 상점 구매품이면 다음 라운드 배달 목록에서도
    /// 뺀다. 서버(또는 오프라인) 전용. 일회용 소지품이 자기 사용을 마치며 스스로 부른다. (#613)
    ///
    /// 소모형의 첫 사례가 부활 키트(<see cref="ReviveKit"/>)라 경로를 여기 판다 — 뒤에 올 소모형
    /// (구역 스캔 #490 등)도 이걸 그대로 쓴다. 아이템 종류와 무관한 처리라 하위가 아니라 기반에 둔다.
    ///
    /// <b>구매 목록에서 빼는 것이 핵심이다.</b> 배달(ShopDelivery)은 매 라운드 구매 목록을 다시
    /// 훑으므로, 빼지 않으면 쓴 키트가 다음 라운드에 또 배달돼 일회용이 아니게 된다.
    /// 잃어버린 구매품을 목록에서 빼는 소매치기(<c>Pickpocket.ServerLoseStolenItem</c>, #303)와 같은 처리다.
    /// </summary>
    public void ServerConsume()
    {
        if (IsSpawned && !IsServer)
            return; // 서버 권위 방어

        // 기본 지급품에는 표식이 없다 — 그쪽은 어차피 매 라운드 다시 지급되므로 뺄 목록도 없다
        if (TryGetComponent(out ShopDeliveredItem delivered))
        {
            App.Game.ShopPurchases?.RemoveCarried(delivered.SourcePrefab);
        }

        // 손에 있으면 주인이 디스폰하고 오너 인벤토리까지 갱신한다 — 소지품 변경 통지는 주인 몫이다.
        // 바닥에 놓인 것을 소모하는 경로는 아직 없지만(사용은 든 상태에서만 일어난다), 주인을 못 찾았다고
        // 아이템이 살아남으면 "썼는데 그대로 있는" 상태가 되므로 스스로 디스폰한다.
        PlayerInteractor holder = Holder;
        PlayerLoadout loadout = holder != null ? holder.GetComponent<PlayerLoadout>() : null;
        if (loadout != null)
        {
            loadout.ServerConsumeHeldItem(this);
            return;
        }

        if (NetworkObject != null && NetworkObject.IsSpawned)
        {
            NetworkObject.Despawn(destroy: true);
        }
        else
        {
            Destroy(gameObject); // 비네트워크 Play 테스트 폴백
        }
    }

    /// <summary>
    /// 이 아이템이 손에 장착됐다 — 오너 클라에서만 호출된다(PlayerLoadout.EquipSlot). (#455)
    /// 장착 전환은 진행 중이던 게이지를 내리는데, 새로 든 아이템이 아직 진행 중인 것을 갖고 있으면
    /// 여기서 다시 띄운다(테이저 충전). 그런 상태가 없는 아이템은 무동작(기본).
    /// 장착 해제 쪽 대응 훅은 없다 — 게이지를 내리는 것은 아이템 종류와 무관해 EquipSlot이 한 번에 처리한다.
    /// </summary>
    public virtual void OnEquipped() { }
}
