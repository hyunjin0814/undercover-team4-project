using System;
using UnityEngine;

[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerItemUser : MonoBehaviour
{
    [Header("장착 아이템")]
    [SerializeField] private ItemBase m_equippedItem;

    private PlayerInputHandler m_inputHandler;
    private PlayerInteractor m_interactor;
    private PlayerIncapacitation m_incapacitation; // 다운(무력화) 중 아이템 사용 차단용 (#105)
    private PlayerEscorter m_escorter; // 밧줄 끌기 중 아이템 사용 잠금용 (#269)

    /// <summary>현재 장착 중인 아이템. 없으면 null. (#45 — PlayerHandView가 초기 표시에 사용)</summary>
    // 파괴된 아이템은 null로 내보낸다 — 장착 중 디스폰(라운드 종료 회수, #370) 후 장착 해제가 도착하기까지
    // 참조가 한 프레임 남는데, `is`/`as` 타입 검사는 Unity 가짜 null을 못 걸러 그대로 통과시킨다
    // (InteractionFeedback의 `equipped is IAimedWeapon`). 사용처마다 가드 두는 대신 여기서 한 번 막는다.
    public ItemBase EquippedItem => m_equippedItem != null ? m_equippedItem : null;

    /// <summary>장착 아이템 변경 이벤트 — 실제로 값이 바뀔 때만 발행. 1인칭 손 표시(#45)·UI 등이 구독한다.</summary>
    public event Action<ItemBase> OnEquippedItemChanged;

    private void Awake()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
        // 사용 시점에 겨냥 중인 대상을 아이템에 넘기기 위한 참조 (#33).
        // 테스트 구성 등 인터랙터가 없으면 null — 이때는 대상 없이(null) 사용된다.
        m_interactor = GetComponent<PlayerInteractor>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_escorter = GetComponent<PlayerEscorter>();
    }

    private void OnEnable()
    {
        m_inputHandler.OnUseItemStarted += HandleUseItem;
        m_inputHandler.OnUseItemCanceled += HandleCancelItem;
    }

    private void OnDisable()
    {
        m_inputHandler.OnUseItemStarted -= HandleUseItem;
        m_inputHandler.OnUseItemCanceled -= HandleCancelItem;
    }

    public void SetEquippedItem(ItemBase item)
    {
        if (m_equippedItem == item)
        {
            return;
        }

        // 장착을 바꾸면 내려놓는 아이템이 진행 중이던 채널링(예: 스캐너 스캔)을 취소한다.
        // 안 하면 채널링이 그대로 돌아 완료된다 — 좌클릭 뗌 취소(HandleCancelItem)는 '지금 든 아이템'
        // 에게만 가므로, 아이템을 바꾼 뒤엔 원래 아이템에 취소가 닿지 않기 때문. (거리이탈만 keepAlive로 취소됨)
        // CancelUse는 채널링 중이 아니면 무동작이라 항상 호출해도 안전하다. Unity 파괴 참조 대비 != null 가드.
        if (m_equippedItem != null)
        {
            m_equippedItem.CancelUse();
        }

        m_equippedItem = item;
        OnEquippedItemChanged?.Invoke(item);
    }

    private void HandleUseItem()
    {
        // 커서가 풀려 있으면 좌클릭은 UI 것이다 — 인벤토리 편집(Tab)은 슬롯을 좌클릭으로 끌기 때문에
        // 그 클릭이 아이템 사용까지 때린다. 편집 모드는 WASD를 감지해 닫혀야 해서 입력 정지
        // (SetSuspended)를 쓸 수 없다 — 커서 상태로 게이트한다. 뗌(HandleCancelItem)은 막지 않는다:
        // 채널링 도중 커서가 풀리면 취소가 닿아야 한다. (#352)
        if (CursorLock.IsUnlocked)
        {
            return;
        }

        // 다운(무력화) 중에는 아이템 사용 불가 (#105)
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
        {
            return;
        }

        // 끌기 중 아이템 사용 차단은 없다 (#390). 끌면서 두 번째 밧줄을 쓸 수 있어야 하는데, 그 예외를
        // 밧줄에만 두면 GDD 8-2의 "손이 묶인다"가 반쪽이 되어 팀 결정으로 통째로 걷었다.
        // 다중 끌기의 대가는 슬롯 경쟁(밧줄로 3칸을 채우면 들 것이 없다)과 무게 페널티(#398)가 진다.

        if (m_equippedItem == null)
        {
            return; // 빈손 좌클릭은 무동작 — 풀어주기는 밧줄을 든 좌클릭으로 이관됐다 (#290 → #369)
        }

        // CanUse() 게이트는 각 아이템의 Use() 내부에서 수행한다 — 사용 불가 사유
        // (배터리 부족 등) 피드백을 아이템이 직접 낼 수 있게 하기 위함. (#34/#35)
        // 겨냥한 대상을 함께 넘긴다 — 각 아이템이 대상에서 필요한 정보를 조회한다.
        GameObject target = m_interactor != null ? m_interactor.CurrentTarget : null;
        m_equippedItem.Use(target);
    }

    /// <summary>
    /// 진행 중인 채널링을 취소한다 — 좌클릭 뗌(<see cref="HandleCancelItem"/>) 외에,
    /// 좌클릭을 누른 채 커서가 풀리는 경로(인벤토리 편집 Tab)도 이걸 부른다. (#352)
    /// </summary>
    public void CancelUse()
    {
        // 채널링 중이 아니면 CancelUse는 무동작이라 항상 호출해도 안전하다.
        // CanUse() 체크 금지 — 채널링 중엔 false라서 취소가 막힌다 (#91)
        // ?. 대신 != null — ?.는 Unity 가짜 null을 못 걸러 파괴된 아이템에 호출이 들어간다 (#370, SetEquippedItem과 동일 관례)
        if (m_equippedItem != null)
        {
            m_equippedItem.CancelUse();
        }
    }

    private void HandleCancelItem() => CancelUse();
}
