using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 아군 약탈 — 기능 정지된 동료의 소지품과 개인 자금을 빼앗는 서버 권위 허브(GDD 7-5).
/// 세션 상태 없이 요청마다 CanLoot로 다시 검증한다.
/// </summary>
[RequireComponent(typeof(OwnerFeedback))]
public class PlayerLooter : NetworkBehaviour
{
    private OwnerFeedback m_feedback;

    private OwnerFeedback Feedback => this.ResolveCapability(ref m_feedback);

    private const float k_fallbackRange = 3f;

    private PlayerInteractor m_interactor;
    private PlayerIncapacitation m_incapacitation;
    private PlayerLoadout m_loadout;
    private PlayerWallet m_wallet;
    private PlayerInputHandler m_input;

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
            return;

        if (m_input != null)
            m_input.OnLootPerformed += HandleLootPerformed;
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner && m_input != null)
            m_input.OnLootPerformed -= HandleLootPerformed;
    }

    private void HandleLootPerformed()
    {
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return;

        LootableBodyInteractable target = FindLootTarget();
        if (target != null)
            RequestOpenLoot(target.Body);
    }

    private LootableBodyInteractable FindLootTarget()
    {
        GameObject targetObj = m_interactor != null ? m_interactor.CurrentTarget : null;
        if (targetObj == null)
            return null;

        LootableBodyInteractable target =
            targetObj.GetComponentInParent<LootableBodyInteractable>();
        return target != null && target.CanLoot(gameObject) ? target : null;
    }

    /// <summary>약탈 창 열기를 요청한다. 아무것도 옮기지 않고 목록과 자금 금액만 받는다.</summary>
    public void RequestOpenLoot(PlayerLootable victim)
    {
        if (victim == null)
            return;

        if (this.HasServerAuthority())
        {
            ServerOpenLoot(victim);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsNetworkReady(victim))
            return;

        OpenLootRpc(new NetworkObjectReference(victim.NetworkObject));
    }

    /// <summary>대상의 개인 자금 전액을 가져가도록 요청한다.</summary>
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

    /// <summary>소지품 하나를 가져가는 요청 — 약탈 창에서 항목을 고르면 부른다.</summary>
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

    private bool IsNetworkReady(PlayerLootable victim)
    {
        if (victim.NetworkObject != null && victim.NetworkObject.IsSpawned)
            return true;

        Debug.LogWarning($"약탈 요청 무시 — 대상이 네트워크 스폰되지 않음: {victim.name}", this);
        return false;
    }

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

    /// <summary>열기·가져가기 요청이 매번 통과해야 하는 약탈 가능 검증.</summary>
    private bool CanLoot(PlayerLootable victim)
    {
        if (victim == null || victim.gameObject == gameObject)
            return false;
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return false;
        if (!victim.CanBeLooted)
            return false;

        return IsInRange(victim);
    }

    private void ServerOpenLoot(PlayerLootable victim)
    {
        if (!this.HasServerAuthority())
            return;
        if (!CanLoot(victim))
            return;

        int availableFunds = victim.Wallet != null ? victim.Wallet.Balance : 0;

        NotifyLootOpened(victim, availableFunds);
    }

    /// <summary>약탈 검증을 다시 통과하면 대상의 개인 자금을 옮긴다. 서버 전용.</summary>
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

        int taken = victim.Wallet.ServerTransferAllTo(m_wallet);

        if (taken > 0)
            victim.ServerNotifyRobbedFunds(taken);

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

        if (!victimLoadout.Held.Holds(itemObject))
            return;

        if (!IsDetachable(victimLoadout, itemObject))
            return;

        if (m_loadout.Held.Count >= PlayerLoadout.k_maxHeldItems)
        {
            Feedback?.NotifyOwner("약탈 실패 — 소지 슬롯이 꽉 찼다 (먼저 버릴 것)");
            return;
        }

        itemObject.TryGetComponent(out ItemBase item);

        if (item != null)
            item.ServerCancelActiveUse();

        itemObject.ChangeOwnership(OwnerClientId);
        m_loadout.Held.Attach(itemObject);

        victimLoadout.ServerNotifyHeldItemsChanged();
        m_loadout.ServerNotifyHeldItemsChanged();

        victim.ServerNotifyRobbedItem();
        Feedback?.NotifyOwner(
            $"[약탈] 소지품 확보 — {(item != null ? item.name : itemObject.name)} ({victim.name})"
        );
    }

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

        m_candidates.Clear();
        return found;
    }

    /// <summary>약탈 요청이 거부된 사실을 약탈자에게 알린다(현재는 로그만).</summary>
    private void NotifyOwnerLootRejected() =>
        Feedback?.NotifyOwner("약탈 실패 — 대상에 손이 닿지 않는다 (거리·가시선·대상 상태)");

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

    private void ShowLoot(PlayerLootable victim, int availableFunds)
    {
        if (App.UI.Current != null && App.UI.Current.TryGetPanel(out LootPanel panel))
        {
            panel.Open(this, victim, availableFunds);
            return;
        }

        Debug.LogWarning(
            $"[약탈] 약탈 창(LootPanel)을 찾지 못했다 — {victim.name}의 소지품·자금({availableFunds})을 가져갈 수 없다"
        );
    }

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
        if (taken > 0)
            Debug.Log($"[약탈] 개인 자금 강탈 — {victim.name}에게서 {taken}");
        else
            Debug.Log($"[약탈] {victim.name}의 개인 자금은 비어 있다");

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
