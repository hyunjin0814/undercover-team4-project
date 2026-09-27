using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;
using Random = UnityEngine.Random;

/// <summary>
/// 본부 해킹 복구 단말 — 먹통 중에만 켜지고, 화면의 복구 코드를 입력하면 DeviceBlackoutEvent가 풀린다.
/// 코드 생성·판정·제한시간·자리 배정은 서버 권위다.
/// </summary>
public class BlackoutRecoveryTerminal : NetworkBehaviour, IInteractable
{
    public const int k_codeDigits = 4;

    private const int k_noCode = -1;
    private const double k_noDeadline = -1d;
    private const ulong k_noUser = ulong.MaxValue;

    private const double k_submitCooldown = 0.2d;

    [Header("화면")]
    [Tooltip("해킹 중에만 켜지는 화면 루트 — 비워 두면 화면 없이 동작한다")]
    [SerializeField] private GameObject m_screenRoot;

    [Header("제한시간")]
    [Tooltip("코드 하나가 유효한 시간(초). 넘기면 서버가 새 코드를 뽑고 입력이 초기화된다")]
    [Min(1f)]
    [SerializeField] private float m_codeSeconds = 10f;

    [Header("카메라 포커스")]
    [Tooltip("상호작용하면 카메라가 이 자리로 옮겨 간다 — 비우면 포커스 없이 동작한다")]
    [SerializeField] private Transform m_focusPoint;

    private readonly NetworkVariable<int> m_codeSynced = new NetworkVariable<int>(k_noCode);
    private int m_code = k_noCode;

    private readonly NetworkVariable<double> m_deadlineSynced = new NetworkVariable<double>(k_noDeadline);
    private double m_deadline = k_noDeadline;

    private readonly NetworkVariable<ulong> m_userSynced = new NetworkVariable<ulong>(k_noUser);
    private ulong m_user = k_noUser;

    private PlayerTerminalFocus m_pendingFocus;
    private readonly Dictionary<ulong, double> m_lastSubmit = new Dictionary<ulong, double>();

    private double Now => IsSpawned && NetworkManager != null ? NetworkManager.ServerTime.Time : Time.timeAsDouble;
    private ulong LocalId => IsSpawned && NetworkManager != null ? NetworkManager.LocalClientId : 0ul;

    public int Code => IsSpawned && !IsServer ? m_codeSynced.Value : m_code;

    public float CodeSeconds => m_codeSeconds;

    public float RemainingSeconds =>
        (float)Math.Max(0d, (IsSpawned && !IsServer ? m_deadlineSynced.Value : m_deadline) - Now);

    public ulong User => IsSpawned && !IsServer ? m_userSynced.Value : m_user;

    public bool IsUsedByOther => User != k_noUser && User != LocalId;

    public bool IsLocalFocused { get; private set; }

    public event Action<int> OnCodeChanged;

    public bool IsOnline => Blackout != null && Blackout.IsCommsBlackout;

    public Transform FocusPoint => m_focusPoint;

    private DeviceBlackoutEvent Blackout => App.Game.SuddenEvent?.GetEvent<DeviceBlackoutEvent>();

    public override void OnNetworkSpawn()
    {
        m_codeSynced.OnValueChanged += HandleCodeSyncedChanged;
        m_userSynced.OnValueChanged += HandleUserSyncedChanged;

        if (IsServer && NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback += HandleClientDisconnect;

        if (Code != k_noCode)
            OnCodeChanged?.Invoke(Code);

        ApplyScreen();
    }

    public override void OnNetworkDespawn()
    {
        m_codeSynced.OnValueChanged -= HandleCodeSyncedChanged;
        m_userSynced.OnValueChanged -= HandleUserSyncedChanged;

        if (IsServer && NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback -= HandleClientDisconnect;
    }

    private void HandleUserSyncedChanged(ulong previous, ulong current) => ApplySeat();

    private void HandleClientDisconnect(ulong clientId) => ServerRelease(clientId);

    private void HandleCodeSyncedChanged(int previous, int current)
    {
        if (IsServer)
            return;

        OnCodeChanged?.Invoke(current);
    }

    private void Start()
    {
        DeviceBlackoutEvent blackout = Blackout;
        if (blackout == null)
            return;

        blackout.OnCommsBlackoutChanged += HandleBlackoutChanged;
        ApplyScreen();
    }

    public override void OnDestroy()
    {
        DeviceBlackoutEvent blackout = Blackout;
        if (blackout != null)
            blackout.OnCommsBlackoutChanged -= HandleBlackoutChanged;

        base.OnDestroy();
    }

    private void HandleBlackoutChanged(bool active)
    {
        if (!IsSpawned || IsServer)
        {
            SetCode(active ? NewCode() : k_noCode);

            if (!active)
                SetUser(k_noUser);
        }

        ApplyScreen();
    }

    /// <summary>해킹 중에만 상호작용이 뜬다.</summary>
    public bool CanInteract(GameObject interactor) => IsOnline;

    /// <summary>조준 안내 — 화면 앞에서 표시를 내리는 일은 <see cref="InteractionFeedback"/>이 한다.</summary>
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.BlackoutRecovery;

    /// <summary>다른 사람이 사용 중이면 막힌 사유를 돌려준다.</summary>
    public LocalizedString BlockedReason(GameObject interactor) =>
        IsUsedByOther ? InteractPrompts.ReasonInUse : null;

    /// <summary>E 상호작용 — 자리를 잡고 화면 앞으로 간다. 오너 클라에서만 불린다.</summary>
    public void Interact(GameObject interactor)
    {
        if (!IsOnline || interactor == null)
            return;

        PlayerTerminalFocus focus = interactor.GetComponent<PlayerTerminalFocus>();
        if (focus == null)
        {
            Debug.LogWarning("BlackoutRecoveryTerminal: 상호작용자에게 PlayerTerminalFocus가 없어 화면 포커스를 건너뛴다", this);
            return;
        }

        if (IsLocalFocused)
        {
            focus.Release();
            return;
        }

        if (IsUsedByOther)
            return;

        m_pendingFocus = focus;

        if (!IsSpawned || IsServer)
            ServerClaim(LocalId);
        else
            RequestClaimRpc();
    }

    private void ApplySeat()
    {
        if (User != LocalId)
        {
            m_pendingFocus = null;
            return;
        }

        if (m_pendingFocus == null)
            return;

        PlayerTerminalFocus focus = m_pendingFocus;
        m_pendingFocus = null;
        focus.Begin(this);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestClaimRpc(RpcParams rpcParams = default) => ServerClaim(rpcParams.Receive.SenderClientId);

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestReleaseRpc(RpcParams rpcParams = default) => ServerRelease(rpcParams.Receive.SenderClientId);

    private void ServerClaim(ulong client)
    {
        if (IsSpawned && !IsServer)
            return;

        if (!IsOnline || (m_user != k_noUser && m_user != client))
            return;

        SetUser(client);
    }

    private void ServerRelease(ulong client)
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_user != client)
            return;

        SetUser(k_noUser);
    }

    private void SetUser(ulong value)
    {
        if (m_user == value)
            return;

        m_user = value;
        if (IsSpawned && IsServer)
            m_userSynced.Value = value;

        ApplySeat();
    }

    /// <summary>로컬 플레이어가 단말 앞에 앉았는지 알린다. 나갈 때 자리도 반납한다.</summary>
    public void SetLocalFocused(bool focused)
    {
        IsLocalFocused = focused;
        if (focused)
            return;

        m_pendingFocus = null;
        if (User != LocalId)
            return;

        if (!IsSpawned || IsServer)
            ServerRelease(LocalId);
        else
            RequestReleaseRpc();
    }

    /// <summary>입력한 코드를 제출한다 — 판정은 서버가 다시 하므로 여기서 미리 풀지 않는다.</summary>
    public void SubmitCode(int code)
    {
        if (!IsSpawned || IsServer)
        {
            ServerSubmit(code, LocalId);
            return;
        }

        RequestSubmitRpc(code);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestSubmitRpc(int code, RpcParams rpcParams = default) =>
        ServerSubmit(code, rpcParams.Receive.SenderClientId);

    private void ServerSubmit(int code, ulong sender)
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_lastSubmit.TryGetValue(sender, out double last) && Now - last < k_submitCooldown)
            return;

        m_lastSubmit[sender] = Now;

        DeviceBlackoutEvent blackout = Blackout;
        if (blackout == null || !blackout.IsCommsBlackout)
            return;

        if (code != m_code)
        {
            SetCode(NewCode());
            return;
        }

        if (blackout.ServerRecover())
            SetCode(k_noCode);
    }

    /// <summary>제한시간을 감시해 넘기면 코드를 재발급한다. 서버(또는 오프라인) 전용.</summary>
    private void Update()
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_code == k_noCode || m_deadline < 0d || Now < m_deadline)
            return;

        SetCode(NewCode());
    }

    private static int NewCode()
    {
        int max = 1;
        for (int i = 0; i < k_codeDigits; i++)
            max *= 10;

        return Random.Range(0, max);
    }

    private void SetCode(int value)
    {
        SetDeadline(value == k_noCode ? k_noDeadline : Now + m_codeSeconds);

        if (m_code == value)
            return;

        m_code = value;
        if (IsSpawned && IsServer)
            m_codeSynced.Value = value;

        OnCodeChanged?.Invoke(value);
    }

    private void SetDeadline(double value)
    {
        m_deadline = value;
        if (IsSpawned && IsServer)
            m_deadlineSynced.Value = value;
    }

    private void ApplyScreen()
    {
        if (m_screenRoot != null)
            m_screenRoot.SetActive(IsOnline);
    }
}
