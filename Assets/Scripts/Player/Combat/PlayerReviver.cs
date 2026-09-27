using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 다운된 동료 구조 — E 탭으로 서버 권위 채널링을 돌려 완료 시 대상을 부활시킨다(GDD 7-5).
/// 이동·아이템 사용·슬롯 전환·E 재입력 시 취소되며, 채널링 동안 대상의 다운 시계를 멈춘다.
/// </summary>
[RequireComponent(typeof(PlayerInputHandler))]
[RequireComponent(typeof(ChannelGauge))]
[RequireComponent(typeof(OwnerFeedback))]
public class PlayerReviver : NetworkBehaviour
{
    private OwnerFeedback m_feedback;

    private OwnerFeedback Feedback => this.ResolveCapability(ref m_feedback);

    private ChannelGauge m_gauge;

    private ChannelGauge Gauge => this.ResolveCapability(ref m_gauge);

    [Header("구조 채널링 (서버 권위)")]
    [Tooltip("구조 채널링 시간(초)")]
    [SerializeField]
    private float m_reviveSeconds = 3f;

    private const float k_fallbackRange = 3f;

    private PlayerInputHandler m_inputHandler;
    private PlayerInteractor m_interactor;
    private PlayerIncapacitation m_incapacitation;
    private PlayerHealth m_selfHealth;

    private readonly ServerChannel m_channel = new();

    private bool m_isChanneling;

    private readonly NetworkVariable<bool> m_isChannelingSynced = new NetworkVariable<bool>();

    public bool IsChanneling =>
        IsSpawned && !IsServer ? m_isChannelingSynced.Value : m_channel.IsActive;

    public PlayerHealth CurrentReviveTarget =>
        IsOwner ? FindAllyTarget(IncapacitationCause.Down) : null;

    public PlayerHealth CurrentDeadTarget =>
        IsOwner ? FindAllyTarget(IncapacitationCause.Die) : null;

    public override void OnNetworkSpawn()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_interactor = GetComponent<PlayerInteractor>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_selfHealth = GetComponent<PlayerHealth>();

        if (IsOwner)
        {
            m_inputHandler.OnInteractStarted += HandleInteractStarted;
            m_inputHandler.OnUseItemStarted += HandleCancelTrigger;
            m_inputHandler.OnDropItem += HandleCancelTrigger;
            m_inputHandler.OnPreviousItem += HandleCancelTrigger;
            m_inputHandler.OnNextItem += HandleCancelTrigger;
            m_inputHandler.OnSelectSlot += HandleSelectSlot;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner)
        {
            m_inputHandler.OnInteractStarted -= HandleInteractStarted;
            m_inputHandler.OnUseItemStarted -= HandleCancelTrigger;
            m_inputHandler.OnDropItem -= HandleCancelTrigger;
            m_inputHandler.OnPreviousItem -= HandleCancelTrigger;
            m_inputHandler.OnNextItem -= HandleCancelTrigger;
            m_inputHandler.OnSelectSlot -= HandleSelectSlot;
        }
        ServerCancelRevive();
    }

    private void Update()
    {
        if (!IsOwner || !m_isChanneling)
            return;

        if (m_inputHandler != null && m_inputHandler.MoveInput.sqrMagnitude > 0.0001f)
            HandleCancelTrigger();
    }

    private void HandleInteractStarted()
    {
        if (m_isChanneling)
        {
            RequestCancelRevive();
            return;
        }

        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return;

        PlayerHealth target = FindAllyTarget(IncapacitationCause.Down);
        if (target != null)
            RequestBeginRevive(target);
    }

    private void HandleCancelTrigger()
    {
        if (m_isChanneling)
            RequestCancelRevive();
    }

    private void HandleSelectSlot(int index) => HandleCancelTrigger();

    private PlayerHealth FindAllyTarget(IncapacitationCause cause)
    {
        GameObject targetObj = m_interactor != null ? m_interactor.CurrentTarget : null;
        if (targetObj == null)
            return null;

        PlayerHealth target = targetObj.GetComponentInParent<PlayerHealth>();
        if (target == null || target == m_selfHealth)
            return null;

        PlayerIncapacitation targetIncap = target.GetComponent<PlayerIncapacitation>();
        if (targetIncap == null || targetIncap.Cause != cause)
            return null;

        if (cause == IncapacitationCause.Die && !targetIncap.IsRevivable)
            return null;

        return target;
    }

    /// <summary>구조 채널링 시작 요청 — 오너가 호출.</summary>
    public void RequestBeginRevive(PlayerHealth target)
    {
        if (target == null)
            return;

        if (!IsSpawned || IsServer)
        {
            ServerBeginRevive(target);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        BeginReviveRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>구조 채널링 취소 요청 — 오너가 호출(이동·다른 입력·E 재입력, #725).</summary>
    public void RequestCancelRevive()
    {
        if (!IsSpawned)
        {
            ServerCancelRevive();
            return;
        }
        if (!IsOwner)
            return;
        CancelReviveRpc();
    }

    private bool IsTargetNetworkReady(PlayerHealth target)
    {
        if (target.NetworkObject != null && target.NetworkObject.IsSpawned)
            return true;
        Debug.LogWarning($"구조 요청 무시 — 대상이 네트워크 스폰되지 않음: {target.name}", this);
        return false;
    }

    [Rpc(SendTo.Server)]
    private void BeginReviveRpc(NetworkObjectReference targetRef)
    {
        if (
            targetRef.TryGet(out NetworkObject targetObj)
            && targetObj.TryGetComponent(out PlayerHealth target)
        )
        {
            ServerBeginRevive(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void CancelReviveRpc() => ServerCancelRevive();

    private void ServerBeginRevive(PlayerHealth target)
    {
        if (m_channel.IsActive || target == null)
            return;

        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
        {
            Debug.LogWarning(
                $"[Server] 다운 상태인 플레이어({m_selfHealth.name})가 구조를 시도하여 거부됨."
            );
            return;
        }

        if (target == m_selfHealth)
        {
            Debug.LogWarning(
                $"[Server] 플레이어({m_selfHealth.name})가 자가 구조(Self-revive)를 시도하여 거부됨."
            );
            return;
        }

        PlayerIncapacitation targetIncap = target.GetComponent<PlayerIncapacitation>();
        if (targetIncap == null || !targetIncap.IsDowned)
        {
            if (targetIncap != null && targetIncap.IsDead)
                Feedback?.NotifyOwner(
                    $"구조 불가 — {target.name}은 기능 정지 상태다. 부활 키트로 일으켜야 한다 (#613)"
                );
            return;
        }
        if (!IsInRange(target))
            return;

        ServerChannelAsync(target, targetIncap).Forget();
    }

    private async UniTaskVoid ServerChannelAsync(
        PlayerHealth target,
        PlayerIncapacitation targetIncap
    )
    {
        Feedback?.NotifyOwner($"구조 채널링 시작: {target.name} ({m_reviveSeconds}초)");
        Gauge?.Begin(m_reviveSeconds, EAudioClip.ReviveLoop);
        ServerNotifyChannelStarted();

        targetIncap.ServerSetBeingRevived(true, m_reviveSeconds);

        ServerChannel.Result result;
        try
        {
            result = await m_channel.RunAsync(
                m_reviveSeconds,
                () =>
                    target != null
                    && targetIncap.IsDowned
                    && (m_incapacitation == null || !m_incapacitation.IsIncapacitated)
            );
        }
        finally
        {
            Gauge?.End();
            targetIncap.ServerSetBeingRevived(false);
            ServerNotifyChannelEnded();
        }

        switch (result)
        {
            case ServerChannel.Result.Canceled:
                Feedback?.NotifyOwner("구조 취소됨");
                return;

            case ServerChannel.Result.OutOfRange:
                Feedback?.NotifyOwner(
                    target != null && targetIncap.IsDead
                        ? $"구조 중단 — 제한시간 초과로 기능 정지됨: {target.name} (본부 이송 필요)"
                        : "구조 중단 — 대상이 구조 대상이 아니게 됨"
                );
                return;

            case ServerChannel.Result.Completed:
                break;
        }

        if (target == null || !IsInRange(target))
        {
            Feedback?.NotifyOwner("구조 실패 — 대상이 범위를 벗어남");
            return;
        }
        if (!targetIncap.IsDowned)
        {
            Feedback?.NotifyOwner(
                targetIncap.IsDead
                    ? $"구조 실패 — 제한시간 초과로 기능 정지됨: {target.name} (본부 이송 필요)"
                    : "구조 취소 — 대상이 이미 복구됨"
            );
            return;
        }

        Feedback?.NotifyOwner($"구조 완료: {target.name}");
        target.ServerRevive();
        GetComponent<PlayerAssistCredit>()?.ServerCreditRescue();
    }

    private void ServerCancelRevive() => m_channel.Cancel();

    private void ServerNotifyChannelStarted()
    {
        if (IsSpawned && IsServer)
            m_isChannelingSynced.Value = true;

        if (IsSpawned && IsServer && !IsOwner)
        {
            ChannelStartedRpc();
            return;
        }
        m_isChanneling = true;
    }

    private void ServerNotifyChannelEnded()
    {
        if (IsSpawned && IsServer)
            m_isChannelingSynced.Value = false;

        if (IsSpawned && IsServer && !IsOwner)
        {
            ChannelEndedRpc();
            return;
        }
        m_isChanneling = false;
    }

    [Rpc(SendTo.Owner)]
    private void ChannelStartedRpc() => m_isChanneling = true;

    [Rpc(SendTo.Owner)]
    private void ChannelEndedRpc() => m_isChanneling = false;

    private bool IsInRange(PlayerHealth target) =>
        PlayerInteractor.IsWithinReach(
            m_interactor,
            target.transform,
            PlayerInteractor.RangeOf(m_interactor, k_fallbackRange),
            transform.position
        );

    public override void OnDestroy()
    {
        m_channel.Dispose();
        base.OnDestroy();
    }
}
