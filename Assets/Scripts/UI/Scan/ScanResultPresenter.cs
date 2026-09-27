using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.UI;

/// <summary>
/// 스캐너를 든 채 NPC를 조준하면 그 NPC의 스캔 카드를 켠다 — 스캔한 NPC는 실제 값, 아니면 ??로 표시한다.
/// 스캔 기록은 이 플레이어 로컬 HashSet으로 관리하며 오너 전용이다.
/// </summary>
public class ScanResultPresenter : NetworkBehaviour
{
    [Tooltip(
        "HUD 루트(캔버스). 남의 플레이어 것이 내 화면에 겹쳐 그려지지 않게 오너가 아니면 끈다"
    )]
    [SerializeField]
    private GameObject m_uiRoot;

    [Header("스캐너 피드백 (#309) — m_uiRoot 하위 오너 전용 UI")]
    [Tooltip("배터리 게이지 패널 루트(배경 포함). 스캐너를 들었을 때만 켜진다 — 표시/숨김 토글 대상")]
    [SerializeField]
    private GameObject m_batteryPanel;

    [Tooltip("배터리 라벨 (m_batteryPanel 하위). 칸이 최대치를 다 담으면 라벨만, 아니면 숫자까지 찍는다")]
    [SerializeField]
    private TextMeshProUGUI m_batteryText;

    [Tooltip("배터리 칸 — 왼쪽부터 잔량만큼 켜진다. 비워 두면 숫자 표기만 남는다 (#894)")]
    [SerializeField]
    private Image[] m_batteryCells = new Image[0];

    [SerializeField]
    private Color m_cellFilledTone = new Color(0.98f, 0.62f, 0.16f, 1f);

    [SerializeField]
    private Color m_cellEmptyTone = new Color(0.13f, 0.15f, 0.19f, 1f);

    [Tooltip("토스트 패널 루트(배경 포함). 실패 사유 표시 중에만 켜진다 — 표시/숨김 토글 대상")]
    [SerializeField]
    private GameObject m_toastPanel;

    [Tooltip("스캔 실패·취소 사유 토스트 텍스트 (m_toastPanel 하위)")]
    [SerializeField]
    private TextMeshProUGUI m_toastText;

    [Tooltip("토스트 표시 유지 시간(초)")]
    [SerializeField]
    private float m_toastSeconds = 2f;

    private const string k_hudTable = "HudTable";
    private const string k_batteryKey = "Hud.Scan.Battery";
    private const string k_batteryLabelKey = "Hud.Scan.BatteryLabel";
    private const string k_chargedKey = "Hud.Scan.Charged";
    private const string k_lowBatteryKey = "Hud.Scan.LowBattery";

    private const string k_itemTable = "ItemTable";
    private const string k_feedbackPrefix = "Item.Feedback.";

    private PlayerInteractor m_interactor;
    private PlayerItemUser m_itemUser;
    private Scanner m_scanner;
    private IChargeable m_battery;

    private readonly HashSet<ulong> m_scannedNpcIds = new HashSet<ulong>();

    /// <summary>이 플레이어가 이미 스캔한 NPC인지 돌려준다.</summary>
    public bool HasScanned(ulong npcNetworkObjectId) => m_scannedNpcIds.Contains(npcNetworkObjectId);

    private ScanInfoView m_currentView;
    private CitizenIdentity m_currentIdentity;
    private ulong m_currentNpcId;
    private bool m_currentHasId;

    private NpcController m_currentController;
    private bool m_currentDeadState;

    private CancellationTokenSource m_toastCts;

    private int m_lastBattery = -1;

    private readonly HashSet<int> m_warnedMissingCard = new HashSet<int>();

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            if (m_uiRoot != null)
                m_uiRoot.SetActive(false);
            enabled = false;
            return;
        }

        if (m_uiRoot != null)
            m_uiRoot.SetActive(true);

        SetActive(m_batteryPanel, false);
        SetActive(m_toastPanel, false);

        LocalizationSettings.SelectedLocaleChanged += HandleLocaleChanged;

        m_interactor = GetComponentInParent<PlayerInteractor>();
        m_itemUser = GetComponentInParent<PlayerItemUser>();
        if (m_interactor == null || m_itemUser == null)
        {
            Debug.LogWarning(
                "ScanResultPresenter: PlayerInteractor/PlayerItemUser를 찾지 못함 — 스캔 표시 불가",
                this
            );
            return;
        }

        m_interactor.OnTargetChanged += HandleTargetChanged;
        m_itemUser.OnEquippedItemChanged += HandleEquippedItemChanged;
        HandleEquippedItemChanged(m_itemUser.EquippedItem);
    }

    public override void OnNetworkDespawn()
    {
        if (m_interactor != null)
            m_interactor.OnTargetChanged -= HandleTargetChanged;
        if (m_itemUser != null)
            m_itemUser.OnEquippedItemChanged -= HandleEquippedItemChanged;

        if (LocalizationSettings.HasSettings)
            LocalizationSettings.SelectedLocaleChanged -= HandleLocaleChanged;

        BindScanner(null);
        HideCurrent();
    }

    private void HandleLocaleChanged(Locale locale)
    {
        UpdateCurrentContent();

        if (m_battery != null)
            ApplyBatteryText(m_battery.CurrentBattery);
    }

    private void HandleTargetChanged(GameObject target)
    {
        HideCurrent();

        if (m_scanner == null)
            return;

        CitizenIdentity identity =
            target != null ? target.GetComponentInParent<CitizenIdentity>() : null;
        if (identity == null)
            return;

        ScanInfoView view = identity.GetComponentInChildren<ScanInfoView>(true);
        if (view == null)
        {
            WarnMissingCardOnce(identity);
            return;
        }

        NetworkObject npcObject = identity.GetComponentInParent<NetworkObject>();

        m_currentIdentity = identity;
        m_currentView = view;
        m_currentHasId = npcObject != null;
        m_currentNpcId = npcObject != null ? npcObject.NetworkObjectId : 0;

        m_currentController = identity.GetComponent<NpcController>();
        m_currentDeadState = m_currentController != null && m_currentController.Death.IsDead;

        if (m_interactor.AimCamera != null)
            m_currentView.SetCamera(m_interactor.AimCamera.transform);

        UpdateCurrentContent();
    }

    private void HandleEquippedItemChanged(ItemBase item)
    {
        BindScanner(item as Scanner);

        HandleTargetChanged(m_interactor != null ? m_interactor.CurrentTarget : null);
    }

    private void UpdateCurrentContent()
    {
        if (m_currentView == null)
            return;

        bool scanned =
            m_currentHasId
            && m_scannedNpcIds.Contains(m_currentNpcId)
            && m_currentIdentity.Profile != null;

        if (scanned)
        {
            CitizenProfile profile = m_currentIdentity.Profile;
            m_currentView.ShowReal(profile.m_nameView, profile.m_typeView, m_currentDeadState);
        }
        else
        {
            m_currentView.ShowMasked();
        }
    }

    private void Update()
    {
        if (m_currentController == null)
            return;

        bool dead = m_currentController.Death.IsDead;
        if (dead == m_currentDeadState)
            return;

        m_currentDeadState = dead;
        UpdateCurrentContent();
    }

    private void WarnMissingCardOnce(CitizenIdentity identity)
    {
        if (!m_warnedMissingCard.Add(identity.GetInstanceID()))
            return;

        Debug.LogWarning(
            $"ScanResultPresenter: {identity.name}에 스캔 카드(ScanInfoView)가 없어 스캔 정보를 표시할 수 없다 — "
                + "NPC 프리팹에 ScanInfoCard.prefab을 자식으로 넣을 것 (NPC_Citizen 참고)",
            identity
        );
    }

    private void HideCurrent()
    {
        if (m_currentView != null)
            m_currentView.Hide();

        m_currentView = null;
        m_currentIdentity = null;
        m_currentHasId = false;
        m_currentNpcId = 0;
        m_currentController = null;
    }

    private void BindScanner(Scanner scanner)
    {
        if (m_scanner == scanner)
            return;

        if (m_scanner != null)
        {
            m_scanner.OnScanCompleted -= HandleScanCompleted;
            if (m_battery != null)
                m_battery.OnCharged -= HandleBatteryChanged;
            m_scanner.OnScanFeedback -= HandleScanFeedback;
            m_scanner.OnDepletedUseAttempt -= HandleDepletedUseAttempt;
        }

        m_scanner = scanner;
        m_battery = scanner != null ? scanner.GetComponent<IChargeable>() : null;

        if (m_scanner != null)
        {
            m_scanner.OnScanCompleted += HandleScanCompleted;
            if (m_battery != null)
                m_battery.OnCharged += HandleBatteryChanged;
            m_scanner.OnScanFeedback += HandleScanFeedback;
            m_scanner.OnDepletedUseAttempt += HandleDepletedUseAttempt;
            m_lastBattery = -1;
            SetActive(m_batteryPanel, true);
            UpdateBattery(m_battery != null ? m_battery.CurrentBattery : 0);
        }
        else
        {
            SetActive(m_batteryPanel, false);
            HideToast();
            m_lastBattery = -1;
        }
    }

    private void HandleScanCompleted(CitizenProfile profile, ulong npcNetworkObjectId)
    {
        m_scannedNpcIds.Add(npcNetworkObjectId);

        if (m_currentHasId && m_currentNpcId == npcNetworkObjectId)
            UpdateCurrentContent();
    }

    private void HandleBatteryChanged(int current) => UpdateBattery(current);

    private void UpdateBattery(int current)
    {
        ApplyBatteryText(current);
        ApplyBatteryCells(current);

        bool charged = m_lastBattery >= 0 && current > m_lastBattery;
        m_lastBattery = current;

        if (charged)
            ShowToast(LocalizedStrings.Get(k_hudTable, k_chargedKey), transient: true);
    }

    private void ApplyBatteryText(int current)
    {
        if (m_batteryText == null || m_battery == null)
            return;

        int max = m_battery.MaxBattery;
        bool cellsCover = max > 0 && m_batteryCells != null && m_batteryCells.Length >= max;

        m_batteryText.text = cellsCover
            ? LocalizedStrings.Get(k_hudTable, k_batteryLabelKey)
            : LocalizedStrings.Get(k_hudTable, k_batteryKey, current, max);
    }

    private void ApplyBatteryCells(int current)
    {
        if (m_batteryCells == null || m_batteryCells.Length == 0)
            return;

        int used = m_battery != null ? Mathf.Min(m_battery.MaxBattery, m_batteryCells.Length) : 0;
        for (int i = 0; i < m_batteryCells.Length; i++)
        {
            Image cell = m_batteryCells[i];
            if (cell == null)
                continue;

            bool inUse = i < used;
            if (cell.gameObject.activeSelf != inUse)
                cell.gameObject.SetActive(inUse);

            if (inUse)
                cell.color = i < current ? m_cellFilledTone : m_cellEmptyTone;
        }
    }

    private void HandleDepletedUseAttempt() =>
        ShowToast(LocalizedStrings.Get(k_hudTable, k_lowBatteryKey), transient: true);

    private void HandleScanFeedback(EItemFeedback feedback) =>
        ShowToast(LocalizedStrings.Get(k_itemTable, k_feedbackPrefix + feedback), transient: true);

    private void ShowToast(string message, bool transient)
    {
        if (m_toastPanel == null || string.IsNullOrEmpty(message))
            return;

        m_toastCts?.Cancel();
        m_toastCts?.Dispose();
        m_toastCts = null;

        if (m_toastText != null)
            m_toastText.text = message;
        SetActive(m_toastPanel, true);

        if (transient)
        {
            m_toastCts = new CancellationTokenSource();
            HideAfterAsync(m_toastCts.Token).Forget();
        }
    }

    private void HideToast()
    {
        m_toastCts?.Cancel();
        m_toastCts?.Dispose();
        m_toastCts = null;
        SetActive(m_toastPanel, false);
    }

    private async UniTaskVoid HideAfterAsync(CancellationToken token)
    {
        try
        {
            await UniTask.Delay(TimeSpan.FromSeconds(m_toastSeconds), cancellationToken: token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        SetActive(m_toastPanel, false);
        m_toastCts?.Dispose();
        m_toastCts = null;
    }

    private static void SetActive(GameObject go, bool active)
    {
        if (go != null && go.activeSelf != active)
            go.SetActive(active);
    }
}
