using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Services.CloudSave;
using Unity.Services.CloudSave.Models;
using UnityEngine;

/// <summary>
/// 로봇 색·치장을 계정 Cloud Save에 저장·복원하고 PlayerPrefs 캐시를 관리한다.
/// 로그인·로그아웃 시 계정 단위 설정 자리 갈아타기도 여기서 한다.
/// </summary>
public static class CosmeticsSaveService
{
    private const string k_key = "player_cosmetics";

    private const float k_debounceSeconds = 1f;

    private static bool s_flushQueued;
    private static bool s_hooked;
    private static bool s_applying;

    private static bool IsReady => App.Net.Auth != null && App.Net.Auth.IsSignedIn;

    /// <summary>로그인 직후 캐시 → 클라우드 순으로 계정 치장을 복원한다. 클라우드에 없으면 현재 값을 올린다.</summary>
    public static async UniTask RestoreAsync()
    {
        if (!IsReady)
            return;

        Hook();

        Apply(() =>
        {
            GameSettings.UseAccount(App.Net.Auth.PlayerId);
            CosmeticLoadout.UseAccount(App.Net.Auth.PlayerId);
            CosmeticInventory.UseAccount(App.Net.Auth.PlayerId);
        });

        CosmeticsSaveData data = await ReadAsync();
        if (data == null)
        {
            await WriteAsync(Capture());
            return;
        }

        Apply(() =>
        {
            CosmeticLoadout.ApplyPlayerColors(data.Colors);
            CosmeticLoadout.ApplyAccessories(data.Accessories);

            CosmeticInventory.Apply(data.Owned, data.Tokens);
            CosmeticLoadout.ApplyCrosshairSettings(data.Version >= CosmeticsSaveData.k_version ? data.Crosshair : null);
        });
    }

    private static void Apply(Action apply)
    {
        s_applying = true;
        try
        {
            apply();
        }
        finally
        {
            s_applying = false;
        }
    }

    /// <summary>계정이 바뀌면 캐시 기준을 되돌린다 — 다음 로그인이 자기 값을 다시 불러온다.</summary>
    public static void OnSignedOut() =>
        Apply(() =>
        {
            GameSettings.UseAccount(null);
            CosmeticLoadout.UseAccount(null);
            CosmeticInventory.UseAccount(null);
        });

    private static void Hook()
    {
        if (s_hooked)
            return;

        s_hooked = true;
        CosmeticLoadout.OnPlayerColorChanged += HandleColorChanged;
        CosmeticLoadout.OnAccessoryChanged += HandleAccessoryChanged;
        CosmeticLoadout.OnCrosshairSettingsChanged += HandleCrosshairChanged;
        CosmeticInventory.OnOwnedChanged += HandleInventoryChanged;
        CosmeticInventory.OnTokensChanged += HandleInventoryChanged;
    }

    private static void HandleColorChanged(EBodyPart part)
    {
        if (!s_applying)
            QueueSave();
    }

    private static void HandleAccessoryChanged(EAccessorySlot slot)
    {
        if (!s_applying)
            QueueSave();
    }

    private static void HandleCrosshairChanged()
    {
        if (!s_applying)
            QueueSave();
    }

    private static void HandleInventoryChanged()
    {
        if (!s_applying)
            QueueSave();
    }

    private static void QueueSave()
    {
        if (s_flushQueued || !IsReady)
            return;

        s_flushQueued = true;
        FlushAsync().Forget();
    }

    private static async UniTaskVoid FlushAsync()
    {
        await UniTask.Delay(TimeSpan.FromSeconds(k_debounceSeconds));
        s_flushQueued = false;

        if (IsReady)
            await WriteAsync(Capture());
    }

    private static CosmeticsSaveData Capture()
    {
        var parts = (EBodyPart[])Enum.GetValues(typeof(EBodyPart));
        var colors = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            colors[(int)parts[i]] = CosmeticLoadout.GetPlayerColor(parts[i]);

        var slots = (EAccessorySlot[])Enum.GetValues(typeof(EAccessorySlot));
        var accessories = new int[slots.Length];
        for (int i = 0; i < slots.Length; i++)
            accessories[(int)slots[i]] = CosmeticLoadout.GetAccessory(slots[i]);

        return new CosmeticsSaveData
        {
            Colors = colors,
            Accessories = accessories,
            Owned = CosmeticInventory.Capture(),
            Tokens = CosmeticInventory.Tokens,
            Crosshair = CosmeticLoadout.GetCrosshairSettings(),
        };
    }

    private static async UniTask<CosmeticsSaveData> ReadAsync()
    {
        try
        {
            Dictionary<string, Item> loaded = await CloudSaveService.Instance.Data.Player.LoadAsync(
                new HashSet<string> { k_key }
            );

            if (!loaded.TryGetValue(k_key, out Item item))
                return null;

            var data = JsonUtility.FromJson<CosmeticsSaveData>(item.Value.GetAs<string>());

            if (
                data == null
                || data.Colors == null
                || data.Version < 1
                || data.Version > CosmeticsSaveData.k_version
            )
            {
                Debug.LogWarning(
                    $"[커스터마이징] 읽을 수 없는 포맷이라 무시한다 — 저장 {data?.Version}, 현재 {CosmeticsSaveData.k_version}"
                );
                return null;
            }

            if (data.Version < CosmeticsSaveData.k_version)
                Debug.Log(
                    $"[커스터마이징] v{data.Version} 레코드를 읽었다 — 그 판에 있던 것만 복원하고 나머지는 기본값으로 둔다"
                );

            Debug.Log($"[커스터마이징] 계정 색을 불러왔다 — {string.Join(",", data.Colors)}");
            return data;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[커스터마이징] 조회 실패(캐시 값으로 진행): {ex.Message}");
            return null;
        }
    }

    private static async UniTask WriteAsync(CosmeticsSaveData data)
    {
        try
        {
            await CloudSaveService.Instance.Data.Player.SaveAsync(
                new Dictionary<string, object> { { k_key, JsonUtility.ToJson(data) } }
            );
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[커스터마이징] 저장 실패(무시하고 진행): {ex.Message}");
        }
    }
}

[Serializable]
public class CosmeticsSaveData
{
    public const int k_version = 4;

    public int Version = k_version;

    public int[] Colors;

    public int[] Accessories;

    public List<CosmeticSlotOwnership> Owned;

    public int Tokens;

    public CrosshairSettings Crosshair;
}
