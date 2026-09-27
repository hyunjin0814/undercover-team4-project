using System;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 스캐너 아이템 — 겨냥하고 좌클릭하면 대상 시민의 스캔 정보가 오너 화면에 뜬다(GDD 5-1).
/// 스캔 1회당 배터리 1을 소모하며, 판정·배터리는 서버 권위다.
/// </summary>
[RequireComponent(typeof(ItemBattery))]
[RequireComponent(typeof(ChannelGauge))]
[RequireComponent(typeof(ToastFeedback))]
[RequireComponent(typeof(OwnerFeedback))]
public class Scanner : ItemBase
{
    private OwnerFeedback m_feedback;

    private OwnerFeedback Feedback => this.ResolveCapability(ref m_feedback);

    private ChannelGauge m_gauge;
    private ToastFeedback m_toast;

    private ChannelGauge Gauge => this.ResolveCapability(ref m_gauge);

    private ToastFeedback Toast => this.ResolveCapability(ref m_toast);

    [Header("스캐너 설정")]
    [Tooltip("스캔 채널링 시간(초). 0이면 즉시 스캔 — 게이지·판독음 없이 겨냥 즉시 결과가 나온다 (#608). "
        + "0보다 크면 예전 홀드 채널링으로 돌아간다(되돌리기용)")]
    [Min(0f)]
    [SerializeField] private float m_channelSeconds;

    [Tooltip("채널링 도중 대상이 이 거리(m)를 벗어나면 스캔 실패로 처리한다 (#91)")]
    [SerializeField] private float m_scanKeepRange = 5f;

    [Tooltip("스캔 진행률이 이 지점(0~1)을 넘으면 대상이 반응한다 (#400). 즉시 스캔(채널링 0)에서는 진행률이 없어 결과 직전에 한 번 발화한다 (#608)")]
    [Range(0f, 1f)]
    [SerializeField] private float m_reactionPoint = 0.5f;

    private readonly ServerChannel m_channel = new();

    private bool m_pendingScan;

    private ItemBattery m_battery;

    public event Action<CitizenProfile, ulong> OnScanCompleted;

    public event Action<EItemFeedback> OnScanFeedback;

    public event Action OnDepletedUseAttempt;

    private void Awake()
    {
        ToastFeedback toast = Toast;
        if (toast != null)
            toast.OnToast += RaiseScanFeedback;

        m_battery = GetComponent<ItemBattery>();
        if (m_battery == null)
        {
            Debug.LogError("Scanner: ItemBattery가 프리팹에 없다 — 프리팹을 열어 추가하고 저장할 것", this);
            return;
        }

        m_battery.CanCharge = () => !m_channel.IsActive;
        m_battery.ChargeBlockedReason = "충전 실패 — 스캔 채널링 중";
        m_battery.FullyChargedFeedback = EItemFeedback.ScannerBatteryFull;
    }

    private void RaiseScanFeedback(EItemFeedback feedback) => OnScanFeedback?.Invoke(feedback);

    private readonly DeviceBlackoutGate m_blackout = new();

    private bool IsBlackout => m_blackout.IsActive;

    /// <summary>스캔 중이 아니고 배터리가 남았으며 먹통이 아닐 때 사용 가능하다(UI 힌트용).</summary>
    public override bool CanUse() =>
        !m_pendingScan && m_battery != null && !m_battery.IsDepleted && !IsBlackout;

    /// <summary>신원과 배정된 프로필이 있는 스캔 가능 대상인지 판정한다.</summary>
    public override bool CanTarget(GameObject aimTarget)
    {
        if (!CanUse())
            return false;
        if (aimTarget == null)
            return false;

        CitizenIdentity identity = aimTarget.GetComponentInParent<CitizenIdentity>();
        if (identity == null || identity.Profile == null)
            return false;

        return !IsAlreadyScanned(identity);
    }

    private PlayerInteractor m_scanLogHolder;
    private ScanResultPresenter m_scanLog;

    private ScanResultPresenter ScanLog
    {
        get
        {
            PlayerInteractor holder = Holder;
            if (holder == null)
                return null;
            if (holder != m_scanLogHolder)
            {
                m_scanLogHolder = holder;
                m_scanLog = holder.GetComponentInChildren<ScanResultPresenter>(true);
            }
            return m_scanLog;
        }
    }

    /// <summary>이 플레이어가 이미 스캔한 대상인지 확인한다.</summary>
    private bool IsAlreadyScanned(CitizenIdentity identity)
    {
        ScanResultPresenter log = ScanLog;
        if (log == null)
            return false;

        NetworkObject npcObject = identity.GetComponentInParent<NetworkObject>();
        return npcObject != null && log.HasScanned(npcObject.NetworkObjectId);
    }

    public override LocalizedString TargetPromptLabel(GameObject aimTarget) => InteractPrompts.Scan;

    /// <summary>스캔 요청을 서버로 전달한다. 판정·배터리 소모·결과 전파는 서버가 한다.</summary>
    public override void Use(GameObject target)
    {
        if (!CanUse())
        {
            if (IsBlackout)
                Toast?.ToastOwner(EItemFeedback.ScannerBlackout);
            else if (m_battery != null && m_battery.IsDepleted)
            {
                Debug.Log($"스캐너 배터리 부족! (남은 배터리: {m_battery.CurrentBattery})");
                OnDepletedUseAttempt?.Invoke();
            }

            return;
        }

        CitizenIdentity aimed =
            target != null ? target.GetComponentInParent<CitizenIdentity>() : null;
        if (aimed != null && IsAlreadyScanned(aimed))
        {
            Toast?.ToastOwner(EItemFeedback.AlreadyScanned);
            return;
        }

        if (!TryResolveScanTarget(target, out NetworkObjectReference npcRef))
            return;

        m_pendingScan = true;

        if (this.HasServerAuthority())
        {
            ServerBeginScan(npcRef);
            return;
        }

        if (!IsOwner)
        {
            m_pendingScan = false;
            return;
        }

        RequestScanRpc(npcRef);
    }

    private static bool TryResolveScanTarget(GameObject target, out NetworkObjectReference npcRef)
    {
        npcRef = default;

        if (target == null)
        {
            Debug.Log("스캔 실패: 겨냥된 대상 없음");
            return false;
        }

        CitizenIdentity identity = target.GetComponentInParent<CitizenIdentity>();
        if (identity == null)
        {
            Debug.Log($"스캔 실패: CitizenIdentity 없음 ({target.name})");
            return false;
        }

        if (identity.Profile == null)
        {
            Debug.Log($"스캔 실패: 프로필 미배정 — 서버 배정 결과가 이 클라이언트에 동기화되지 않음 ({target.name})");
            return false;
        }

        NetworkObject npcNetObj = identity.GetComponentInParent<NetworkObject>();
        if (npcNetObj == null)
        {
            Debug.Log($"스캔 실패: NPC에 NetworkObject 없음 ({target.name})");
            return false;
        }

        npcRef = new NetworkObjectReference(npcNetObj);
        return true;
    }

    private static bool TryResolveCitizen(
        NetworkObjectReference npcRef, out NetworkObject npcNetObj, out CitizenIdentity identity)
    {
        identity = null;
        return npcRef.TryGet(out npcNetObj)
            && npcNetObj.TryGetComponent(out identity)
            && identity.Profile != null;
    }

    [Rpc(SendTo.Server)]
    private void RequestScanRpc(NetworkObjectReference npcRef)
    {
        ServerBeginScan(npcRef);
    }

    /// <summary>스캔 요청을 서버에서 재검증하고 채널링을 시작한다.</summary>
    private void ServerBeginScan(NetworkObjectReference npcRef)
    {
        if (!this.HasServerAuthority())
            return;

        if (m_channel.IsActive || m_battery.IsDepleted)
        {
            ClearPendingRpc();
            return;
        }

        if (IsBlackout)
        {
            Toast?.ToastOwner(EItemFeedback.ScanFailedBlackout);
            ClearPendingRpc();
            return;
        }

        if (!TryResolveCitizen(npcRef, out _, out CitizenIdentity identity))
        {
            Debug.Log("스캔 실패(서버): NPC 참조 해석 실패 또는 프로필 없음");
            ClearPendingRpc();
            return;
        }

        ServerScanAsync(npcRef, identity).Forget();
    }

    private async UniTaskVoid ServerScanAsync(NetworkObjectReference npcRef, CitizenIdentity identity)
    {
        CitizenProfile profile = identity.Profile;

        if (m_channelSeconds <= 0f)
        {
            ServerTriggerScanReaction(identity);
        }
        else
        {
            Feedback?.NotifyOwner($"스캔 채널링 시작: {identity.name} ({m_channelSeconds}초)");
            Gauge?.Begin(m_channelSeconds, EAudioClip.ScannerScan);

            ServerChannel.Result result;
            try
            {
                result = await m_channel.RunAsync(
                    m_channelSeconds,
                    () => identity != null && IsInRange(identity.transform) && !IsBlackout,
                    m_reactionPoint,
                    () => ServerTriggerScanReaction(identity));
            }
            finally
            {
                Gauge?.End();
            }

            switch (result)
            {
                case ServerChannel.Result.OutOfRange:
                    Toast?.ToastOwner(
                        IsBlackout
                            ? EItemFeedback.ScanStoppedBlackout
                            : EItemFeedback.ScanFailedOutOfRange);
                    ClearPendingRpc();
                    return;

                case ServerChannel.Result.Canceled:
                    Feedback?.NotifyOwner("스캔 취소됨 (홀드 뗌)");
                    ClearPendingRpc();
                    return;
            }
        }

        m_battery.ServerConsume();
        Debug.Log($"[서버] NPC 스캔됨: {GetScanInfo(profile)}. 남은 배터리: {m_battery.CurrentBattery}");

        ScanResultRpc(npcRef);
    }

    private void ServerTriggerScanReaction(CitizenIdentity identity)
    {
        if (identity == null)
            return;

        NpcController npc = identity.GetComponent<NpcController>();
        if (npc == null)
            return;

        PlayerInteractor interactor = Holder;
        npc.Reaction.ServerReactTo(
            ReactionTrigger.Scan,
            interactor != null ? interactor.transform : null
        );
    }

    [Rpc(SendTo.Owner)]
    private void ScanResultRpc(NetworkObjectReference npcRef)
    {
        m_pendingScan = false;

        if (!TryResolveCitizen(npcRef, out NetworkObject npcNetObj, out CitizenIdentity identity))
        {
            Debug.LogWarning("ScanResultRpc: NPC 참조 해석 실패 또는 프로필 없음 (클라 동기화 지연?)");
            return;
        }

        App.Sound?.PlaySfx2D(EAudioClip.ScannerScan);

        Debug.Log($"스캔 결과 수신: {GetScanInfo(identity.Profile)}");
        OnScanCompleted?.Invoke(identity.Profile, npcNetObj.NetworkObjectId);
    }

    [Rpc(SendTo.Owner)]
    private void ClearPendingRpc()
    {
        m_pendingScan = false;
    }

    /// <summary>좌클릭 뗌 — 진행 중인 스캔 채널링 취소를 서버에 요청한다.</summary>
    public override void CancelUse() => CancelScan();

    /// <summary>버리기 등 소유권 이전 경로에서 서버가 직접 스캔 채널을 끊는다 (ItemBase 훅).</summary>
    public override void ServerCancelActiveUse() => m_channel.Cancel();

    /// <summary>진행 중인 스캔 채널링을 취소한다. (이동·피격 등 방해 시 호출)</summary>
    public void CancelScan()
    {
        if (m_channelSeconds <= 0f)
            return;

        if (this.HasServerAuthority())
        {
            m_channel.Cancel();
            return;
        }

        if (IsOwner)
            RequestCancelScanRpc();
    }

    [Rpc(SendTo.Server)]
    private void RequestCancelScanRpc()
    {
        m_channel.Cancel();
    }

    private bool IsInRange(Transform target) =>
        PlayerInteractor.IsWithinReach(Holder, target, m_scanKeepRange, transform.position);

    private static string GetScanInfo(CitizenProfile profile)
    {
        if (profile == null)
            return "대상 정보 없음";

        return $"이름={profile.CitizenName}, 타입={profile.m_typeView}, 세력={profile.m_factionView}";
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
            m_channel.Cancel();

        m_pendingScan = false;
    }

    private void OnDisable()
    {
        CancelScan();
        m_pendingScan = false;
    }

    public override void OnDestroy()
    {
        m_channel.Dispose();
        base.OnDestroy();
    }
}
