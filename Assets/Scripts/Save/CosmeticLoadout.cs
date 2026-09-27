using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 계정이 착용한 치장(로봇 색·액세서리) — 정본은 Cloud Save, PlayerPrefs는 계정별 캐시다.
/// </summary>
public static class CosmeticLoadout
{
    private const string k_playerColorKeyPrefix = "settings.playerColor.";
    private const string k_accessoryKeyPrefix = "settings.accessory.";
    private const string k_crosshairKeyPrefix = "settings.crosshair.";

    private const string k_localAccount = "local";

    private const int k_defaultPlayerColor = 0;

    private static readonly int[] s_playerColors = new int[
        Enum.GetValues(typeof(EBodyPart)).Length
    ];

    private static readonly int[] s_accessories = new int[
        Enum.GetValues(typeof(EAccessorySlot)).Length
    ];

    private static CrosshairSettings s_crosshair = CrosshairSettings.Default();

    private static string s_account = k_localAccount;

    public static event Action<EBodyPart> OnPlayerColorChanged;

    public static event Action<EAccessorySlot> OnAccessoryChanged;

    public static event Action OnCrosshairSettingsChanged;

    /// <summary>해당 부위의 팔레트 색 인덱스를 돌려준다.</summary>
    public static int GetPlayerColor(EBodyPart part) => s_playerColors[(int)part];

    public static void SetPlayerColor(EBodyPart part, int index)
    {
        int clamped = Mathf.Max(0, index);
        if (s_playerColors[(int)part] == clamped)
            return;

        s_playerColors[(int)part] = clamped;
        PlayerPrefs.SetInt(ColorKey(part), clamped);
        OnPlayerColorChanged?.Invoke(part);
    }

    /// <summary>그 슬롯에 쓴 카탈로그 인덱스 — 0은 안 씀. 카탈로그 길이는 여기서 모른다.</summary>
    public static int GetAccessory(EAccessorySlot slot) => s_accessories[(int)slot];

    public static void SetAccessory(EAccessorySlot slot, int index)
    {
        int clamped = Mathf.Max(0, index);
        if (s_accessories[(int)slot] == clamped)
            return;

        s_accessories[(int)slot] = clamped;
        PlayerPrefs.SetInt(AccessoryKey(slot), clamped);
        OnAccessoryChanged?.Invoke(slot);
    }

    /// <summary>현재 크로스헤어 설정 — 값 자체를 그대로 돌려준다(참조 공유 주의: 호출부는 읽기 전용으로 쓸 것).</summary>
    public static CrosshairSettings GetCrosshairSettings() => s_crosshair;

    public static void SetCrosshairSettings(CrosshairSettings settings)
    {
        if (settings == null)
            return;

        s_crosshair = settings;
        SaveCrosshairToPrefs(settings);
        OnCrosshairSettingsChanged?.Invoke();
    }

    /// <summary>클라우드에서 받은 한 벌을 적용한다 — 캐시에도 남긴다. (CosmeticsSaveService)</summary>
    public static void ApplyPlayerColors(IReadOnlyList<int> colors)
    {
        if (colors == null)
            return;

        foreach (EBodyPart part in Enum.GetValues(typeof(EBodyPart)))
        {
            int index = (int)part;
            if (index >= colors.Count)
                continue;

            int clamped = Mathf.Max(0, colors[index]);
            s_playerColors[index] = clamped;
            PlayerPrefs.SetInt(ColorKey(part), clamped);
            OnPlayerColorChanged?.Invoke(part);
        }
    }

    /// <summary>클라우드에서 받은 한 벌을 적용한다 — 캐시에도 남긴다. (CosmeticsSaveService)</summary>
    public static void ApplyAccessories(IReadOnlyList<int> accessories)
    {
        if (accessories == null)
            return;

        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
        {
            int index = (int)slot;
            if (index >= accessories.Count)
                continue;

            int clamped = Mathf.Max(0, accessories[index]);
            s_accessories[index] = clamped;
            PlayerPrefs.SetInt(AccessoryKey(slot), clamped);
            OnAccessoryChanged?.Invoke(slot);
        }
    }

    /// <summary>클라우드에서 받은 값을 적용한다 — 캐시에도 남긴다. null이면 기본값을 그대로 둔다(옛 레코드). (CosmeticsSaveService, #945)</summary>
    public static void ApplyCrosshairSettings(CrosshairSettings settings)
    {
        if (settings == null)
            return;

        s_crosshair = settings;
        SaveCrosshairToPrefs(settings);
        OnCrosshairSettingsChanged?.Invoke();
    }

    /// <summary>계정 자리를 갈아탄다 — 캐시에서 그 계정 것을 다시 읽는다. (CosmeticsSaveService)</summary>
    public static void UseAccount(string accountId)
    {
        string next = string.IsNullOrWhiteSpace(accountId) ? k_localAccount : accountId;
        if (s_account == next)
            return;

        s_account = next;
        LoadAccount();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Load()
    {
        OnPlayerColorChanged = null;
        OnAccessoryChanged = null;
        OnCrosshairSettingsChanged = null;

        s_account = k_localAccount;
        LoadAccount();
    }

    private static void LoadAccount()
    {
        LoadPlayerColors();
        LoadAccessories();
        LoadCrosshairSettings();
    }

    private static string ColorKey(EBodyPart part) =>
        k_playerColorKeyPrefix + s_account + "." + part;

    private static string AccessoryKey(EAccessorySlot slot) =>
        k_accessoryKeyPrefix + s_account + "." + slot;

    private static string CrosshairShapeKey() => k_crosshairKeyPrefix + s_account + ".shape";

    private static string CrosshairColorKey() => k_crosshairKeyPrefix + s_account + ".color";

    private static string CrosshairSizeKey() => k_crosshairKeyPrefix + s_account + ".size";

    private static string CrosshairThicknessKey() =>
        k_crosshairKeyPrefix + s_account + ".thickness";

    private static void LoadPlayerColors()
    {
        foreach (EBodyPart part in Enum.GetValues(typeof(EBodyPart)))
        {
            s_playerColors[(int)part] = PlayerPrefs.GetInt(ColorKey(part), k_defaultPlayerColor);
            OnPlayerColorChanged?.Invoke(part);
        }
    }

    private static void LoadAccessories()
    {
        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
        {
            s_accessories[(int)slot] = PlayerPrefs.GetInt(AccessoryKey(slot), 0);
            OnAccessoryChanged?.Invoke(slot);
        }
    }

    private static void SaveCrosshairToPrefs(CrosshairSettings settings)
    {
        PlayerPrefs.SetInt(CrosshairShapeKey(), (int)settings.Shape);
        PlayerPrefs.SetInt(CrosshairColorKey(), settings.ColorIndex);
        PlayerPrefs.SetFloat(CrosshairSizeKey(), settings.Size);
        PlayerPrefs.SetFloat(CrosshairThicknessKey(), settings.Thickness);
    }

    private static void LoadCrosshairSettings()
    {
        CrosshairSettings fallback = CrosshairSettings.Default();
        s_crosshair = new CrosshairSettings
        {
            Shape = (ECrosshairShape)PlayerPrefs.GetInt(CrosshairShapeKey(), (int)fallback.Shape),
            ColorIndex = PlayerPrefs.GetInt(CrosshairColorKey(), fallback.ColorIndex),
            Size = PlayerPrefs.GetFloat(CrosshairSizeKey(), fallback.Size),
            Thickness = PlayerPrefs.GetFloat(CrosshairThicknessKey(), fallback.Thickness),
        };
        OnCrosshairSettingsChanged?.Invoke();
    }
}
