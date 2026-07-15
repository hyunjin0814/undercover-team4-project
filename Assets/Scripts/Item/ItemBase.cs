using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 모든 아이템의 공통 기반 클래스.
/// 이름·아이콘·설명 등 공통 데이터와 사용 진입점(Use)을 정의한다.
/// 스캐너·수갑 등 하위 아이템은 이 클래스를 상속해 Use()를 구현한다.
/// 아이템은 독립 NetworkObject 프리팹이므로(#88) NetworkBehaviour를 상속한다 —
/// 배터리 등 상태를 NetworkVariable로 전 클라에 동기화하고, 줍기 시 소유권이 이전된다.
/// </summary>
public abstract class ItemBase : NetworkBehaviour
{
    [Header("아이템 정보")]
    [SerializeField]
    private string m_itemName;

    [SerializeField]
    private Sprite m_itemIcon;

    [SerializeField]
    [TextArea]
    private string m_itemDescription;

    [Header("1인칭 표시")]
    [Tooltip("장착 시 1인칭 손에 표시할 모델 프리팹. 비우면 손만 표시된다 (#45)")]
    [SerializeField]
    private GameObject m_heldModelPrefab;

    [Header("조준 피드백")]
    [Tooltip("이 아이템으로 사용 가능한 대상을 조준했을 때의 윤곽선 색 (#184)")]
    [SerializeField]
    private Color m_targetOutlineColor = new Color(1f, 0.85f, 0.2f);

    /// <summary>인벤토리·UI에 표시되는 아이템 이름.</summary>
    public string ItemName => m_itemName;

    /// <summary>인벤토리·UI에 표시되는 아이템 아이콘.</summary>
    public Sprite ItemIcon => m_itemIcon;

    /// <summary>인벤토리·UI에 표시되는 아이템 설명.</summary>
    public string ItemDescription => m_itemDescription;

    /// <summary>장착 시 1인칭 손에 들리는 모델 프리팹. 없으면 null — PlayerHandView가 표시를 생략한다. (#45)</summary>
    public GameObject HeldModelPrefab => m_heldModelPrefab;

    /// <summary>이 아이템으로 사용 가능한 대상을 조준 중일 때의 윤곽선 색. (#184)</summary>
    public Color TargetOutlineColor => m_targetOutlineColor;

    /// <summary>
    /// 이 아이템을 지금 저 대상에 사용할 수 있는지 — 조준 피드백(윤곽선) 판정용. (#184)
    /// Use()의 조기 검증과 같은 기준을 유지해야 "윤곽선이 떴는데 사용은 안 됨"이 안 생긴다.
    /// 기본값 false — 대상 지정 사용이 없는 아이템은 재정의하지 않는다.
    /// 매 프레임 호출되므로(InteractionFeedback) 무거운 연산은 피할 것.
    /// </summary>
    public virtual bool CanTarget(GameObject aimTarget) => false;

    /// <summary>
    /// 현재 아이템을 사용할 수 있는지 여부.
    /// 기본값은 true이며, 하위 클래스가 사용 조건을 재정의한다.
    /// (예: 스캐너는 배터리 잔량이 있을 때만 true)
    /// UI 표시(장착 아이콘 활성/비활성 등)에 참고할 수 있으나, 사용 가능 여부의
    /// 최종 판정은 Use() 구현부가 스스로 수행한다 — 아래 Use() 계약 참고.
    /// </summary>
    public virtual bool CanUse() => true;

    /// <summary>
    /// 아이템 사용 진입점. 하위 클래스가 구체 동작을 구현한다.
    /// (예: 스캐너 3초 채널링 후 스캔 정보 로그)
    /// 계약: 호출부(PlayerItemUser)는 CanUse()로 게이트하지 않고 이 메서드를 호출한다.
    /// 따라서 구현부는 진입 시 스스로 CanUse()를 확인하고, 사용 불가면 사유를
    /// 로그로 알린 뒤 반환해야 한다. (이래야 사용 불가 피드백을 아이템이 낼 수 있다)
    /// </summary>
    /// <param name="target">
    /// 사용 대상 — PlayerInteractor가 겨냥한 오브젝트. 겨냥한 것이 없으면 null.
    /// 하위 아이템이 이 대상에서 필요한 컴포넌트를 조회한다 (Scanner→CitizenProfile #34, Handcuffs→NpcController #35).
    /// </param>
    // 네트워크 전환 패턴: 오너 입력 → 클라에서 대상 조기 검증 → ServerRpc 요청 → 서버가 실제 효과 실행/검증 후 동기화.
    // 각 하위 구현(Scanner, Handcuffs 등)이 이 패턴을 직접 담당한다 (#55).
    public abstract void Use(GameObject target);

    /// <summary>
    /// 진행 중인 사용(채널링)을 중단한다. 좌클릭을 떼면 PlayerItemUser가 호출한다 (#91).
    /// 채널링이 없는 즉발 아이템은 기본 구현(무동작)을 그대로 쓴다.
    /// </summary>
    // TODO: 네트워크 테스트 시 취소도 서버 권위로 (오너 뗌 입력 → CancelUseServerRpc → 서버가 채널링 중단)
    public virtual void CancelUse() { }
}
