using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 상점 진열 추첨(라운드당 1회)과 주문창 주문의 서버 권위 판정.
/// 진열은 세이브에 실려 같은 라운드면 복원된다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class ShopLineup : NetworkBehaviour
{
    public struct Slot : INetworkSerializable, IEquatable<Slot>
    {
        public int EntryIndex;
        public EShopSlotStatus Status;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer)
            where T : IReaderWriter
        {
            serializer.SerializeValue(ref EntryIndex);
            serializer.SerializeValue(ref Status);
        }

        public bool Equals(Slot other) => EntryIndex == other.EntryIndex && Status == other.Status;
    }

    [SerializeField]
    private ShopCatalog m_catalog;

    [Tooltip("주문창에 뿌릴 칸 수. 소모형·랜덤 칸의 합이 모자라면 남는 칸은 빈 칸이 된다")]
    [Min(0)]
    [SerializeField]
    private int m_slotCount = 7;

    private readonly NetworkList<Slot> m_slots = new NetworkList<Slot>();

    public int SlotCount => IsSpawned ? m_slots.Count : 0;

    public event Action OnChanged;

    public event Action<EShopReply> OnPurchaseReply;

    /// <summary>칸이 파는 품목 — 빈 칸이거나 범위 밖이면 null.</summary>
    public ShopCatalog.Entry GetEntry(int slot) =>
        m_catalog != null && TryGetSlot(slot, out Slot value)
            ? m_catalog.Get(value.EntryIndex)
            : null;

    /// <summary>칸의 판매 상태 — 범위 밖이면 Available.</summary>
    public EShopSlotStatus GetStatus(int slot) =>
        TryGetSlot(slot, out Slot value) ? value.Status : EShopSlotStatus.Available;

    public override void OnNetworkSpawn()
    {
        m_slots.OnListChanged += HandleSlotsChanged;

        if (IsServer)
        {
            m_slots.Clear();
            AssignLineupAsync().Forget();
        }

        OnChanged?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        m_slots.OnListChanged -= HandleSlotsChanged;
    }

    private void HandleSlotsChanged(NetworkListEvent<Slot> _) => OnChanged?.Invoke();

    private bool TryGetSlot(int slot, out Slot value)
    {
        if (IsSpawned && slot >= 0 && slot < m_slots.Count)
        {
            value = m_slots[slot];
            return true;
        }

        value = default;
        return false;
    }

    private async UniTaskVoid AssignLineupAsync()
    {
        await UniTask.NextFrame(this.GetCancellationTokenOnDestroy());

        if (TryRestoreLineup())
            return;

        AssignLineup();
    }

    private void AssignLineup()
    {
        if (m_catalog == null)
        {
            Debug.LogWarning("ShopLineup: 카탈로그가 배선되지 않았다", this);
            return;
        }

        int slotCount = m_slotCount;
        if (slotCount <= 0)
            return;

        List<int> staples = new List<int>();
        List<int> others = new List<int>();
        for (int i = 0; i < m_catalog.Count; i++)
        {
            ShopCatalog.Entry entry = m_catalog.Get(i);
            if (entry == null || !entry.IsValid)
                continue;

            (entry.IsStaple ? staples : others).Add(i);
        }

        int stapleSlots = Mathf.Clamp(m_catalog.StapleSlots, 0, slotCount);
        int randomSlots = Mathf.Clamp(m_catalog.RandomSlots, 0, slotCount - stapleSlots);

        if (staples.Count == 0)
        {
            randomSlots = Mathf.Min(slotCount, randomSlots + stapleSlots);
            stapleSlots = 0;
        }
        else if (others.Count == 0)
        {
            stapleSlots = Mathf.Min(slotCount, stapleSlots + randomSlots);
            randomSlots = 0;
        }
        else if (stapleSlots + randomSlots < slotCount)
        {
            randomSlots = slotCount - stapleSlots;
        }

        List<int> assignment = new List<int>(slotCount);
        assignment.AddRange(PickStapleSlots(staples, stapleSlots));
        assignment.AddRange(PickRandomSlots(others, randomSlots));
        while (assignment.Count < slotCount)
            assignment.Add(-1);

        Shuffle(assignment);

        ShopPurchases purchases = App.Game.ShopPurchases;
        m_slots.Clear();
        for (int slot = 0; slot < slotCount; slot++)
            m_slots.Add(MakeSlot(assignment[slot], purchases));

        Debug.Log(
            $"[상점] 진열 추첨 — 소모형 {stapleSlots}칸, 랜덤 {randomSlots}칸, 빈 칸 {slotCount - stapleSlots - randomSlots}"
        );

        PushSnapshot();
        SaveService.SaveAsync().Forget();
    }

    private int CurrentRound =>
        App.Game.RoundProgress != null ? App.Game.RoundProgress.Current : RoundProgress.k_firstRound;

    private bool TryRestoreLineup()
    {
        ShopPurchases purchases = App.Game.ShopPurchases;
        if (m_catalog == null || purchases == null)
            return false;

        ShopSlotSaveEntry[] saved = purchases.Lineup;
        if (saved == null || saved.Length == 0 || purchases.LineupRound != CurrentRound)
            return false;

        for (int i = 0; i < saved.Length; i++)
        {
            ShopSlotSaveEntry e = saved[i];
            int index = string.IsNullOrEmpty(e.Id) ? -1 : IndexOfId(e.Id, e.Installable);

            if (index < 0 && !string.IsNullOrEmpty(e.Id))
                Debug.LogWarning($"[상점] 세이브의 진열 품목 '{e.Id}'을(를) 카탈로그에서 찾지 못해 빈 칸으로 둔다", this);

            if (!Enum.TryParse(e.Status, out EShopSlotStatus status))
                status = EShopSlotStatus.Available;

            ShopCatalog.Entry restored = m_catalog.Get(index);
            if (restored != null && restored.IsInstallable && purchases.HasInstallable(restored.Installable))
                status = EShopSlotStatus.Owned;

            m_slots.Add(new Slot { EntryIndex = index, Status = status });
        }

        Debug.Log($"[상점] 진열 복원 — {saved.Length}칸 ({CurrentRound}라운드)");
        return true;
    }

    private void PushSnapshot()
    {
        ShopPurchases purchases = App.Game.ShopPurchases;
        if (purchases == null || m_catalog == null)
            return;

        var snapshot = new ShopSlotSaveEntry[m_slots.Count];
        for (int i = 0; i < m_slots.Count; i++)
        {
            Slot slot = m_slots[i];
            ShopCatalog.Entry entry = m_catalog.Get(slot.EntryIndex);
            snapshot[i] = new ShopSlotSaveEntry
            {
                Id = IdOf(entry),
                Installable = entry != null && entry.IsInstallable,
                Status = slot.Status.ToString(),
            };
        }

        purchases.ServerSetLineup(CurrentRound, snapshot);
    }

    private static string IdOf(ShopCatalog.Entry entry)
    {
        if (entry == null)
            return string.Empty;

        return entry.IsInstallable ? entry.Installable.ToString() : SaveItemLookup.GetId(entry.ItemPrefab);
    }

    private int IndexOfId(string id, bool installable)
    {
        for (int i = 0; i < m_catalog.Count; i++)
        {
            ShopCatalog.Entry entry = m_catalog.Get(i);
            if (entry == null || entry.IsInstallable != installable)
                continue;
            if (IdOf(entry) == id)
                return i;
        }

        return -1;
    }

    private Slot MakeSlot(int entryIndex, ShopPurchases purchases)
    {
        ShopCatalog.Entry entry = m_catalog.Get(entryIndex);
        bool owned =
            entry != null
            && entry.IsInstallable
            && purchases != null
            && purchases.HasInstallable(entry.Installable);

        return new Slot
        {
            EntryIndex = entryIndex,
            Status = owned ? EShopSlotStatus.Owned : EShopSlotStatus.Available,
        };
    }

    private static List<int> PickStapleSlots(List<int> staples, int count)
    {
        List<int> result = new List<int>(count);
        if (staples.Count == 0 || count <= 0)
            return result;

        List<int> pool = new List<int>(staples);
        Shuffle(pool);
        for (int i = 0; i < pool.Count && result.Count < count; i++)
            result.Add(pool[i]);

        while (result.Count < count)
            result.Add(staples[UnityEngine.Random.Range(0, staples.Count)]);

        return result;
    }

    private List<int> PickRandomSlots(List<int> others, int count)
    {
        List<int> result = new List<int>(count);
        if (others.Count == 0 || count <= 0)
            return result;

        List<int> pool = new List<int>(others);
        for (int i = 0; i < count && pool.Count > 0; i++)
        {
            int pickAt = UnityEngine.Random.Range(0, pool.Count);
            int index = pool[pickAt];
            result.Add(index);

            if (m_catalog.Get(index).IsInstallable)
                pool.RemoveAt(pickAt);
        }

        return result;
    }

    private static void Shuffle(List<int> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>주문창이 칸의 주문 버튼을 눌렀을 때 부른다.</summary>
    public void RequestPurchase(int slot)
    {
        if (!IsSpawned)
        {
            Debug.LogWarning(
                "ShopLineup: 세션이 없어 구매할 수 없다 (정식 경로 Title→Lobby→Shop으로 진입할 것)",
                this
            );
            return;
        }

        RequestPurchaseRpc(slot);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestPurchaseRpc(int slot, RpcParams rpcParams = default)
    {
        ulong requester = rpcParams.Receive.SenderClientId;

        ShopPurchases purchases = App.Game.ShopPurchases;
        TeamFund fund = App.Game.TeamFund;
        if (purchases == null || fund == null)
        {
            Debug.LogWarning(
                "ShopLineup: 상주 홀더(TeamFund/ShopPurchases)를 찾지 못해 구매를 처리할 수 없다",
                this
            );
            return;
        }

        if (!TryGetSlot(slot, out Slot value))
        {
            Debug.LogWarning($"ShopLineup: 범위 밖 칸({slot})에 구매 요청이 들어왔다", this);
            return;
        }

        ShopCatalog.Entry entry = m_catalog != null ? m_catalog.Get(value.EntryIndex) : null;
        if (entry == null)
        {
            Debug.LogWarning("ShopLineup: 빈 칸에 구매 요청이 들어왔다", this);
            return;
        }

        if (entry.IsInstallable && purchases.HasInstallable(entry.Installable))
        {
            ReplyRpc(
                EShopReply.AlreadyOwned,
                EAudioClip.None,
                RpcTarget.Single(requester, RpcTargetUse.Temp)
            );
            return;
        }

        if (value.Status == EShopSlotStatus.SoldOut)
        {
            ReplyRpc(
                EShopReply.SoldOut,
                EAudioClip.None,
                RpcTarget.Single(requester, RpcTargetUse.Temp)
            );
            return;
        }

        if (!fund.TrySpend(entry.Price))
        {
            ReplyRpc(
                EShopReply.InsufficientFunds,
                EAudioClip.None,
                RpcTarget.Single(requester, RpcTargetUse.Temp)
            );
            return;
        }

        if (entry.IsInstallable)
            purchases.AddInstallable(entry.Installable);
        else
            purchases.AddCarried(entry.ItemPrefab);

        value.Status = EShopSlotStatus.SoldOut;
        m_slots[slot] = value;

        PushSnapshot();
        SaveService.SaveAsync().Forget();

        ReplyRpc(
            EShopReply.OrderPlaced,
            EAudioClip.ShopPurchase,
            RpcTarget.Single(requester, RpcTargetUse.Temp)
        );
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void ReplyRpc(EShopReply reply, EAudioClip sound, RpcParams rpcParams)
    {
        OnPurchaseReply?.Invoke(reply);
        App.Sound?.PlaySfx2D(sound);
    }
}
