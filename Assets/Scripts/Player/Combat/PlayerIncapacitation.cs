using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

public enum IncapacitationCause
{
    None,

    Down,
    Penalty,
    Stun,
    Die,
    Abducted,
    Beamed,

    Launched,
}

/// <summary>
/// 플레이어 무력화 상태를 서버 권위로 관리하는 공통 기반 — 다운·사망·매달기·기절·납치 등.
/// 원인(Cause)에 따라 복구 방식이 다르며, 다른 컴포넌트는 IsIncapacitated를 읽어 행동을 막는다.
/// </summary>
public class PlayerIncapacitation : NetworkBehaviour
{
    [SerializeField]
    private GameObject m_reviveHitbox;

    [Header("다운 유예 (#725)")]
    [Tooltip(
        "다운 상태로 이 시간(초)이 지나면 Die(기능 정지)로 전환된다 — 동료가 맨손으로 구조할 수 있는 제한시간"
    )]
    [SerializeField]
    private float m_dieAfterDownSeconds = 60f;

    public float DieAfterDownSeconds => m_dieAfterDownSeconds;

    [Header("소유권 이관 (#957)")]
    [Tooltip(
        "래그돌이 도는 중에 죽었을 때, 미뤄 둔 소유권 이관의 서버 상한(초) — 오너의 정착 통보가 "
            + "안 오는 경우(연결 끊김·맵 밖 낙하)의 안전장치. PlayerRagdoll의 정착 타임아웃보다 넉넉히 잡을 것"
    )]
    [SerializeField]
    private float m_ownershipHandoverMaxSeconds = 8f;

    private readonly NetworkVariable<IncapacitationCause> m_causeSynced =
        new NetworkVariable<IncapacitationCause>();

    private IncapacitationCause m_cause;

    private readonly NetworkVariable<bool> m_bodyLostSynced = new NetworkVariable<bool>();
    private bool m_bodyLost;

    private ulong m_ownerBeforeDeath;
    private bool m_ownershipMovedToServer;

    private bool m_ownershipHandoverPending;

    private int m_handoverEpisode;

    internal bool IsOwnershipHandoverPending => m_ownershipHandoverPending;

    internal ulong BodyOwnerClientId =>
        m_ownershipMovedToServer ? m_ownerBeforeDeath : OwnerClientId;

    private int m_stunEpisode;

    private int m_launchEpisode;

    private readonly NetworkVariable<double> m_stunDeadlineSynced = new NetworkVariable<double>();
    private double m_stunDeadline;

    private double CurrentTime =>
        IsSpawned && NetworkManager != null ? NetworkManager.ServerTime.Time : Time.timeAsDouble;

    private int m_causeEpisode;

    private readonly NetworkVariable<double> m_downDeadlineSynced = new NetworkVariable<double>();
    private double m_downDeadline;

    private readonly NetworkVariable<float> m_downFrozenRemainingSynced =
        new NetworkVariable<float>();
    private float m_downFrozenRemaining;

    private readonly NetworkVariable<double> m_reviveEndSynced = new NetworkVariable<double>();
    private double m_reviveEnd;

    public IncapacitationCause Cause => IsSpawned && !IsServer ? m_causeSynced.Value : m_cause;

    public bool IsIncapacitated => Cause != IncapacitationCause.None;

    public bool IsDowned => Cause == IncapacitationCause.Down;

    public bool IsDead => Cause == IncapacitationCause.Die;

    public bool IsBodyLost => IsSpawned && !IsServer ? m_bodyLostSynced.Value : m_bodyLost;

    public bool IsRevivable => IsDead && !IsBodyLost;

    private int m_downCount;
    public int DownCount => m_downCount;

    public bool IsOutOfAction => IsDowned || IsDead;

    public bool IsAimTargetable => IsRagdollCause && !IsBodyLost;

    public bool IsRagdollCause => IsOutOfAction || IsLaunched || IsBeamed;

    public bool IsBeamed => Cause == IncapacitationCause.Beamed;

    public bool IsStunned => Cause == IncapacitationCause.Stun;

    public bool IsLaunched => Cause == IncapacitationCause.Launched;

    public float RemainingStunSeconds
    {
        get
        {
            if (!IsStunned)
                return 0f;

            double deadline = IsSpawned && !IsServer ? m_stunDeadlineSynced.Value : m_stunDeadline;
            return Mathf.Max(0f, (float)(deadline - CurrentTime));
        }
    }

    public float RemainingUntilDie
    {
        get
        {
            if (!IsDowned)
                return 0f;

            if (IsBeingRevived)
                return IsSpawned && !IsServer
                    ? m_downFrozenRemainingSynced.Value
                    : m_downFrozenRemaining;

            double deadline = IsSpawned && !IsServer ? m_downDeadlineSynced.Value : m_downDeadline;
            return Mathf.Max(0f, (float)(deadline - CurrentTime));
        }
    }

    public bool IsBeingRevived => RemainingReviveSeconds > 0f;

    public float RemainingReviveSeconds
    {
        get
        {
            double end = IsSpawned && !IsServer ? m_reviveEndSynced.Value : m_reviveEnd;
            return Mathf.Max(0f, (float)(end - CurrentTime));
        }
    }

    public bool IsProne => IsIncapacitated;

    private static readonly List<PlayerIncapacitation> s_instances = new();

    public static IReadOnlyList<PlayerIncapacitation> All => s_instances;

    private PlayerEscorter m_escorter;

    private PlayerEscorter Escorter
    {
        get
        {
            if (m_escorter == null)
                m_escorter = GetComponent<PlayerEscorter>();
            return m_escorter;
        }
    }

    private PlayerRagdoll m_ragdoll;

    private PlayerRagdoll Ragdoll
    {
        get
        {
            if (m_ragdoll == null)
                m_ragdoll = GetComponent<PlayerRagdoll>();
            return m_ragdoll;
        }
    }

    private void OnEnable() => s_instances.Add(this);

    private void OnDisable() => s_instances.Remove(this);

    public event Action<bool> OnIncapacitatedChanged;

    public static event Action OnAnyIncapacitatedChanged;

    public override void OnNetworkSpawn()
    {
        m_causeSynced.OnValueChanged += HandleSyncedChanged;
        m_bodyLostSynced.OnValueChanged += HandleBodyLostSyncedChanged;

        RefreshAimHitbox();
    }

    public override void OnNetworkDespawn()
    {
        m_causeSynced.OnValueChanged -= HandleSyncedChanged;
        m_bodyLostSynced.OnValueChanged -= HandleBodyLostSyncedChanged;
    }

    private void HandleSyncedChanged(IncapacitationCause previous, IncapacitationCause current)
    {
        if (IsServer)
            return;

        RefreshAimHitbox();

        bool was = previous != IncapacitationCause.None;
        bool now = current != IncapacitationCause.None;
        if (was != now)
            OnIncapacitatedChanged?.Invoke(now);
    }

    private void HandleBodyLostSyncedChanged(bool previous, bool current)
    {
        if (IsServer)
            return;

        RefreshAimHitbox();
    }

    private void RefreshAimHitbox()
    {
        if (m_reviveHitbox != null)
            m_reviveHitbox.SetActive(IsAimTargetable);
    }

    /// <summary>원인을 지정해 무력화 상태로 진입시킨다. 서버(또는 오프라인) 전용.</summary>
    public void Incapacitate(IncapacitationCause cause)
    {
        if (IsSpawned && !IsServer)
            return;

        if (cause == IncapacitationCause.None)
        {
            Debug.LogWarning(
                "PlayerIncapacitation: 원인 None으로는 무력화할 수 없다 — 해제는 Recover()",
                this
            );
            return;
        }

        if (Cause == IncapacitationCause.Die || Cause == IncapacitationCause.Down)
        {
            Debug.Log(
                $"[다운/Die] 무력화 덮어쓰기 무시 — {name}은 이미 쓰러져 있다 (요청 원인: {cause})",
                this
            );
            return;
        }

        SetCause(cause);
    }

    /// <summary>seconds 동안 기절시킨다. 이미 무력화된 대상은 무시한다. 서버 전용.</summary>
    public void ServerStun(float seconds)
    {
        if (IsSpawned && !IsServer)
            return;
        if (IsIncapacitated)
            return;

        SetCause(IncapacitationCause.Stun);
        SetStunDeadline(CurrentTime + seconds);
        ServerStunTimerAsync(seconds, ++m_stunEpisode).Forget();
    }

    /// <summary>홈런 진압봉에 맞아 비행 상태로 진입시킨다. 정착 통보가 없으면 maxSeconds 뒤 안전장치가 푼다.</summary>
    public void ServerLaunch(float maxSeconds)
    {
        if (IsSpawned && !IsServer)
            return;
        if (IsIncapacitated)
            return;

        SetCause(IncapacitationCause.Launched);
        ServerLaunchTimeoutAsync(maxSeconds, ++m_launchEpisode).Forget();
    }

    /// <summary>오너가 비행 정착을 서버에 통보한다. 원인이 여전히 비행일 때만 해제한다.</summary>
    public void RequestLaunchSettled()
    {
        if (!IsSpawned || IsServer)
        {
            ServerRecoverFromLaunch();
            return;
        }
        if (!IsOwner)
            return;

        LaunchSettledRpc();
    }

    [Rpc(SendTo.Server)]
    private void LaunchSettledRpc() => ServerRecoverFromLaunch();

    private void ServerRecoverFromLaunch()
    {
        if (m_cause != IncapacitationCause.Launched)
            return;

        Recover();
    }

    /// <summary>오너가 정착을 통보하면 미뤄 둔 사망 소유권 이관을 실행한다. 미룬 것이 없으면 무동작.</summary>
    public void RequestDeathSettled()
    {
        if (!IsSpawned || IsServer)
        {
            ServerCompleteOwnershipHandover();
            return;
        }
        if (!IsOwner)
            return;

        DeathSettledRpc();
    }

    [Rpc(SendTo.Server)]
    private void DeathSettledRpc() => ServerCompleteOwnershipHandover();

    internal void ServerCompleteOwnershipHandover()
    {
        if (IsSpawned && !IsServer)
            return;
        if (!m_ownershipHandoverPending)
            return;

        m_ownershipHandoverPending = false;
        m_handoverEpisode++;

        if (!IsSpawned || NetworkObject == null || NetworkManager == null)
            return;
        if (m_ownerBeforeDeath == NetworkManager.ServerClientId)
            return;

        m_ownershipMovedToServer = true;
        NetworkObject.ChangeOwnership(NetworkManager.ServerClientId);
    }

    private void CancelPendingHandover()
    {
        if (!m_ownershipHandoverPending)
            return;

        m_ownershipHandoverPending = false;
        m_handoverEpisode++;
    }

    private bool ShouldDeferHandover()
    {
        PlayerRagdoll ragdoll = Ragdoll;
        return ragdoll != null && ragdoll.IsRagdollActive && !ragdoll.IsSettled;
    }

    private async UniTaskVoid ServerOwnershipHandoverTimeoutAsync(float seconds, int episode)
    {
        try
        {
            await UniTask.Delay(
                TimeSpan.FromSeconds(seconds),
                cancellationToken: destroyCancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (episode != m_handoverEpisode)
            return;

        Debug.LogWarning(
            $"[소유권] 정착 통보가 안 와 상한({seconds}초)으로 이관한다 — {name}",
            this
        );
        ServerCompleteOwnershipHandover();
    }

    private async UniTaskVoid ServerLaunchTimeoutAsync(float seconds, int episode)
    {
        try
        {
            await UniTask.Delay(
                TimeSpan.FromSeconds(seconds),
                cancellationToken: destroyCancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (episode != m_launchEpisode || m_cause != IncapacitationCause.Launched)
            return;

        Recover();
    }

    /// <summary>몸이 회수 불가능한 곳으로 사라졌을 때 기능 정지(Die)로 확정한다. 서버(또는 오프라인) 전용.</summary>
    public void ServerKillByBodyLost()
    {
        if (IsSpawned && !IsServer)
            return;

        SetBodyLost(true);

        if (Cause != IncapacitationCause.Die)
        {
            Debug.Log($"[몸 소실] 결말 — 기능 정지: {name}", this);
            SetCause(IncapacitationCause.Die);
        }

        ServerCompleteOwnershipHandover();
    }

    /// <summary>유예 중인 몸을 즉시 완전 사망으로 — 유예 중 확인사살(환경·NPC·아군 진압봉 모두). 서버(또는 오프라인) 전용.</summary>
    public void ServerFinishOff()
    {
        if (IsSpawned && !IsServer)
            return;
        if (!IsDowned)
            return;

        Debug.Log($"[Die] 유예 확인사살 — 기능 정지: {name}", this);
        SetCause(IncapacitationCause.Die);
    }

    /// <summary>구조 채널링 시작 시 Die 타이머를 멈추고, 종료 시 남은 시간으로 다시 건다. 서버(또는 오프라인) 전용.</summary>
    public void ServerSetBeingRevived(bool active, float channelSeconds = 0f)
    {
        if (IsSpawned && !IsServer)
            return;
        if (!IsDowned)
            return;

        if (active)
        {
            m_downFrozenRemaining = RemainingUntilDie;
            SetDownFrozenRemaining(m_downFrozenRemaining);
            SetReviveEnd(CurrentTime + channelSeconds);
            m_causeEpisode++;
        }
        else
        {
            SetDownDeadline(CurrentTime + m_downFrozenRemaining);
            SetReviveEnd(0d);
            ServerDieTimerAsync(m_downFrozenRemaining, ++m_causeEpisode).Forget();
        }
    }

    /// <summary>무력화 해제(부활·복구) — 서버(또는 오프라인)에서만.</summary>
    public void Recover()
    {
        if (IsSpawned && !IsServer)
            return;
        SetCause(IncapacitationCause.None);
    }

    /// <summary>라운드 사이 초기화 — 다운 횟수를 비운다. PlayerHealth.ServerResetState가 부른다.</summary>
    public void ServerResetRound()
    {
        m_downCount = 0;
    }

    private async UniTaskVoid ServerStunTimerAsync(float seconds, int episode)
    {
        try
        {
            await UniTask.Delay(
                TimeSpan.FromSeconds(seconds),
                cancellationToken: destroyCancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (m_stunEpisode != episode || m_cause != IncapacitationCause.Stun)
            return;

        Recover();
    }

    private async UniTaskVoid ServerDieTimerAsync(float seconds, int episode)
    {
        try
        {
            await UniTask.Delay(
                TimeSpan.FromSeconds(seconds),
                cancellationToken: destroyCancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (m_causeEpisode != episode || m_cause != IncapacitationCause.Down)
            return;

        Debug.Log($"[Die] 유예 시간 경과 — 기능 정지: {name} (부활 키트만 남는다)", this);
        SetCause(IncapacitationCause.Die);
    }

    private void SetCause(IncapacitationCause cause)
    {
        if (m_cause == cause)
            return;

        bool was = IsIncapacitated;
        m_cause = cause;
        if (IsSpawned && IsServer)
            m_causeSynced.Value = cause;

        if (!was && (cause == IncapacitationCause.Down || cause == IncapacitationCause.Die))
            m_downCount++;

        ApplyDeathOwnership(cause);

        if (cause != IncapacitationCause.Stun)
        {
            SetStunDeadline(0d);
        }

        if (cause != IncapacitationCause.Die)
        {
            SetBodyLost(false);
        }

        m_causeEpisode++;
        if (cause == IncapacitationCause.Down)
        {
            SetDownDeadline(CurrentTime + m_dieAfterDownSeconds);
            SetReviveEnd(0d);
            ServerDieTimerAsync(m_dieAfterDownSeconds, m_causeEpisode).Forget();
        }
        else
        {
            SetDownDeadline(0d);
            SetReviveEnd(0d);
        }

        if (!was && IsIncapacitated)
            Escorter?.ReleaseAllDrags();

        RefreshAimHitbox();
        if (was != IsIncapacitated)
            OnIncapacitatedChanged?.Invoke(IsIncapacitated);
        OnAnyIncapacitatedChanged?.Invoke();
    }

    /// <summary>사망 중 이 NetworkObject의 소유권을 서버로 옮기고, 풀리면 원래 오너에게 돌려준다.</summary>
    private void ApplyDeathOwnership(IncapacitationCause cause)
    {
        if (!IsSpawned || !IsServer || NetworkObject == null || NetworkManager == null)
            return;

        bool wantsServerOwner =
            cause == IncapacitationCause.Die || cause == IncapacitationCause.Down;

        if (!wantsServerOwner)
            CancelPendingHandover();

        if (wantsServerOwner == m_ownershipMovedToServer)
            return;

        if (wantsServerOwner)
        {
            if (m_ownershipHandoverPending)
                return;

            m_ownerBeforeDeath = OwnerClientId;
            if (m_ownerBeforeDeath == NetworkManager.ServerClientId)
                return;

            if (ShouldDeferHandover())
            {
                m_ownershipHandoverPending = true;
                ServerOwnershipHandoverTimeoutAsync(
                        m_ownershipHandoverMaxSeconds,
                        ++m_handoverEpisode
                    )
                    .Forget();
                return;
            }

            m_ownershipMovedToServer = true;
            NetworkObject.ChangeOwnership(NetworkManager.ServerClientId);
            return;
        }

        m_ownershipMovedToServer = false;

        if (!NetworkManager.ConnectedClients.ContainsKey(m_ownerBeforeDeath))
            return;

        NetworkObject.ChangeOwnership(m_ownerBeforeDeath);
    }

    private void SetStunDeadline(double deadline)
    {
        m_stunDeadline = deadline;
        if (IsSpawned && IsServer)
            m_stunDeadlineSynced.Value = deadline;
    }

    private void SetBodyLost(bool value)
    {
        m_bodyLost = value;
        if (IsSpawned && IsServer)
            m_bodyLostSynced.Value = value;
        RefreshAimHitbox();
    }

    private void SetDownDeadline(double deadline)
    {
        m_downDeadline = deadline;
        if (IsSpawned && IsServer)
            m_downDeadlineSynced.Value = deadline;
    }

    private void SetDownFrozenRemaining(float remaining)
    {
        m_downFrozenRemaining = remaining;
        if (IsSpawned && IsServer)
            m_downFrozenRemainingSynced.Value = remaining;
    }

    private void SetReviveEnd(double end)
    {
        m_reviveEnd = end;
        if (IsSpawned && IsServer)
            m_reviveEndSynced.Value = end;
    }
}
