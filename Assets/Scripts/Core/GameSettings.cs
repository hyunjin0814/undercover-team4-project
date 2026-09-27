using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;

/// <summary>
/// 로컬 게임 설정(감도·시점·시야각·흔들림·비네트·음량·마이크·언어·그래픽)을 PlayerPrefs에 저장·적용하는 정적 저장소.
/// </summary>
public static class GameSettings
{
    private const string k_mouseSensitivityName = "mouseSensitivity.v2";
    private const string k_lookSmoothingName = "lookSmoothing";
    private const string k_fovName = "fov";
    private const string k_screenShakeName = "screenShake";
    private const string k_speedVignetteName = "speedVignette";
    private const string k_masterVolumeName = "masterVolume";
    private const string k_bgmVolumeName = "bgmVolume";
    private const string k_sfxVolumeName = "sfxVolume";
    private const string k_voiceVolumeName = "voiceVolume";
    private const string k_micMutedName = "micMuted";
    private const string k_vSyncName = "vSync";
    private const string k_windowModeKey = "settings.windowMode";
    private const string k_resolutionWidthKey = "settings.resolutionWidth";
    private const string k_resolutionHeightKey = "settings.resolutionHeight";

    private const string k_settingPrefix = "settings.";
    private const string k_localAccount = "local";

    public const float k_minMouseSensitivity = 0.25f;
    public const float k_maxMouseSensitivity = 3f;

    public const float k_minLookSmoothing = 0f;
    public const float k_maxLookSmoothing = 1f;

    private const float k_slowestLookRate = 10f;
    private const float k_fastestLookRate = 60f;

    private const float k_defaultMouseSensitivity = 1f;

    private const float k_defaultLookSmoothing = 0.4f;

    public const float k_minFov = 60f;
    public const float k_maxFov = 100f;

    private const float k_defaultFov = 70f;
    private const bool k_defaultScreenShake = true;
    private const bool k_defaultSpeedVignette = true;

    private const float k_defaultMasterVolume = 1f;
    private const float k_defaultBgmVolume = 1f;
    private const float k_defaultSfxVolume = 1f;
    private const float k_defaultVoiceVolume = 1f;
    private const bool k_defaultMicMuted = false;

    private const bool k_defaultVSync = true;

    private const int k_minResolutionHeight = 720;

    private static string s_account = k_localAccount;

    private static float s_mouseSensitivity = k_defaultMouseSensitivity;
    private static float s_lookSmoothing = k_defaultLookSmoothing;
    private static float s_fov = k_defaultFov;
    private static bool s_screenShake = k_defaultScreenShake;
    private static bool s_speedVignette = k_defaultSpeedVignette;
    private static float s_masterVolume = k_defaultMasterVolume;
    private static float s_bgmVolume = k_defaultBgmVolume;
    private static float s_sfxVolume = k_defaultSfxVolume;
    private static float s_voiceVolume = k_defaultVoiceVolume;
    private static bool s_micMuted = k_defaultMicMuted;
    private static bool s_vSync = k_defaultVSync;

    private static EWindowMode s_windowMode;
    private static Vector2Int s_resolution;
    private static Vector2Int[] s_resolutions;

    public static float MouseSensitivity
    {
        get => s_mouseSensitivity;
        set
        {
            s_mouseSensitivity = Mathf.Clamp(value, k_minMouseSensitivity, k_maxMouseSensitivity);
            PlayerPrefs.SetFloat(Key(k_mouseSensitivityName), s_mouseSensitivity);
        }
    }

    public static float LookSmoothing
    {
        get => s_lookSmoothing;
        set
        {
            s_lookSmoothing = Mathf.Clamp(value, k_minLookSmoothing, k_maxLookSmoothing);
            PlayerPrefs.SetFloat(Key(k_lookSmoothingName), s_lookSmoothing);
        }
    }

    public static float LookSmoothingRate =>
        s_lookSmoothing <= 0.001f
            ? 0f
            : Mathf.Lerp(k_fastestLookRate, k_slowestLookRate, s_lookSmoothing);

    public static float Fov
    {
        get => s_fov;
        set
        {
            s_fov = Mathf.Clamp(value, k_minFov, k_maxFov);
            PlayerPrefs.SetFloat(Key(k_fovName), s_fov);
        }
    }

    public static bool ScreenShake
    {
        get => s_screenShake;
        set
        {
            s_screenShake = value;
            PlayerPrefs.SetInt(Key(k_screenShakeName), s_screenShake ? 1 : 0);
        }
    }

    public static bool SpeedVignette
    {
        get => s_speedVignette;
        set
        {
            s_speedVignette = value;
            PlayerPrefs.SetInt(Key(k_speedVignetteName), s_speedVignette ? 1 : 0);
        }
    }

    public static float MasterVolume
    {
        get => s_masterVolume;
        set
        {
            s_masterVolume = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(DeviceKey(k_masterVolumeName), s_masterVolume);
            AudioListener.volume = s_masterVolume;
        }
    }

    public static float BgmVolume
    {
        get => s_bgmVolume;
        set
        {
            s_bgmVolume = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(DeviceKey(k_bgmVolumeName), s_bgmVolume);
            App.Sound?.Bgm?.ApplyVolume();
        }
    }

    public static float SfxVolume
    {
        get => s_sfxVolume;
        set
        {
            s_sfxVolume = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(DeviceKey(k_sfxVolumeName), s_sfxVolume);
            OnSfxVolumeChanged?.Invoke(s_sfxVolume);
        }
    }

    public static event Action<float> OnSfxVolumeChanged;

    public static float VoiceVolume
    {
        get => s_voiceVolume;
        set
        {
            s_voiceVolume = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(DeviceKey(k_voiceVolumeName), s_voiceVolume);
            App.Net.Vivox?.ApplyVoiceVolume();
        }
    }

    public static event Action<bool> OnMicMutedChanged;

    public static bool MicMuted
    {
        get => s_micMuted;
        set
        {
            s_micMuted = value;
            PlayerPrefs.SetInt(Key(k_micMutedName), s_micMuted ? 1 : 0);
            App.Net.Vivox?.ApplyMicMute();
            OnMicMutedChanged?.Invoke(s_micMuted);
        }
    }

    public static bool VSync
    {
        get => s_vSync;
        set
        {
            s_vSync = value;
            PlayerPrefs.SetInt(Key(k_vSyncName), s_vSync ? 1 : 0);
            QualitySettings.vSyncCount = s_vSync ? 1 : 0;
        }
    }

    public static EWindowMode WindowMode => s_windowMode;

    public static Vector2Int Resolution => s_resolution;

    public static IReadOnlyList<Vector2Int> AvailableResolutions
    {
        get
        {
            if (s_resolutions != null)
                return s_resolutions;

            var seen = new HashSet<Vector2Int>();
            var list = new List<Vector2Int>();

            foreach (var resolution in Screen.resolutions)
            {
                var size = new Vector2Int(resolution.width, resolution.height);
                if (size.y < k_minResolutionHeight)
                    continue;

                if (seen.Add(size))
                    list.Add(size);
            }

            if (s_resolution.x > 0 && s_resolution.y > 0 && seen.Add(s_resolution))
                list.Add(s_resolution);

            list.Sort((a, b) => a.x != b.x ? b.x.CompareTo(a.x) : b.y.CompareTo(a.y));
            s_resolutions = list.ToArray();
            return s_resolutions;
        }
    }

    /// <summary>창 모드·해상도를 화면에만 적용한다. 저장은 KeepDisplay가 한다.</summary>
    public static void ApplyDisplay(EWindowMode mode, Vector2Int resolution)
    {
        s_windowMode = mode;
        s_resolution = new Vector2Int(Mathf.Max(1, resolution.x), Mathf.Max(1, resolution.y));
        Screen.SetResolution(s_resolution.x, s_resolution.y, ToFullScreenMode(mode));

        CursorLock.Reassert();
    }

    /// <summary>현재 창 모드·해상도를 디스크에 바로 저장한다(확인창 [유지]).</summary>
    public static void KeepDisplay()
    {
        PlayerPrefs.SetInt(k_windowModeKey, (int)s_windowMode);
        PlayerPrefs.SetInt(k_resolutionWidthKey, s_resolution.x);
        PlayerPrefs.SetInt(k_resolutionHeightKey, s_resolution.y);
        PlayerPrefs.Save();
    }

    private static FullScreenMode ToFullScreenMode(EWindowMode mode) =>
        mode switch
        {
            EWindowMode.Windowed => FullScreenMode.Windowed,
            EWindowMode.Fullscreen => FullScreenMode.ExclusiveFullScreen,
            _ => FullScreenMode.FullScreenWindow,
        };

    private static EWindowMode ToWindowMode(FullScreenMode mode) =>
        mode switch
        {
            FullScreenMode.Windowed => EWindowMode.Windowed,
            FullScreenMode.ExclusiveFullScreen => EWindowMode.Fullscreen,
            _ => EWindowMode.Borderless,
        };

    /// <summary>설정 저장 자리를 이 계정으로 전환한다. 비우면 로그인 전 자리로 돌아간다.</summary>
    public static void UseAccount(string accountId)
    {
        string next = string.IsNullOrWhiteSpace(accountId) ? k_localAccount : accountId;
        if (s_account == next)
            return;

        s_account = next;
        LoadAccountSettings();
    }

    /// <summary>항목의 실제 PlayerPrefs 키를 만든다(로그인 전에는 계정 접두어 없음).</summary>
    private static string Key(string name) =>
        s_account == k_localAccount
            ? k_settingPrefix + name
            : k_settingPrefix + s_account + "." + name;

    private static string DeviceKey(string name) => k_settingPrefix + name;

    private static readonly string[] s_deviceVolumeNames =
    {
        k_masterVolumeName,
        k_bgmVolumeName,
        k_sfxVolumeName,
        k_voiceVolumeName,
    };

    public static IList<Locale> AvailableLocales => LocalizationSettings.AvailableLocales.Locales;

    public static Locale Locale
    {
        get => LocalizationSettings.SelectedLocale;
        set
        {
            if (value == null || value == LocalizationSettings.SelectedLocale)
                return;

            LocalizationSettings.SelectedLocale = value;
            PlayerPrefLocaleSelector.Save(value);
        }
    }

    /// <summary>로그인 전(local) 자리의 저장값을 읽어 언어 외 모든 설정에 적용한다.</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Load()
    {
        OnMicMutedChanged = null;
        OnSfxVolumeChanged = null;

        ResetFields();

        s_account = k_localAccount;
        LoadAccountSettings();

        LoadDisplay();
    }

    /// <summary>현재 계정 자리의 저장값을 읽어 적용한다. 저장값이 없으면 현재 값을 유지한다.</summary>
    private static void LoadAccountSettings()
    {
        PromoteAccountVolumesToDevice();

        MouseSensitivity = PlayerPrefs.GetFloat(Key(k_mouseSensitivityName), s_mouseSensitivity);
        LookSmoothing = PlayerPrefs.GetFloat(Key(k_lookSmoothingName), s_lookSmoothing);
        Fov = PlayerPrefs.GetFloat(Key(k_fovName), s_fov);
        ScreenShake = PlayerPrefs.GetInt(Key(k_screenShakeName), s_screenShake ? 1 : 0) != 0;
        SpeedVignette = PlayerPrefs.GetInt(Key(k_speedVignetteName), s_speedVignette ? 1 : 0) != 0;
        MasterVolume = PlayerPrefs.GetFloat(DeviceKey(k_masterVolumeName), s_masterVolume);
        BgmVolume = PlayerPrefs.GetFloat(DeviceKey(k_bgmVolumeName), s_bgmVolume);
        SfxVolume = PlayerPrefs.GetFloat(DeviceKey(k_sfxVolumeName), s_sfxVolume);
        VoiceVolume = PlayerPrefs.GetFloat(DeviceKey(k_voiceVolumeName), s_voiceVolume);
        MicMuted = PlayerPrefs.GetInt(Key(k_micMutedName), s_micMuted ? 1 : 0) != 0;
        VSync = PlayerPrefs.GetInt(Key(k_vSyncName), s_vSync ? 1 : 0) != 0;
    }

    private static void PromoteAccountVolumesToDevice()
    {
        if (s_account == k_localAccount)
            return;

        foreach (string name in s_deviceVolumeNames)
        {
            string accountKey = k_settingPrefix + s_account + "." + name;
            if (!PlayerPrefs.HasKey(accountKey))
                continue;

            PlayerPrefs.SetFloat(DeviceKey(name), PlayerPrefs.GetFloat(accountKey));
            PlayerPrefs.DeleteKey(accountKey);
        }
    }

    private static void ResetFields()
    {
        s_mouseSensitivity = k_defaultMouseSensitivity;
        s_lookSmoothing = k_defaultLookSmoothing;
        s_fov = k_defaultFov;
        s_screenShake = k_defaultScreenShake;
        s_speedVignette = k_defaultSpeedVignette;
        s_masterVolume = k_defaultMasterVolume;
        s_bgmVolume = k_defaultBgmVolume;
        s_sfxVolume = k_defaultSfxVolume;
        s_voiceVolume = k_defaultVoiceVolume;
        s_micMuted = k_defaultMicMuted;
        s_vSync = k_defaultVSync;
    }

    /// <summary>저장된 창 모드·해상도를 적용한다. 저장값이 없으면 화면을 건드리지 않는다.</summary>
    private static void LoadDisplay()
    {
        s_resolutions = null;
        s_windowMode = ToWindowMode(Screen.fullScreenMode);
        s_resolution = new Vector2Int(Screen.width, Screen.height);

        if (!PlayerPrefs.HasKey(k_windowModeKey))
            return;

        int storedMode = PlayerPrefs.GetInt(k_windowModeKey, (int)s_windowMode);
        ApplyDisplay(
            Enum.IsDefined(typeof(EWindowMode), storedMode)
                ? (EWindowMode)storedMode
                : s_windowMode,
            new Vector2Int(
                PlayerPrefs.GetInt(k_resolutionWidthKey, s_resolution.x),
                PlayerPrefs.GetInt(k_resolutionHeightKey, s_resolution.y)
            )
        );
    }

    /// <summary>언어·창 모드·해상도를 제외한 설정을 기본값으로 되돌린다.</summary>
    public static void ResetToDefaults()
    {
        MouseSensitivity = k_defaultMouseSensitivity;
        LookSmoothing = k_defaultLookSmoothing;
        Fov = k_defaultFov;
        ScreenShake = k_defaultScreenShake;
        SpeedVignette = k_defaultSpeedVignette;
        MasterVolume = k_defaultMasterVolume;
        BgmVolume = k_defaultBgmVolume;
        SfxVolume = k_defaultSfxVolume;
        VoiceVolume = k_defaultVoiceVolume;
        MicMuted = k_defaultMicMuted;
        VSync = k_defaultVSync;
    }

    /// <summary>설정을 디스크에 기록한다. 설정 창을 닫을 때 한 번 호출한다.</summary>
    public static void Save() => PlayerPrefs.Save();
}
