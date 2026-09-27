using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.CloudSave;
using Unity.Services.CloudSave.Models;
using UnityEngine;

/// <summary>
/// 판 진행 상황을 UGS Cloud Save의 키 하나로 저장·로드하는 정적 서비스(호스트 계정 기준).
/// 이어하기/새로 시작은 세션 생성 전에 고르고, 라운드 성공·상점 출동 시 저장하며 실패 시 삭제한다.
/// </summary>
public static class SaveService
{
    private const string k_key = "session_progress";

    private static SessionSaveData s_known;

    public static SessionSaveData Pending { get; private set; }

    public static int SavedRound => s_known?.Round ?? 0;

    private static readonly HashSet<string> s_restoredWallets = new HashSet<string>();

    /// <summary>클라우드에서 세이브를 읽어 둔다 — 타이틀에서 '이어하기' 노출을 정하기 위해. 읽기만 하고 적용하지는 않는다.</summary>
    public static async UniTask<bool> RefreshAsync()
    {
        s_known = null;

        if (!IsReady)
            return false;

        try
        {
            Dictionary<string, Item> loaded = await CloudSaveService.Instance.Data.Player.LoadAsync(
                new HashSet<string> { k_key }
            );

            if (!loaded.TryGetValue(k_key, out Item item))
            {
                Debug.Log("[세이브] 저장된 판이 없다 — 새 판만 가능");
                return false;
            }

            var data = JsonUtility.FromJson<SessionSaveData>(item.Value.GetAs<string>());

            if (data == null || data.Version != SessionSaveData.k_version)
            {
                Debug.LogWarning(
                    $"[세이브] 포맷 버전이 달라 무시한다 — 저장 {data?.Version}, 현재 {SessionSaveData.k_version}"
                );
                return false;
            }

            s_known = data;
            Debug.Log($"[세이브] 불러옴 — {data.Round}라운드, 팀 자금 {data.TeamFund}, 지갑 {data.Players.Length}명");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[세이브] 조회 실패(새 판으로 진행): {ex.Message}");
            return false;
        }
    }

    /// <summary>불러온 세이브로 다음 세션을 시작한다 — 세션 생성 전에 부를 것.</summary>
    public static void UseSave()
    {
        s_restoredWallets.Clear();
        Pending = s_known;

        if (Pending == null)
            Debug.LogWarning("[세이브] 이어할 세이브가 없어 새 판으로 시작한다");
    }

    /// <summary>세이브를 적용하지 않고 새 판으로 시작한다 — 세션 생성 전에 부를 것.</summary>
    public static void StartFresh()
    {
        s_restoredWallets.Clear();
        Pending = null;
    }

    private static bool s_writing;
    private static bool s_writeQueued;

    public static async UniTask SaveAsync()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening && !nm.IsServer)
        {
            Debug.LogWarning("[세이브] 저장은 서버(호스트)에서만");
            return;
        }

        if (!IsReady)
            return;

        if (s_writing)
        {
            s_writeQueued = true;
            return;
        }

        s_writing = true;
        try
        {
            do
            {
                s_writeQueued = false;
                await WriteAsync(Capture());
            } while (s_writeQueued);
        }
        finally
        {
            s_writing = false;
        }
    }

    private static async UniTask WriteAsync(SessionSaveData data)
    {
        try
        {
            await CloudSaveService.Instance.Data.Player.SaveAsync(
                new Dictionary<string, object> { { k_key, JsonUtility.ToJson(data) } }
            );

            s_known = data;
            Debug.Log($"[세이브] 저장 완료 — {data.Round}라운드, 팀 자금 {data.TeamFund}, 지갑 {data.Players.Length}명");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[세이브] 저장 실패(무시하고 진행): {ex.Message}");
        }
    }

    /// <summary>세이브를 지운다 — 라운드 실패로 판이 끝났을 때. 이어할 판이 없어졌다는 뜻이다.</summary>
    public static async UniTask DeleteAsync()
    {
        s_known = null;
        Pending = null;
        s_restoredWallets.Clear();

        if (!IsReady)
            return;

        try
        {
            await CloudSaveService.Instance.Data.Player.DeleteAsync(k_key);
            Debug.Log("[세이브] 라운드 실패 — 저장된 판을 지웠다");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[세이브] 삭제 실패(무시): {ex.Message}");
        }
    }

    /// <summary>이 PlayerId의 저장된 지갑 잔액을 한 번만 꺼낸다.</summary>
    public static bool TryTakeWalletBalance(string playerId, out int balance)
    {
        balance = 0;

        if (Pending?.Players == null || string.IsNullOrEmpty(playerId))
            return false;

        if (!s_restoredWallets.Add(playerId))
            return false;

        foreach (PlayerSaveEntry entry in Pending.Players)
        {
            if (entry.PlayerId != playerId)
                continue;

            balance = entry.Balance;
            return true;
        }

        return false;
    }

    private static bool IsReady => App.Net.Auth != null && App.Net.Auth.IsSignedIn;

    private static SessionSaveData Capture()
    {
        RoundProgress progress = App.Game.RoundProgress;
        TeamFund fund = App.Game.TeamFund;
        MapSelection maps = App.Game.MapSelection;

        var data = new SessionSaveData
        {
            Round = progress != null ? progress.Current : RoundProgress.k_firstRound,
            TeamFund = fund != null ? fund.Balance : 0,
            MapIndex = maps != null ? maps.SelectedIndex : 0,
            Players = CapturePlayers(),
        };

        ShopPurchases purchases = App.Game.ShopPurchases;
        if (purchases == null)
            return data;

        var carried = new List<string>(purchases.Carried.Count);
        foreach (ItemBase item in purchases.Carried)
        {
            string id = SaveItemLookup.GetId(item);
            if (!string.IsNullOrEmpty(id))
                carried.Add(id);
        }
        data.CarriedItems = carried.ToArray();

        var installables = new List<string>(purchases.Installables.Count);
        foreach (EInstallable installable in purchases.Installables)
            installables.Add(installable.ToString());
        data.Installables = installables.ToArray();

        if (purchases.LineupRound == data.Round)
            data.ShopSlots = purchases.Lineup;

        return data;
    }

    private static PlayerSaveEntry[] CapturePlayers()
    {
        var byPlayerId = new Dictionary<string, int>();

        if (s_known?.Players != null)
        {
            foreach (PlayerSaveEntry entry in s_known.Players)
            {
                if (!string.IsNullOrEmpty(entry.PlayerId))
                    byPlayerId[entry.PlayerId] = entry.Balance;
            }
        }

        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsServer)
        {
            foreach (NetworkClient client in nm.ConnectedClientsList)
            {
                PlayerWallet wallet = client.PlayerObject != null
                    ? client.PlayerObject.GetComponent<PlayerWallet>()
                    : null;

                if (wallet == null || string.IsNullOrEmpty(wallet.OwnerPlayerId))
                    continue;

                byPlayerId[wallet.OwnerPlayerId] = wallet.Balance;
            }
        }

        var result = new PlayerSaveEntry[byPlayerId.Count];
        int index = 0;
        foreach (KeyValuePair<string, int> pair in byPlayerId)
            result[index++] = new PlayerSaveEntry { PlayerId = pair.Key, Balance = pair.Value };

        return result;
    }

#if UNITY_EDITOR
    public static SessionSaveData DevKnown =>
        s_known == null ? null : JsonUtility.FromJson<SessionSaveData>(JsonUtility.ToJson(s_known));

    /// <summary>[개발 도구] 손으로 만든 세이브를 클라우드에 덮어쓴다.</summary>
    public static async UniTask DevOverwriteAsync(SessionSaveData data)
    {
        if (data == null || !IsReady)
        {
            Debug.LogWarning("[세이브] 개발용 덮어쓰기 불가 — 로그인된 플레이 모드에서만 된다");
            return;
        }

        await WriteAsync(data);
    }
#endif

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        s_known = null;
        Pending = null;
        s_restoredWallets.Clear();
    }
}
