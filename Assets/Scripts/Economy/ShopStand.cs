using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// 상점 진열대 (#182) — Shop 씬에 배치되는 씬 NetworkObject. 품목 하나를 팔고, E로 구매를 요청한다.
/// (GDD 3-2 흐름 7 "아이템 구매", 8-3 추가 아이템, 9-2 자금)
///
/// <b>진열대 자체가 품목의 신원이다.</b> 클라가 보내는 것은 "산다"는 사실뿐이고, 서버는 RPC 수신자인
/// 자기 인스펙터 참조에서 품목과 가격을 읽는다 — 클라가 인덱스·id·가격을 위조할 경로가 아예 없다.
///
/// 구매품은 구매자 인벤토리로 가지 않는다(#370 — 상점은 빈손 대기). 팀 소유 목록(ShopPurchases)에
/// 등록만 하고, 실제 지급은 게임 씬 진입 시 ShopDelivery가 본부 택배 지점에서 한다.
///
/// 표시는 두 갈래다:
///  · <b>상시 가격표</b> — 이름·가격·구매 여부. 진열대에 늘 붙어 있다.
///  · <b>조준 카드</b> — 이름·분류·설명. 조준했을 때만 ShopStandPresenter가 켠다.
///
/// 콜라이더는 반드시 <c>Interactable</c> 레이어에 둘 것 — PlayerInteractor의 조준 마스크가 그 레이어만 본다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(ShopStandView))]
public class ShopStand : NetworkBehaviour, IInteractable
{
    [Header("판매 품목")]
    [Tooltip("소지형 판매 품목의 프리팹. 가격·이름·설명은 여기(ItemBase)에서 읽는다. 설치형이면 비운다")]
    [SerializeField]
    private ItemBase m_itemPrefab;

    [Tooltip("설치형 판매 품목. None이면 이 진열대는 위 프리팹(소지형)을 판다")]
    [SerializeField]
    private EInstallable m_installable = EInstallable.None;

    [Header("설치형 가격 (이름·설명은 ItemTable에서 온다)")]
    [Min(0)]
    [SerializeField]
    private int m_installablePrice;

    // 설치형 이름·설명은 인스펙터가 아니라 ItemTable에서 온다 (#497). 키는 enum 이름으로 만든다 —
    // 소지형이 이미 ItemBase.ItemName으로 Item.Name.* 을 쓰므로 두 종류의 출처가 같아진다.
    // 조준 카드에 둘이 나란히 뜨는 화면이라 여기서 갈라지면 한쪽만 번역되는 상태가 눈에 보인다.
    private const string k_itemTable = "ItemTable";
    private const string k_shopTable = "ShopTable";
    private const string k_nameKeyPrefix = "Item.Name.";
    private const string k_descriptionKeyPrefix = "Item.Description.";

    // 가격표·카드는 같은 오브젝트의 표시 컴포넌트가 그린다 (RequireComponent 보장).
    private ShopStandView m_view;

    // 한 번이라도 샀는가 — 표시 전용이다. 소지형은 중복 구매를 허용하므로 "구매 불가"가 아니라 "산 적 있음".
    // Shop 씬은 라운드마다 재로드돼 진열대가 새로 생기므로, 서버가 스폰 때 구매 목록을 보고 매번 복원한다.
    private readonly NetworkVariable<bool> m_purchased = new NetworkVariable<bool>();

    /// <summary>설치형 진열대인가 — 아니면 소지형(프리팹) 진열대.</summary>
    public bool IsInstallable => m_installable != EInstallable.None;

    /// <summary>판매가. 소지형은 프리팹의 ItemBase.ShopPrice, 설치형은 이 진열대의 값.</summary>
    public int Price => IsInstallable ? m_installablePrice : (m_itemPrefab != null ? m_itemPrefab.ShopPrice : 0);

    private void Awake()
    {
        m_view = GetComponent<ShopStandView>();
    }

    public override void OnNetworkSpawn()
    {
        // 서버만 진실을 채운다(쓰기 권한도 서버뿐) — 클라는 아래 구독으로 받은 값만 그린다.
        if (IsServer)
        {
            ShopPurchases purchases = App.Game.ShopPurchases;
            m_purchased.Value = purchases != null && (IsInstallable
                ? purchases.HasInstallable(m_installable)
                : purchases.HasCarried(m_itemPrefab));
        }

        m_purchased.OnValueChanged += HandlePurchasedChanged;

        // 언어를 바꾸면 표시를 다시 해석한다 — 소지형 이름·설명은 지역화 문자열이라 스폰 때 해석한 값이
        // 그대로 굳으면 진열대만 옛 언어로 남는다. 항목별 StringChanged를 두 번 구독하는 대신
        // 로케일 변경 한 곳에 걸고 RefreshView로 통째로 다시 채운다.
        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        RefreshView();
    }

    public override void OnNetworkDespawn()
    {
        m_purchased.OnValueChanged -= HandlePurchasedChanged;

        // 종료 중에는 설정 에셋을 되살리지 않는다 — HasSettings로 먼저 확인한다.
        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;
    }

    private void HandleLocaleChanged(Locale locale) => RefreshView();

    private void HandlePurchasedChanged(bool previous, bool current) => m_view.SetPurchased(current);

    // 가격표·카드 내용을 채운다 — 스폰 시 1회, 이후 언어가 바뀔 때마다 다시.
    private void RefreshView()
    {
        m_view.SetContent(ResolveName(), ResolveDescription(), IsInstallable, Price);
        m_view.SetPurchased(m_purchased.Value);
    }

    private string ResolveName()
    {
        if (IsInstallable)
            return LocalizedStrings.Get(k_itemTable, k_nameKeyPrefix + m_installable);

        return m_itemPrefab != null && !m_itemPrefab.ItemName.IsEmpty
            ? m_itemPrefab.ItemName.GetLocalizedString()
            : LocalizedStrings.Get(k_shopTable, "Shop.Stand.NoItem");
    }

    private string ResolveDescription()
    {
        if (IsInstallable)
            return LocalizedStrings.Get(k_itemTable, k_descriptionKeyPrefix + m_installable);

        return m_itemPrefab != null && !m_itemPrefab.ItemDescription.IsEmpty
            ? m_itemPrefab.ItemDescription.GetLocalizedString()
            : string.Empty;
    }

    // ---- 상호작용 (E) ----

    // 잔액이 부족해도 눌러서 사유를 볼 수 있어야 하므로 게이팅하지 않는다 — 판정은 전부 서버가 한다.
    public bool CanInteract(GameObject interactor) => true;

    // 조준 안내 (#664). 잔액 부족을 사유로 달지 않는다 — 값만 보려고 누르는 것을 열어 둔
    // 진열대라(위 CanInteract가 늘 true인 이유) 살 수 없다고 미리 회색으로 막아 세울 자리가 아니다.
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.Shop;

    public void Interact(GameObject interactor)
    {
        // Shop 씬은 정식 경로(Title → Lobby → Shop)로만 들어온다 — 세션 없이 단독 Play하면
        // TeamFund·ShopPurchases 자체가 없으므로 오프라인 폴백을 두지 않는다.
        if (!IsSpawned)
        {
            Debug.LogWarning("ShopStand: 세션이 없어 구매할 수 없다 (정식 경로 Title→Lobby→Shop으로 진입할 것)", this);
            return;
        }

        RequestPurchaseRpc();
    }

    // ---- 구매 (서버 권위) ----

    // InvokePermission = Everyone — 진열대는 씬에 놓인 서버 소유 오브젝트라 오너가 없다. 기본값(오너 전용)이면
    // 아무도 구매할 수 없다. (SignalDecoder.RequestBroadcastRpc·Scanner.RequestChargeRpc가 같은 이유로 이렇게 돼 있다)
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestPurchaseRpc(RpcParams rpcParams = default)
    {
        ulong requester = rpcParams.Receive.SenderClientId;

        ShopPurchases purchases = App.Game.ShopPurchases;
        TeamFund fund = App.Game.TeamFund;
        if (purchases == null || fund == null)
        {
            Debug.LogWarning("ShopStand: 상주 홀더(TeamFund/ShopPurchases)를 찾지 못해 구매를 처리할 수 없다", this);
            return;
        }

        if (!IsInstallable && m_itemPrefab == null)
        {
            Debug.LogWarning("ShopStand: 판매 품목이 지정되지 않았다", this);
            return;
        }

        // 설치형은 본부 씬 인스턴스를 켜는 방식이라 두 번 사도 의미가 없다 — 서버가 거부한다.
        // (소지형은 중복 구매 허용 — 산 개수만큼 매 라운드 배달된다)
        if (IsInstallable && purchases.HasInstallable(m_installable))
        {
            ReplyRpc("이미 구매한 장비", EAudioClip.None, RpcTarget.Single(requester, RpcTargetUse.Temp));
            return;
        }

        // 자금 차감은 서버 단일 지점(TeamFund.TrySpend) — 동시 구매 경쟁도 여기서 자연 해소된다.
        // 부족하면 차감 없이 false, 잔액은 0 밑으로 내려가지 않는다 (#104).
        if (!fund.TrySpend(Price))
        {
            ReplyRpc("팀 자금 부족", EAudioClip.None, RpcTarget.Single(requester, RpcTargetUse.Temp));
            return;
        }

        if (IsInstallable)
            purchases.AddInstallable(m_installable);
        else
            purchases.AddCarried(m_itemPrefab);

        m_purchased.Value = true;
        ReplyRpc("구매 완료 — 다음 라운드에 본부로 배달된다", EAudioClip.ShopPurchase, RpcTarget.Single(requester, RpcTargetUse.Temp));
    }

    // 구매 결과는 요청자에게만. 표시는 진열대 자신의 카드가 맡는다 — 카드는 클라마다 로컬 오브젝트라
    // 남의 화면에는 뜨지 않는다. 그래서 별도 HUD 배선이 필요 없다.
    //
    // 소리를 문구와 함께 싣는 이유는 판정이 서버에만 있기 때문이다 — 성공과 거절을 클라가 다시
    // 가리려면 문구를 문자열로 비교해야 하고, 그러면 문구를 고칠 때마다 소리가 조용히 어긋난다.
    // 요청자에게만 가는 RPC라 2D다: 확인음이지 세상에 난 소리가 아니다.
    //
    // <b>거절은 무음이다</b> — 잔액이 부족해도 눌러서 사유를 볼 수 있게 열어 둔 진열대라(CanInteract가
    // 항상 true다) 값만 보려고 누르는 일이 잦고, 그때마다 실패음이 나면 잘못한 것처럼 들린다.
    // 문구는 이미 카드에 뜬다. 그래서 EAudioClip.None을 명시로 넣는다 — 배선을 빠뜨린 게 아니다.
    [Rpc(SendTo.SpecifiedInParams)]
    private void ReplyRpc(string message, EAudioClip sound, RpcParams rpcParams)
    {
        m_view.ShowNotice(message);
        App.Sound?.PlaySfx2D(sound);
    }

    // ---- 조준 카드 토글 (ShopStandPresenter가 호출, 오너 로컬) ----

    /// <summary>조준 카드를 켜고 끈다 — 로컬 플레이어가 이 진열대를 겨냥할 때만 켜진다. (#182)</summary>
    public void SetCardVisible(bool visible) => m_view.ShowCard(visible);
}
