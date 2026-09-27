using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 팀 상점 구매 목록 — 세션 내내 유지되는 상주 홀더로, ShopDelivery가 이 목록으로 매 라운드 배달한다.
/// 배달용 컬렉션은 서버 전용이고, 표시용 구매 집계는 NetworkList로 동기화한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class ShopPurchases : NetworkedManagerBase
{
    [Tooltip("집계에 실을 품목 id 체계 — 카탈로그 인덱스로 싣는다 (#840). ShopLineup과 같은 에셋을 잡을 것")]
    [SerializeField]
    private ShopCatalog m_catalog;

    private readonly List<ItemBase> m_carried = new List<ItemBase>();

    private readonly HashSet<EInstallable> m_installables = new HashSet<EInstallable>();

    private readonly NetworkList<PurchaseTally> m_tallies = new NetworkList<PurchaseTally>();

    private ShopSlotSaveEntry[] m_lineup = Array.Empty<ShopSlotSaveEntry>();
    private int m_lineupRound;

    public IReadOnlyList<ItemBase> Carried => m_carried;

    public NetworkList<PurchaseTally> Tallies => m_tallies;

    /// <summary>세션 시작 시 1회 — 이어하기면 저장된 구매 목록을 되살린다.</summary>
    public override void OnNetworkSpawn()
    {
        if (!IsServer)
            return;

        m_tallies.Clear();

        if (m_catalog == null)
            Debug.LogWarning("[상점] 카탈로그가 배선되지 않아 구매 집계를 채울 수 없다 (#840)", this);

        RestoreFromSave();
    }

    private void RestoreFromSave()
    {
        SessionSaveData save = SaveService.Pending;
        if (save == null) return;

        foreach (string id in save.CarriedItems)
        {
            ItemBase prefab = SaveItemLookup.Find(id);
            if (prefab != null)
            {
                m_carried.Add(prefab);
                BumpBought(IndexOf(prefab));
            }
            else
                Debug.LogWarning($"[상점] 세이브의 소지형 '{id}'을(를) 찾지 못해 건너뛴다 — 프리팹 이름이 바뀌었는가?", this);
        }

        foreach (string installableName in save.Installables)
        {
            if (Enum.TryParse(installableName, out EInstallable installable) && installable != EInstallable.None)
            {
                if (m_installables.Add(installable))
                    BumpBought(IndexOf(installable));
            }
            else
                Debug.LogWarning($"[상점] 세이브의 설치형 '{installableName}'을(를) 알 수 없어 건너뛴다", this);
        }

        m_lineup = save.ShopSlots ?? Array.Empty<ShopSlotSaveEntry>();
        m_lineupRound = save.Round;

        Debug.Log($"[상점] 세이브 복원 — 소지형 {m_carried.Count}개, 설치형 {m_installables.Count}종, 진열 {m_lineup.Length}칸({m_lineupRound}라운드)");
    }

    public IReadOnlyCollection<EInstallable> Installables => m_installables;

    public ShopSlotSaveEntry[] Lineup => m_lineup;

    public int LineupRound => m_lineupRound;

    /// <summary>진열이 바뀔 때 ShopLineup(서버)이 밀어 넣는다.</summary>
    public void ServerSetLineup(int round, ShopSlotSaveEntry[] slots)
    {
        if (IsSpawned && !IsServer)
            return;

        m_lineupRound = round;
        m_lineup = slots ?? Array.Empty<ShopSlotSaveEntry>();
    }

    /// <summary>이 설치형을 이미 샀는가 — 중복 구매 거부·표시 복원용. 서버 전용.</summary>
    public bool HasInstallable(EInstallable installable) => m_installables.Contains(installable);

    /// <summary>소지형 구매를 기록한다 — ShopLineup의 구매 RPC(서버)가 자금 차감 성공 후 호출한다.</summary>
    public void AddCarried(ItemBase itemPrefab)
    {
        if (!IsServer)
        {
            Debug.LogWarning("ShopPurchases.AddCarried는 서버에서만", this);
            return;
        }
        if (itemPrefab == null) return;

        m_carried.Add(itemPrefab);
        BumpBought(IndexOf(itemPrefab));
        Debug.Log($"[상점] 소지형 구매 기록 — {itemPrefab.name} (총 {m_carried.Count}개)");
    }

    /// <summary>설치형 구매를 기록한다 — ShopLineup의 구매 RPC(서버)가 자금 차감 성공 후 호출한다.</summary>
    public void AddInstallable(EInstallable installable)
    {
        if (!IsServer)
        {
            Debug.LogWarning("ShopPurchases.AddInstallable은 서버에서만", this);
            return;
        }
        if (installable == EInstallable.None) return;

        if (m_installables.Add(installable))
            BumpBought(IndexOf(installable));
        Debug.Log($"[상점] 설치형 구매 기록 — {installable}");
    }

    /// <summary>소지형 구매 기록 하나를 지운다.</summary>
    public void RemoveCarried(ItemBase itemPrefab)
    {
        if (!IsServer)
        {
            Debug.LogWarning("ShopPurchases.RemoveCarried는 서버에서만", this);
            return;
        }
        if (itemPrefab == null) return;

        if (m_carried.Remove(itemPrefab))
        {
            DropRemaining(IndexOf(itemPrefab));
            Debug.Log($"[상점] 구매품 소실 — {itemPrefab.name} (남은 {m_carried.Count}개)");
        }
    }

    /// <summary>라운드 실패로 판이 끝났을 때 구매 목록을 비운다.</summary>
    public void Clear()
    {
        if (!IsServer)
        {
            Debug.LogWarning("ShopPurchases.Clear는 서버에서만", this);
            return;
        }

        Debug.Log($"[상점] 라운드 실패로 구매 목록 초기화 — 소지형 {m_carried.Count}개, 설치형 {m_installables.Count}종");
        m_carried.Clear();
        m_installables.Clear();
        m_tallies.Clear();

        m_lineup = Array.Empty<ShopSlotSaveEntry>();
        m_lineupRound = 0;
    }

    private int IndexOf(ItemBase itemPrefab) => m_catalog != null ? m_catalog.IndexOf(itemPrefab) : -1;

    private int IndexOf(EInstallable installable) => m_catalog != null ? m_catalog.IndexOf(installable) : -1;

    private void BumpBought(int catalogIndex)
    {
        if (!TryTallyIndex(catalogIndex, out ushort index))
            return;

        int row = FindTally(index);
        if (row < 0)
        {
            m_tallies.Add(new PurchaseTally { CatalogIndex = index, Bought = 1, Remaining = 1 });
            return;
        }

        PurchaseTally tally = m_tallies[row];
        tally.Bought = Step(tally.Bought, 1);
        tally.Remaining = Step(tally.Remaining, 1);
        m_tallies[row] = tally;
    }

    private void DropRemaining(int catalogIndex)
    {
        if (!TryTallyIndex(catalogIndex, out ushort index))
            return;

        int row = FindTally(index);
        if (row < 0)
            return;

        PurchaseTally tally = m_tallies[row];
        tally.Remaining = Step(tally.Remaining, -1);
        m_tallies[row] = tally;
    }

    private bool TryTallyIndex(int catalogIndex, out ushort index)
    {
        index = 0;
        if (catalogIndex < 0 || catalogIndex > ushort.MaxValue)
            return false;

        index = (ushort)catalogIndex;
        return true;
    }

    private int FindTally(ushort catalogIndex)
    {
        for (int i = 0; i < m_tallies.Count; i++)
        {
            if (m_tallies[i].CatalogIndex == catalogIndex)
                return i;
        }

        return -1;
    }

    private static ushort Step(ushort value, int delta) =>
        (ushort)Mathf.Clamp(value + delta, 0, ushort.MaxValue);
}
