using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 아군 약탈 — 기능 정지(Die)된 동료의 소지품과 개인 자금을 빼앗는다. 서버 권위 허브. (#487, GDD 7-5 · 9-2)
///
/// <b>터는 쪽</b>만 맡는다. 당하는 쪽(털릴 수 있는 상태인가·무엇을 들고 있나·피해 알림)은
/// <see cref="PlayerLootable"/>이고, 이 컴포넌트는 소매치기(<see cref="Pickpocket"/>, #303)처럼
/// <b>행위 주체</b>다 — 대상에게서는 목록만 받아 오고, 무엇을 언제 가져갈지는 여기서 정한다.
///
/// <b>세션 상태를 두지 않는다</b> — "누가 누구를 털고 있는가"를 서버가 기억하지 않고, 열기·가져가기
/// 요청마다 <see cref="CanLoot"/>로 처음부터 다시 검증한다(#118 요청/실행 분리). 약탈 창(UI)은 순전히
/// 약탈자 클라의 표시이고, 그 창이 열려 있다는 사실은 서버에서 아무 권한도 되지 않는다 —
/// 창을 띄워 둔 채 대상이 부활하거나 멀어져도 다음 요청이 그냥 거부된다.
///
/// 이전 절차 자체는 소매치기 NPC(<see cref="Pickpocket.ServerStealFrom"/>)가 검증한 순서를 그대로
/// 따른다 — 채널링 끊기 → 소유권 이전 → 부착 → <b>양쪽</b> 오너에게 목록 동기화.
/// 배터리 잔량 같은 아이템 상태는 아이템 NetworkObject에 실려 있어 저절로 따라온다(<see cref="ItemBattery"/>).
/// </summary>
[RequireComponent(typeof(OwnerFeedback))]
public class PlayerLooter : NetworkBehaviour
{
    private OwnerFeedback m_feedback;

    private OwnerFeedback Feedback => this.ResolveCapability(ref m_feedback);

    // 사거리는 조준·윤곽선과 같은 기준 — PlayerInteractor.Range 재사용 (#147 패턴, #184)
    private const float k_fallbackRange = 3f; // 테스트 구성 등 PlayerInteractor가 없을 때

    private PlayerInteractor m_interactor;
    private PlayerIncapacitation m_incapacitation; // 쓰러진 내가 남을 털지 못하게
    private PlayerLoadout m_loadout; // 가져온 것을 받을 내 소지품
    private PlayerWallet m_wallet; // 뺏은 자금이 들어올 내 지갑
    private PlayerInputHandler m_input; // R 입력 구독 (#725)

    // 약탈 가능 소지품 후보 버퍼 — 요청마다 새 List를 만들지 않게 재사용한다 (Pickpocket과 같은 관례).
    // 서버 검증 전용이다. 오너 표시 쪽(ShowLoot)은 자기 목록을 따로 만든다 — 호스트에서는 둘이 같은
    // 피어에서 도는데, 한 버퍼를 나눠 쓰면 표시가 검증 중간 상태를 들여다보게 된다.
    private readonly List<ItemBase> m_candidates = new List<ItemBase>();

    private void Awake()
    {
        m_interactor = GetComponent<PlayerInteractor>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_loadout = GetComponent<PlayerLoadout>();
        m_wallet = GetComponent<PlayerWallet>();
        m_input = GetComponent<PlayerInputHandler>();
    }

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
            return; // 원격 클라는 서버 실행·채널링 피드백만 필요 — 입력은 오너만 구독한다

        if (m_input != null)
            m_input.OnLootPerformed += HandleLootPerformed;
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner && m_input != null)
            m_input.OnLootPerformed -= HandleLootPerformed;
    }

    // ---- 오너 입력 핸들러 (R, #725) ----

    private void HandleLootPerformed()
    {
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return; // 쓰러진 내가 남을 털 수는 없다 — 서버 CanLoot과 같은 기준

        LootableBodyInteractable target = FindLootTarget();
        if (target != null)
            RequestOpenLoot(target.Body);
    }

    // 조준 중인 대상이 약탈 가능한 몸이면 그 컴포넌트를, 아니면 null을 반환한다.
    // (PlayerReviver.FindAllyTarget과 같은 패턴 — #725)
    private LootableBodyInteractable FindLootTarget()
    {
        GameObject targetObj = m_interactor != null ? m_interactor.CurrentTarget : null;
        if (targetObj == null)
            return null;

        LootableBodyInteractable target =
            targetObj.GetComponentInParent<LootableBodyInteractable>();
        return target != null && target.CanLoot(gameObject) ? target : null;
    }

    // ---- 오너 클라 진입점 (R 입력·약탈 창이 호출) ----

    /// <summary>
    /// 약탈 열기 요청 — 쓰러진 동료를 겨냥한 R이 부른다(<see cref="HandleLootPerformed"/>).
    ///
    /// <b>열기는 아무것도 옮기지 않는다.</b> R은 확인용이고, 소지품도 자금도 창에서 눌러 가져간다.
    /// 그래서 시체를 열어 보기만 하고 그냥 떠날 수 있다 — 훔칠지 말지를 내용을 보고 정한다.
    ///
    /// 자금 금액은 서버가 <b>약탈자에게만</b> 실어 보낸다. 잔액 NetworkVariable의 읽기 권한은
    /// 여전히 Owner이므로(#484 — #485/#487의 정보 비대칭) 약탈자 클라가 스스로 읽는 것이 아니라,
    /// 쓰러진 몸에 손이 닿은 사람에게 서버가 한정해서 알려 주는 것이다.
    /// </summary>
    public void RequestOpenLoot(PlayerLootable victim)
    {
        if (victim == null)
            return;

        // 서버(호스트 포함)·오프라인은 로컬 참조로 바로 실행 (PlayerCarrier.RequestCarry 관례)
        if (this.HasServerAuthority())
        {
            ServerOpenLoot(victim);
            return;
        }
        if (!IsOwner)
            return; // 남의 플레이어 오브젝트에서 온 호출 방지
        if (!IsNetworkReady(victim))
            return;

        OpenLootRpc(new NetworkObjectReference(victim.NetworkObject));
    }

    /// <summary>
    /// 개인 자금을 가져가는 요청 — 약탈 창의 자금 칸을 누르면 부른다. (#487)
    ///
    /// <b>전액이다.</b> 부분 이전을 열어 두면 "몇 번 더 털기"가 최적 플레이가 되어, 한 번의 선택이어야
    /// 할 것이 반복 작업이 된다. 버튼을 따로 둔 것은 <b>가져갈지 정하게</b> 하려는 것이지 금액을
    /// 나누려는 것이 아니다.
    /// </summary>
    public void RequestTakeFunds(PlayerLootable victim)
    {
        if (victim == null)
            return;

        if (this.HasServerAuthority())
        {
            ServerTakeFunds(victim);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsNetworkReady(victim))
            return;

        TakeFundsRpc(new NetworkObjectReference(victim.NetworkObject));
    }

    /// <summary>소지품 하나를 가져가는 요청 — 약탈 창에서 항목을 고르면 부른다. (#487)</summary>
    public void RequestTakeItem(PlayerLootable victim, ItemBase item)
    {
        if (victim == null || item == null)
            return;

        NetworkObject itemObject = item.NetworkObject;
        if (itemObject == null)
            return;

        if (this.HasServerAuthority())
        {
            ServerTakeItem(victim, itemObject);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsNetworkReady(victim) || !itemObject.IsSpawned)
            return;

        TakeItemRpc(
            new NetworkObjectReference(victim.NetworkObject),
            new NetworkObjectReference(itemObject)
        );
    }

    // 원격 클라 → 서버로 대상을 넘기려면 스폰돼 있어야 한다(NetworkObjectReference 제약).
    private bool IsNetworkReady(PlayerLootable victim)
    {
        if (victim.NetworkObject != null && victim.NetworkObject.IsSpawned)
            return true;

        Debug.LogWarning($"약탈 요청 무시 — 대상이 네트워크 스폰되지 않음: {victim.name}", this);
        return false;
    }

    // ---- 서버 RPC (오너 → 서버) ----

    [Rpc(SendTo.Server)]
    private void OpenLootRpc(NetworkObjectReference victimRef)
    {
        if (TryResolveVictim(victimRef, out PlayerLootable victim))
            ServerOpenLoot(victim);
    }

    [Rpc(SendTo.Server)]
    private void TakeFundsRpc(NetworkObjectReference victimRef)
    {
        if (TryResolveVictim(victimRef, out PlayerLootable victim))
            ServerTakeFunds(victim);
    }

    [Rpc(SendTo.Server)]
    private void TakeItemRpc(NetworkObjectReference victimRef, NetworkObjectReference itemRef)
    {
        if (
            TryResolveVictim(victimRef, out PlayerLootable victim)
            && itemRef.TryGet(out NetworkObject itemObject)
        )
        {
            ServerTakeItem(victim, itemObject);
        }
    }

    private static bool TryResolveVictim(
        NetworkObjectReference victimRef,
        out PlayerLootable victim
    )
    {
        victim = null;
        return victimRef.TryGet(out NetworkObject victimObject)
            && victimObject.TryGetComponent(out victim);
    }

    // ---- 서버 실행 (권위) ----

    /// <summary>
    /// 공통 관문 — 열기·가져가기가 <b>매번</b> 통과해야 한다. 자금과 아이템이 같은 검증을 쓰는 것이
    /// 중요하다: 둘로 갈라 두면 한쪽만 조건이 밀려도 티가 나지 않는다.
    /// 위조 RPC로 멀쩡한 동료를 털거나 벽 너머로 손을 뻗는 것을 여기서 막는다 (#487 완료 기준 4번).
    /// 클라 조기검증(<see cref="LootableBodyInteractable.CanLoot"/>)과 같은 기준이라
    /// "윤곽선은 뜨는데 눌러도 반응이 없는" 어긋남이 생기지 않는다 (#184).
    /// </summary>
    private bool CanLoot(PlayerLootable victim)
    {
        if (victim == null || victim.gameObject == gameObject)
            return false; // 자기 자신은 못 턴다
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return false; // 쓰러진 사람이 남을 털 수는 없다
        if (!victim.CanBeLooted)
            return false; // 다운·기능 정지만 — 멀쩡한 동료·기절한 동료는 대상이 아니다(#725)

        return IsInRange(victim);
    }

    private void ServerOpenLoot(PlayerLootable victim)
    {
        if (!this.HasServerAuthority())
            return;
        if (!CanLoot(victim))
            return;

        // 열기는 아무것도 옮기지 않는다 — 얼마가 있는지만 약탈자에게 알린다. 실제 이전은 ServerTakeFunds다.
        // 이 금액은 그 순간의 스냅숏이다: 창이 떠 있는 동안 남이 먼저 털어 갈 수 있고, 그때는 눌러도
        // 0이 옮겨진다(§4-3 — 창은 권한이 아니다). 서버 잔액이 언제나 진실이다.
        int availableFunds = victim.Wallet != null ? victim.Wallet.Balance : 0;

        NotifyLootOpened(victim, availableFunds);
    }

    /// <summary>
    /// 개인 자금 이전 — 창의 자금 칸을 눌렀을 때만 돈다. 열기와 <b>같은 관문</b>(<see cref="CanLoot"/>)을
    /// 다시 통과해야 한다: 창을 띄워 둔 채 대상이 부활하거나 멀어졌으면 여기서 거부된다.
    /// </summary>
    private void ServerTakeFunds(PlayerLootable victim)
    {
        if (!this.HasServerAuthority())
            return;
        if (!CanLoot(victim))
        {
            NotifyOwnerLootRejected();
            return;
        }
        if (victim.Wallet == null || m_wallet == null)
            return;

        // 전액 이전 — 발행이 아니라 이전이라 총량이 늘지 않는다 (PlayerWallet.ServerTransferAllTo)
        int taken = victim.Wallet.ServerTransferAllTo(m_wallet);

        if (taken > 0)
            victim.ServerNotifyRobbedFunds(taken);

        // 0이어도 알린다 — 약탈자 창의 자금 칸을 비워 줘야 "눌렀는데 아무 일도 안 났다"가 되지 않는다.
        NotifyFundsTaken(victim, taken);
    }

    private void ServerTakeItem(PlayerLootable victim, NetworkObject itemObject)
    {
        if (!this.HasServerAuthority())
            return;
        if (!CanLoot(victim))
        {
            NotifyOwnerLootRejected();
            return;
        }
        if (itemObject == null || !itemObject.IsSpawned)
            return;

        PlayerLoadout victimLoadout = victim.Loadout;
        if (victimLoadout == null || m_loadout == null)
            return;

        // 그 몸에 실제로 붙어 있는 것만 — 부착 여부가 소지의 진실이다 (PlayerLoadout.DropRpc와 같은 판정)
        if (!victimLoadout.Held.Holds(itemObject))
            return;

        // 묶어 둔 밧줄은 뺄 수 없다 — 줄이 손을 떠나면 묶인 NPC가 주인 없이 남는다 (#369).
        // 버리기·소매치기와 같은 기준을 쓰려고 목록으로 받아 대조한다.
        if (!IsDetachable(victimLoadout, itemObject))
            return;

        // 소지 5칸 제한 (#144/#793) — 꽉 차면 거부한다. 자동 드롭·스왑은 하지 않는다:
        // 약탈자가 의도하지 않은 아이템이 바닥에 떨어지는 편이 못 가져가는 것보다 나쁘다.
        if (m_loadout.Held.Count >= PlayerLoadout.k_maxHeldItems)
        {
            // 화면 표시가 아니라 로그인 이유는 NotifyOwnerLootRejected 주석 참고 (#525와 함께 붙인다)
            Feedback?.NotifyOwner("약탈 실패 — 소지 슬롯이 꽉 찼다 (먼저 버릴 것)");
            return;
        }

        itemObject.TryGetComponent(out ItemBase item);

        // 채널링 중이면 끊는다 — 소유권을 잃은 뒤엔 옛 오너의 취소 RPC가 막혀 배터리가 새고
        // 오완료된다 (버리기·소매치기와 같은 처리)
        if (item != null)
            item.ServerCancelActiveUse();

        itemObject.ChangeOwnership(OwnerClientId);
        m_loadout.Held.Attach(itemObject);

        // 양쪽 오너가 각자 인벤토리를 재구성해야 한다 — 한쪽만 알리면 털린 쪽 화면에 유령 아이템이 남는다
        victimLoadout.ServerNotifyHeldItemsChanged();
        m_loadout.ServerNotifyHeldItemsChanged();

        victim.ServerNotifyRobbedItem();
        Feedback?.NotifyOwner(
            $"[약탈] 소지품 확보 — {(item != null ? item.name : itemObject.name)} ({victim.name})"
        );
    }

    // 손에서 떼어 낼 수 있는 소지품인가 — 기준은 버리기·소매치기와 같다 (PlayerLoadout.CollectDetachableItems).
    private bool IsDetachable(PlayerLoadout victimLoadout, NetworkObject itemObject)
    {
        victimLoadout.CollectDetachableItems(m_candidates);

        bool found = false;
        foreach (ItemBase candidate in m_candidates)
        {
            if (candidate != null && candidate.NetworkObject == itemObject)
            {
                found = true;
                break;
            }
        }

        m_candidates.Clear(); // 파괴된 아이템 참조를 들고 있지 않는다
        return found;
    }

    /// <summary>
    /// 칸을 눌렀는데 <see cref="CanLoot"/>에 걸렸다 — 왜 아무 일도 안 났는지 약탈자에게 알린다.
    ///
    /// <b>조용히 return하면 안 되는 자리다.</b> 창 자동 닫기(4.5m)가 서버 도달 거리(3m)보다 넉넉하고
    /// 서버는 가시선까지 보므로(<see cref="PlayerInteractor.IsWithinReach"/>), 창이 떠 있는데 클릭이
    /// 전부 거부되는 구간이 실제로 존재한다 — 스스로는 못 움직여도 <b>남이 대상을 밧줄로 끌어갈 수
    /// 있다</b>(#365). 자금 0 케이스를 굳이 알리는 것(<see cref="ServerTakeFunds"/>)과 같은 이유다.
    ///
    /// <b>지금은 로그뿐이다.</b> 토스트로 띄우려면 <see cref="ToastFeedback"/>를 프리팹에 붙이고
    /// 그 <c>OnToast</c>를 받아 띄울 UI 채널이 있어야 하며(<see cref="Scanner"/>가 그렇다),
    /// 사유는 <see cref="EItemFeedback"/> 값과 <c>Item.Feedback.*</c> 키가 함께 필요하다 (#525).
    /// 이 클래스에는 아직 그 채널이 없다.
    /// </summary>
    private void NotifyOwnerLootRejected() =>
        Feedback?.NotifyOwner("약탈 실패 — 대상에 손이 닿지 않는다 (거리·가시선·대상 상태)");

    // ---- 약탈자 쪽 결과 (서버 → 오너) ----

    // 서버가 대상을 확인해 준 뒤에야 창이 열린다 — 클라가 혼자 판단해 열면 서버가 거부할 대상 앞에서도
    // 창이 뜬다. (PlayerCarrier의 오너 피드백과 같은 분기)
    private void NotifyLootOpened(PlayerLootable victim, int availableFunds)
    {
        if (IsSpawned && IsServer && !IsOwner)
        {
            LootOpenedRpc(new NetworkObjectReference(victim.NetworkObject), availableFunds);
            return;
        }

        ShowLoot(victim, availableFunds);
    }

    [Rpc(SendTo.Owner)]
    private void LootOpenedRpc(NetworkObjectReference victimRef, int availableFunds)
    {
        if (TryResolveVictim(victimRef, out PlayerLootable victim))
            ShowLoot(victim, availableFunds);
    }

    // 약탈자 오너 로컬 — 창을 연다. 이 시점에는 아무것도 옮기지 않는다.
    private void ShowLoot(PlayerLootable victim, int availableFunds)
    {
        // 창이 곧 유일한 조작 경로다 — 자금까지 창에서 가져가게 된 뒤로는, 창을 못 찾으면 소지품도
        // 자금도 손에 넣을 수 없다. (열기가 자금을 옮기던 시절과 달라진 점)
        if (App.UI.Current != null && App.UI.Current.TryGetPanel(out LootPanel panel))
        {
            panel.Open(this, victim, availableFunds);
            return;
        }

        Debug.LogWarning(
            $"[약탈] 약탈 창(LootPanel)을 찾지 못했다 — {victim.name}의 소지품·자금({availableFunds})을 가져갈 수 없다"
        );
    }

    // 자금 이전 결과 — 창의 자금 칸을 비우고 로그를 남긴다.
    private void NotifyFundsTaken(PlayerLootable victim, int taken)
    {
        if (IsSpawned && IsServer && !IsOwner)
        {
            FundsTakenRpc(new NetworkObjectReference(victim.NetworkObject), taken);
            return;
        }

        ShowFundsTaken(victim, taken);
    }

    [Rpc(SendTo.Owner)]
    private void FundsTakenRpc(NetworkObjectReference victimRef, int taken)
    {
        if (TryResolveVictim(victimRef, out PlayerLootable victim))
            ShowFundsTaken(victim, taken);
    }

    private void ShowFundsTaken(PlayerLootable victim, int taken)
    {
        // 잔액 표시 UI가 없어(GDD 9-2 ⏸) 오간 금액은 아직 로그가 유일한 확인 경로다.
        if (taken > 0)
            Debug.Log($"[약탈] 개인 자금 강탈 — {victim.name}에게서 {taken}");
        else
            Debug.Log($"[약탈] {victim.name}의 개인 자금은 비어 있다");

        // 전액 이전이라 남은 금액은 언제나 0이다 — 남이 먼저 털어 0을 받았어도 결과는 같다.
        // 어느 대상의 결과인지 창이 직접 대조한다(창이 그 사이 다른 시체로 옮겨 갔을 수 있다).
        if (App.UI.Current != null && App.UI.Current.TryGetPanel(out LootPanel panel))
            panel.SetFunds(victim, 0);
    }

    private bool IsInRange(PlayerLootable victim) =>
        PlayerInteractor.IsWithinReach(
            m_interactor,
            victim.transform,
            PlayerInteractor.RangeOf(m_interactor, k_fallbackRange),
            transform.position
        );
}
