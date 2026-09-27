using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 모든 아이템의 공통 기반 — 표시 데이터와 사용 진입점(Use)을 정의한다.
/// 독립 NetworkObject 프리팹이며, 게이지·토스트·로그는 능력 컴포넌트로 붙인다.
/// </summary>
public abstract class ItemBase : NetworkBehaviour
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

    [Tooltip(
        "3인칭 손은 손가락 프리셋이 안 걸려 펴진 채라, 1인칭에 맞춘 그립이 손을 뚫는 아이템이 있다."
            + " 체크하면 3인칭 표시만 아래 값으로 따로 잡는다 (#843)"
    )]
    [SerializeField]
    private bool m_overrideThirdPersonGrip;

    [SerializeField]
    private Vector3 m_thirdPersonPositionOffset;

    [SerializeField]
    private Vector3 m_thirdPersonRotationOffset;

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

    public LocalizedString ItemName => m_itemName;

    public Sprite ItemIcon => m_itemIcon;

    public LocalizedString ItemDescription => m_itemDescription;

    public GameObject HeldModelPrefab => m_heldModelPrefab;

    public Vector3 HeldPositionOffset => m_heldPositionOffset;

    public Vector3 HeldRotationOffset => m_heldRotationOffset;

    public Vector3 ThirdPersonPositionOffset =>
        m_overrideThirdPersonGrip ? m_thirdPersonPositionOffset : m_heldPositionOffset;

    public Vector3 ThirdPersonRotationOffset =>
        m_overrideThirdPersonGrip ? m_thirdPersonRotationOffset : m_heldRotationOffset;

    public Color TargetOutlineColor => m_targetOutlineColor;

    public HandGrip HandGrip => m_handGrip;

    public int ShopPrice => m_shopPrice;

    protected PlayerInteractor Holder => GetComponentInParent<PlayerInteractor>();

    /// <summary>지금 저 대상에 이 아이템을 쓸 수 있는지 판정한다(조준 윤곽선용). 기본은 false.</summary>
    public virtual bool CanTarget(GameObject aimTarget) => false;

    /// <summary>대상 조준 시 띄울 동작 문구를 돌려준다. null이면 안내하지 않는다.</summary>
    public virtual LocalizedString TargetPromptLabel(GameObject aimTarget) => null;

    /// <summary>대상 없이 손에 든 것만으로 쓰는 아이템의 사용 안내 문구를 돌려준다. null이면 안내하지 않는다.</summary>
    public virtual LocalizedString HeldPromptLabel() => null;

    /// <summary>지금 사용 가능한지 돌려준다(UI 힌트용). 기본은 true.</summary>
    public virtual bool CanUse() => true;

    /// <summary>아이템 사용 진입점. 구현부가 CanUse를 스스로 확인하고 불가 사유를 알려야 한다.</summary>
    public abstract void Use(GameObject target);

    /// <summary>진행 중인 사용(채널링)의 취소를 서버에 요청한다. 기본은 무동작.</summary>
    public virtual void CancelUse() { }

    /// <summary>서버에서 진행 중인 사용(채널링)을 즉시 중단한다. 기본은 무동작.</summary>
    public virtual void ServerCancelActiveUse() { }

    /// <summary>아이템을 소모한다 — 손에서 빼 디스폰하고 상점 배달 목록에서도 뺀다. 서버(또는 오프라인) 전용.</summary>
    public void ServerConsume()
    {
        if (IsSpawned && !IsServer)
            return;

        if (TryGetComponent(out ShopDeliveredItem delivered))
        {
            App.Game.ShopPurchases?.RemoveCarried(delivered.SourcePrefab);
        }

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
            Destroy(gameObject);
        }
    }

    /// <summary>손에 장착됐을 때 오너 클라에서 호출된다. 진행 중 상태가 있으면 게이지를 다시 띄운다.</summary>
    public virtual void OnEquipped() { }
}
