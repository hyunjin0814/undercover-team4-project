using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 설정 창 — 감도·시점·음량·마이크·언어·그래픽 설정을 즉시 적용하고 창을 닫을 때 저장한다.
/// 창 모드·해상도는 DisplayConfirmPanel로 확인받는다.
/// </summary>
public class SettingsPanel : PanelBase
{
    [Header("슬라이더")]
    [SerializeField] private Slider m_mouseSensitivitySlider;

    [Tooltip("시점 스무딩 강도 — 0이면 원시 입력. 멀미가 나면 낮춘다 (#665)")]
    [SerializeField] private Slider m_lookSmoothingSlider;

    [Tooltip("시야각(수직, 도). 좁을수록 멀미가 심해진다 (#665)")]
    [SerializeField] private Slider m_fovSlider;

    [Tooltip("크로스헤어 설정 패널을 여는 버튼 (#945)")]
    [SerializeField] private Button m_crosshairSettingsButton;

    [SerializeField] private Slider m_masterVolumeSlider;
    [SerializeField] private Slider m_bgmVolumeSlider;
    [SerializeField] private Slider m_sfxVolumeSlider;
    [SerializeField] private Slider m_voiceVolumeSlider;

    [Header("값 표시")]
    [SerializeField] private TextMeshProUGUI m_mouseSensitivityValue;
    [SerializeField] private TextMeshProUGUI m_lookSmoothingValue;
    [SerializeField] private TextMeshProUGUI m_fovValue;
    [SerializeField] private TextMeshProUGUI m_masterVolumeValue;
    [SerializeField] private TextMeshProUGUI m_bgmVolumeValue;
    [SerializeField] private TextMeshProUGUI m_sfxVolumeValue;
    [SerializeField] private TextMeshProUGUI m_voiceVolumeValue;

    [Header("토글")]
    [SerializeField] private Toggle m_micMuteToggle;

    [Tooltip("마이크 음소거 단축키 안내 — Settings.Label.MicMuteHint ({0}=음소거 키)")]
    [SerializeField] private TextMeshProUGUI m_micMuteHintText;
    [SerializeField] private LocalizedString m_micMuteHintFormat;

    [SerializeField] private Toggle m_screenShakeToggle;
    [SerializeField] private Toggle m_speedVignetteToggle;

    [Header("그래픽 (#796)")]
    [Tooltip("창 모드 — 항목은 EWindowMode 순서대로 런타임에 채운다. 인스펙터에 항목을 적지 말 것")]
    [SerializeField] private TMP_Dropdown m_windowModeDropdown;

    [Tooltip("해상도 — 항목은 Screen.resolutions를 폭x높이로 묶어 런타임에 채운다")]
    [SerializeField] private TMP_Dropdown m_resolutionDropdown;

    [Tooltip("수직동기화 — 끄면 프레임 상한이 사라진다")]
    [SerializeField] private Toggle m_vSyncToggle;

    [Tooltip("창 항목 문구 — Settings.WindowMode.Windowed")]
    [SerializeField] private LocalizedString m_windowModeWindowedLabel;

    [Tooltip("테두리 없는 전체 창 항목 문구 — Settings.WindowMode.Borderless")]
    [SerializeField] private LocalizedString m_windowModeBorderlessLabel;

    [Tooltip("전체화면 항목 문구 — Settings.WindowMode.Fullscreen")]
    [SerializeField] private LocalizedString m_windowModeFullscreenLabel;

    [Header("언어 (#374)")]
    [Tooltip("표시 언어 선택. 항목은 Localization Settings의 로케일 목록에서 런타임에 채운다 — 인스펙터에 항목을 적지 말 것")]
    [SerializeField] private TMP_Dropdown m_languageDropdown;

    [Header("탭 (#796 후속)")]
    [Tooltip("탭 버튼 — 배열 순서가 곧 페이지 순서다 (m_tabPages와 짝을 맞출 것)")]
    [SerializeField] private Button[] m_tabButtons;

    [Tooltip("탭 내용 — m_tabButtons와 같은 순서. 고른 하나만 켜진다")]
    [SerializeField] private GameObject[] m_tabPages;

    [Tooltip("고른 탭에서만 켜지는 그림 묶음 — m_tabButtons와 같은 순서 (#894)")]
    [SerializeField] private GameObject[] m_tabSelectedMarks;

    [Tooltip("고르지 않은 탭에서만 켜지는 그림 묶음 — m_tabButtons와 같은 순서 (#894)")]
    [SerializeField] private GameObject[] m_tabNormalMarks;

    [Tooltip("고른 탭 / 고르지 않은 탭의 글자색 — 고른 탭은 바탕이 밝아지므로 글자가 어두워진다")]
    [SerializeField] private Color m_tabSelectedTextColor = new Color(0.110f, 0.137f, 0.165f);
    [SerializeField] private Color m_tabNormalTextColor = new Color(0.894f, 0.918f, 0.945f);

    [Header("버튼")]
    [SerializeField] private Button m_closeButton;
    [SerializeField] private Button m_resetButton;

    [Tooltip("개발진 창을 여는 버튼 — [일반] 탭에 있다")]
    [SerializeField] private Button m_creditsButton;

    public override bool CanCloseWithESC => true;
    public override bool IsStackable => true;

    private bool m_micMuteHintBound;

    private VivoxManager Vivox => App.Net.Vivox;

    protected override void Awake()
    {
        base.Awake();

        SetupSlider(
            m_mouseSensitivitySlider,
            GameSettings.k_minMouseSensitivity,
            GameSettings.k_maxMouseSensitivity,
            HandleMouseSensitivityChanged
        );
        SetupSlider(
            m_lookSmoothingSlider,
            GameSettings.k_minLookSmoothing,
            GameSettings.k_maxLookSmoothing,
            HandleLookSmoothingChanged
        );
        SetupSlider(m_fovSlider, GameSettings.k_minFov, GameSettings.k_maxFov, HandleFovChanged);

        if (m_crosshairSettingsButton != null)
            m_crosshairSettingsButton.onClick.AddListener(HandleCrosshairSettingsClicked);

        SetupSlider(m_masterVolumeSlider, 0f, 1f, HandleMasterVolumeChanged);
        SetupSlider(m_bgmVolumeSlider, 0f, 1f, HandleBgmVolumeChanged);
        SetupSlider(m_sfxVolumeSlider, 0f, 1f, HandleSfxVolumeChanged);
        SetupSlider(m_voiceVolumeSlider, 0f, 1f, HandleVoiceVolumeChanged);

        if (m_micMuteToggle != null)
            m_micMuteToggle.onValueChanged.AddListener(HandleMicMuteToggled);

        if (m_screenShakeToggle != null)
            m_screenShakeToggle.onValueChanged.AddListener(HandleScreenShakeToggled);

        if (m_speedVignetteToggle != null)
            m_speedVignetteToggle.onValueChanged.AddListener(HandleSpeedVignetteToggled);

        if (m_languageDropdown != null)
            m_languageDropdown.onValueChanged.AddListener(HandleLanguageChanged);

        if (m_vSyncToggle != null)
            m_vSyncToggle.onValueChanged.AddListener(HandleVSyncToggled);

        if (m_windowModeDropdown != null)
            m_windowModeDropdown.onValueChanged.AddListener(HandleWindowModeChanged);

        if (m_resolutionDropdown != null)
            m_resolutionDropdown.onValueChanged.AddListener(HandleResolutionChanged);

        SetupTabs();

        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        GameSettings.OnMicMutedChanged += HandleMicMutedExternally;

        if (m_closeButton != null) m_closeButton.onClick.AddListener(ClosePanel);
        if (m_resetButton != null) m_resetButton.onClick.AddListener(HandleResetClicked);
        if (m_creditsButton != null) m_creditsButton.onClick.AddListener(HandleCreditsClicked);
    }

    protected override void OnDestroy()
    {
        if (IsOpened) GameSettings.Save();

        UnbindMicMuteHint();

        if (m_mouseSensitivitySlider != null)
            m_mouseSensitivitySlider.onValueChanged.RemoveListener(HandleMouseSensitivityChanged);
        if (m_lookSmoothingSlider != null)
            m_lookSmoothingSlider.onValueChanged.RemoveListener(HandleLookSmoothingChanged);
        if (m_fovSlider != null)
            m_fovSlider.onValueChanged.RemoveListener(HandleFovChanged);
        if (m_crosshairSettingsButton != null)
            m_crosshairSettingsButton.onClick.RemoveListener(HandleCrosshairSettingsClicked);
        if (m_masterVolumeSlider != null)
            m_masterVolumeSlider.onValueChanged.RemoveListener(HandleMasterVolumeChanged);
        if (m_bgmVolumeSlider != null)
            m_bgmVolumeSlider.onValueChanged.RemoveListener(HandleBgmVolumeChanged);
        if (m_sfxVolumeSlider != null)
            m_sfxVolumeSlider.onValueChanged.RemoveListener(HandleSfxVolumeChanged);
        if (m_voiceVolumeSlider != null)
            m_voiceVolumeSlider.onValueChanged.RemoveListener(HandleVoiceVolumeChanged);
        if (m_micMuteToggle != null)
            m_micMuteToggle.onValueChanged.RemoveListener(HandleMicMuteToggled);
        if (m_screenShakeToggle != null)
            m_screenShakeToggle.onValueChanged.RemoveListener(HandleScreenShakeToggled);
        if (m_speedVignetteToggle != null)
            m_speedVignetteToggle.onValueChanged.RemoveListener(HandleSpeedVignetteToggled);
        if (m_languageDropdown != null)
            m_languageDropdown.onValueChanged.RemoveListener(HandleLanguageChanged);
        if (m_vSyncToggle != null)
            m_vSyncToggle.onValueChanged.RemoveListener(HandleVSyncToggled);
        if (m_windowModeDropdown != null)
            m_windowModeDropdown.onValueChanged.RemoveListener(HandleWindowModeChanged);
        if (m_resolutionDropdown != null)
            m_resolutionDropdown.onValueChanged.RemoveListener(HandleResolutionChanged);
        if (m_tabButtons != null)
            foreach (Button tab in m_tabButtons)
                if (tab != null)
                    tab.onClick.RemoveAllListeners();

        GameSettings.OnMicMutedChanged -= HandleMicMutedExternally;

        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;

        if (m_closeButton != null)
            m_closeButton.onClick.RemoveListener(ClosePanel);
        if (m_resetButton != null)
            m_resetButton.onClick.RemoveListener(HandleResetClicked);
        if (m_creditsButton != null)
            m_creditsButton.onClick.RemoveListener(HandleCreditsClicked);

        base.OnDestroy();
    }

    /// <summary>탭 버튼에 탭 번호를 연결한다.</summary>
    private void SetupTabs()
    {
        if (m_tabButtons == null)
            return;

        for (int i = 0; i < m_tabButtons.Length; i++)
        {
            if (m_tabButtons[i] == null)
                continue;

            int index = i;
            m_tabButtons[i].onClick.AddListener(() => SelectTab(index));
        }
    }

    /// <summary>그 탭만 켜고 나머지를 끈다. 꺼진 페이지는 레이아웃에서도 빠져 창 높이가 그 탭에 맞는다.</summary>
    private void SelectTab(int index)
    {
        if (m_tabPages != null)
            for (int i = 0; i < m_tabPages.Length; i++)
                if (m_tabPages[i] != null)
                    m_tabPages[i].SetActive(i == index);

        if (m_tabButtons == null)
            return;

        for (int i = 0; i < m_tabButtons.Length; i++)
            ApplyTabVisual(i, i == index);
    }

    private void ApplyTabVisual(int index, bool selected)
    {
        SetTabMark(m_tabSelectedMarks, index, selected);
        SetTabMark(m_tabNormalMarks, index, !selected);

        Button tab = m_tabButtons[index];
        if (tab == null)
            return;

        TMP_Text label = tab.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
            label.color = selected ? m_tabSelectedTextColor : m_tabNormalTextColor;
    }

    private static void SetTabMark(GameObject[] marks, int index, bool on)
    {
        if (marks == null || index < 0 || index >= marks.Length || marks[index] == null)
            return;

        marks[index].SetActive(on);
    }

    private static void SetToggle(Toggle toggle, bool on)
    {
        if (toggle == null)
            return;

        toggle.SetIsOnWithoutNotify(on);

        ToggleTint tint = toggle.GetComponent<ToggleTint>();
        if (tint != null)
            tint.Refresh();
    }

    private static void SetupSlider(Slider slider, float min, float max, UnityAction<float> handler)
    {
        if (slider == null) return;

        slider.minValue = min;
        slider.maxValue = max;
        slider.wholeNumbers = false;
        slider.onValueChanged.AddListener(handler);
    }

    public override void OpenPanel()
    {
        SyncFromSettings();
        SelectTab(0);

        base.OpenPanel();
    }

    public override void ClosePanel()
    {
        GameSettings.Save();
        UnbindMicMuteHint();
        base.ClosePanel();
    }

    /// <summary>슬라이더·토글을 알림 없이 현재 설정값으로 맞춘다.</summary>
    private void SyncFromSettings()
    {
        if (m_mouseSensitivitySlider != null)
            m_mouseSensitivitySlider.SetValueWithoutNotify(GameSettings.MouseSensitivity);
        if (m_lookSmoothingSlider != null)
            m_lookSmoothingSlider.SetValueWithoutNotify(GameSettings.LookSmoothing);
        if (m_fovSlider != null)
            m_fovSlider.SetValueWithoutNotify(GameSettings.Fov);
        if (m_masterVolumeSlider != null)
            m_masterVolumeSlider.SetValueWithoutNotify(GameSettings.MasterVolume);
        if (m_bgmVolumeSlider != null)
            m_bgmVolumeSlider.SetValueWithoutNotify(GameSettings.BgmVolume);
        if (m_sfxVolumeSlider != null)
            m_sfxVolumeSlider.SetValueWithoutNotify(GameSettings.SfxVolume);
        if (m_voiceVolumeSlider != null)
            m_voiceVolumeSlider.SetValueWithoutNotify(GameSettings.VoiceVolume);
        SetToggle(m_micMuteToggle, GameSettings.MicMuted);
        SetToggle(m_screenShakeToggle, GameSettings.ScreenShake);
        SetToggle(m_speedVignetteToggle, GameSettings.SpeedVignette);
        SetToggle(m_vSyncToggle, GameSettings.VSync);

        SyncDisplayDropdowns();
        SyncLanguageDropdown();
        RefreshLabels();
        RefreshMicMuteHint();
    }

    /// <summary>언어 드롭다운을 각 언어의 자기 이름으로 채우고 현재 언어를 선택한다.</summary>
    private void SyncLanguageDropdown()
    {
        if (m_languageDropdown == null)
            return;

        IList<Locale> locales = GameSettings.AvailableLocales;
        var labels = new List<string>(locales.Count);
        int current = 0;

        for (int i = 0; i < locales.Count; i++)
        {
            Locale locale = locales[i];
            var culture = locale.Identifier.CultureInfo;
            labels.Add(culture != null ? culture.NativeName : locale.LocaleName);

            if (locale == GameSettings.Locale)
                current = i;
        }

        m_languageDropdown.ClearOptions();
        m_languageDropdown.AddOptions(labels);
        m_languageDropdown.SetValueWithoutNotify(current);
        m_languageDropdown.RefreshShownValue();
    }

    /// <summary>창 모드·해상도 드롭다운을 현재 화면 값으로 맞춘다.</summary>
    private void SyncDisplayDropdowns()
    {
        SyncWindowModeDropdown();
        SyncResolutionDropdown();
    }

    private void SyncWindowModeDropdown()
    {
        if (m_windowModeDropdown == null)
            return;

        var labels = new List<string>
        {
            WindowModeLabel(m_windowModeWindowedLabel, EWindowMode.Windowed),
            WindowModeLabel(m_windowModeBorderlessLabel, EWindowMode.Borderless),
            WindowModeLabel(m_windowModeFullscreenLabel, EWindowMode.Fullscreen),
        };

        m_windowModeDropdown.ClearOptions();
        m_windowModeDropdown.AddOptions(labels);
        m_windowModeDropdown.SetValueWithoutNotify((int)GameSettings.WindowMode);
        m_windowModeDropdown.RefreshShownValue();
    }

    private static string WindowModeLabel(LocalizedString text, EWindowMode mode) =>
        text != null && !text.IsEmpty ? text.GetLocalizedString() : mode.ToString();

    private void SyncResolutionDropdown()
    {
        if (m_resolutionDropdown == null)
            return;

        IReadOnlyList<Vector2Int> resolutions = GameSettings.AvailableResolutions;
        var labels = new List<string>(resolutions.Count);
        int current = 0;

        for (int i = 0; i < resolutions.Count; i++)
        {
            labels.Add($"{resolutions[i].x} x {resolutions[i].y}");

            if (resolutions[i] == GameSettings.Resolution)
                current = i;
        }

        m_resolutionDropdown.ClearOptions();
        m_resolutionDropdown.AddOptions(labels);
        m_resolutionDropdown.SetValueWithoutNotify(current);
        m_resolutionDropdown.RefreshShownValue();
    }

    private void HandleWindowModeChanged(int index)
    {
        if (!System.Enum.IsDefined(typeof(EWindowMode), index))
            return;

        ApplyDisplay((EWindowMode)index, GameSettings.Resolution);
    }

    private void HandleResolutionChanged(int index)
    {
        IReadOnlyList<Vector2Int> resolutions = GameSettings.AvailableResolutions;
        if (index < 0 || index >= resolutions.Count)
            return;

        ApplyDisplay(GameSettings.WindowMode, resolutions[index]);
    }

    private void HandleVSyncToggled(bool on) => GameSettings.VSync = on;

    /// <summary>고른 창 모드·해상도를 적용하고 확인창을 띄운다. 확인창이 없으면 적용하지 않는다.</summary>
    private void ApplyDisplay(EWindowMode mode, Vector2Int resolution)
    {
        if (App.UI.Current == null || !App.UI.Current.TryGetPanel(out DisplayConfirmPanel confirm))
        {
            Debug.LogError(
                $"[{nameof(SettingsPanel)}] 확인창({nameof(DisplayConfirmPanel)})이 없어 화면 설정을 적용하지 않았습니다 — 씬에 배치됐는지 확인하세요.",
                this
            );
            SyncDisplayDropdowns();
            return;
        }

        if (confirm.IsOpened)
        {
            SyncDisplayDropdowns();
            return;
        }

        EWindowMode previousMode = GameSettings.WindowMode;
        Vector2Int previousResolution = GameSettings.Resolution;

        GameSettings.ApplyDisplay(mode, resolution);
        confirm.Begin(previousMode, previousResolution, SyncDisplayDropdowns);
    }

    private void HandleLocaleChanged(Locale locale) => SyncWindowModeDropdown();

    private void HandleMouseSensitivityChanged(float value)
    {
        GameSettings.MouseSensitivity = value;
        RefreshLabels();
    }

    private void HandleLookSmoothingChanged(float value)
    {
        GameSettings.LookSmoothing = value;
        RefreshLabels();
    }

    private void HandleFovChanged(float value)
    {
        GameSettings.Fov = value;
        RefreshLabels();
    }

    private void HandleMasterVolumeChanged(float value)
    {
        GameSettings.MasterVolume = value;
        RefreshLabels();
    }

    private void HandleBgmVolumeChanged(float value)
    {
        GameSettings.BgmVolume = value;
        RefreshLabels();
    }

    private void HandleSfxVolumeChanged(float value)
    {
        GameSettings.SfxVolume = value;
        RefreshLabels();
    }

    private void HandleVoiceVolumeChanged(float value)
    {
        GameSettings.VoiceVolume = value;
        RefreshLabels();
    }

    private void HandleMicMuteToggled(bool on) => GameSettings.MicMuted = on;

    private void RefreshMicMuteHint()
    {
        if (m_micMuteHintText == null)
            return;

        if (Vivox == null || m_micMuteHintFormat == null || m_micMuteHintFormat.IsEmpty)
        {
            m_micMuteHintText.text = string.Empty;
            return;
        }

        UnbindMicMuteHint();

        m_micMuteHintFormat.Arguments = new object[] { Vivox.MicMuteBinding };
        m_micMuteHintFormat.StringChanged += HandleMicMuteHintChanged;
        m_micMuteHintBound = true;
    }

    private void HandleMicMuteHintChanged(string localized)
    {
        if (m_micMuteHintText != null)
            m_micMuteHintText.text = localized;
    }

    private void UnbindMicMuteHint()
    {
        if (!m_micMuteHintBound)
            return;

        m_micMuteHintFormat.StringChanged -= HandleMicMuteHintChanged;
        m_micMuteHintBound = false;
    }

    private void HandleScreenShakeToggled(bool on) => GameSettings.ScreenShake = on;

    private void HandleSpeedVignetteToggled(bool on) => GameSettings.SpeedVignette = on;

    private void HandleLanguageChanged(int index)
    {
        IList<Locale> locales = GameSettings.AvailableLocales;
        if (index < 0 || index >= locales.Count)
            return;

        GameSettings.Locale = locales[index];
    }

    private void HandleMicMutedExternally(bool on) => SetToggle(m_micMuteToggle, on);

    private void HandleCreditsClicked() => App.UI.Current?.OpenPanel<CreditsPanel>();

    private void HandleCrosshairSettingsClicked()
    {
        if (App.UI.Current != null && App.UI.Current.TryGetPanel(out CrosshairSettingsPanel panel))
            panel.OpenPanel();
        else
            Debug.LogWarning("SettingsPanel: 크로스헤어 설정 패널을 찾지 못했다");
    }

    private void HandleResetClicked()
    {
        GameSettings.ResetToDefaults();
        SyncFromSettings();
    }

    private void RefreshLabels()
    {
        if (m_mouseSensitivityValue != null) 
            m_mouseSensitivityValue.text = $"x{GameSettings.MouseSensitivity:0.00}";

        if (m_lookSmoothingValue != null)
            m_lookSmoothingValue.text = $"{GameSettings.LookSmoothing * 100f:0}%";

        if (m_fovValue != null)
            m_fovValue.text = $"{GameSettings.Fov:0}°";

        if (m_masterVolumeValue != null)
            m_masterVolumeValue.text = $"{GameSettings.MasterVolume * 100f:0}%";

        if (m_bgmVolumeValue != null)
            m_bgmVolumeValue.text = $"{GameSettings.BgmVolume * 100f:0}%";

        if (m_sfxVolumeValue != null)
            m_sfxVolumeValue.text = $"{GameSettings.SfxVolume * 100f:0}%";

        if (m_voiceVolumeValue != null)
            m_voiceVolumeValue.text = $"{GameSettings.VoiceVolume * 100f:0}%";
    }
}
