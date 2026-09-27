using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 부활 키트 — Die 상태의 플레이어를 즉시 부활시키는 소모성 아이템.
/// 남에게 쓰는 좌클릭 경로와, 소지자 본인이 E 홀드로 스스로 일어나는 경로를 함께 담는다.
/// </summary>
[RequireComponent(typeof(ChannelGauge))]
[RequireComponent(typeof(OwnerFeedback))]
public class ReviveKit : ItemBase
{
    private OwnerFeedback m_feedback;

    private OwnerFeedback Feedback => this.ResolveCapability(ref m_feedback);

    private ChannelGauge m_gauge;

    private ChannelGauge Gauge => this.ResolveCapability(ref m_gauge);

    private const float k_fallbackRange = 3f;

    [Header("자가 부활 (#820)")]
    [Tooltip("자가 부활 채널링 시간(초) — 동료 구조(PlayerReviver)와 같은 값으로 시작한다")]
    [SerializeField]
    private float m_selfReviveSeconds = 3f;

    private readonly ServerChannel m_selfChannel = new();

    /// <summary>이 키트로 일으킬 수 있는 대상을 조준 중인지 — 윤곽선·크로스헤어 게이트.</summary>
    public override bool CanTarget(GameObject aimTarget) => ResolveTarget(aimTarget) != null;

    public override LocalizedString TargetPromptLabel(GameObject aimTarget) => InteractPrompts.Revive;

    /// <summary>사용 진입점 — 오너의 의도를 서버로 넘긴다. 실제 부활과 소모는 서버가 한다 (Scanner.Use와 같은 구조, #55).</summary>
    public override void Use(GameObject target)
    {
        PlayerHealth revivable = ResolveTarget(target);
        if (revivable == null)
        {
            Debug.Log("부활 실패: 기능 정지된 동료를 조준해야 한다");
            return;
        }

        if (this.HasServerAuthority())
        {
            ServerTryRevive(revivable);
            return;
        }

        if (!IsOwner)
            return;

        if (revivable.NetworkObject == null || !revivable.NetworkObject.IsSpawned)
        {
            Debug.LogWarning($"부활 요청 무시 — 대상이 네트워크 스폰되지 않음: {revivable.name}", this);
            return;
        }

        RequestReviveRpc(new NetworkObjectReference(revivable.NetworkObject));
    }

    private PlayerHealth ResolveTarget(GameObject aimTarget)
    {
        if (aimTarget == null)
            return null;

        PlayerHealth target = aimTarget.GetComponentInParent<PlayerHealth>();
        if (target == null || target == HolderHealth)
            return null;

        PlayerIncapacitation targetIncapacitation = target.GetComponent<PlayerIncapacitation>();
        return targetIncapacitation != null && targetIncapacitation.IsRevivable ? target : null;
    }

    private PlayerHealth HolderHealth
    {
        get
        {
            PlayerInteractor holder = Holder;
            return holder != null ? holder.GetComponent<PlayerHealth>() : null;
        }
    }

    [Rpc(SendTo.Server)]
    private void RequestReviveRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObject)
            && targetObject.TryGetComponent(out PlayerHealth target))
        {
            ServerTryRevive(target);
        }
    }

    /// <summary>부활 조건을 재검증하고 대상을 부활시킨 뒤 키트를 소모한다. 서버(또는 오프라인) 전용.</summary>
    private void ServerTryRevive(PlayerHealth target)
    {
        if (!this.HasServerAuthority() || target == null)
            return;

        PlayerInteractor holder = Holder;
        if (holder == null)
        {
            Debug.LogWarning("[부활 키트] 든 사람이 없는 키트로 부활 요청이 들어왔다 — 거부", this);
            return;
        }

        if (target == holder.GetComponent<PlayerHealth>())
        {
            Debug.LogWarning($"[부활 키트] 자가 부활 시도 거부 — {holder.name}", this);
            return;
        }

        PlayerIncapacitation userIncapacitation = holder.GetComponent<PlayerIncapacitation>();
        if (userIncapacitation != null && userIncapacitation.IsIncapacitated)
        {
            Feedback?.NotifyOwner("부활 실패 — 무력화 상태에서는 키트를 쓸 수 없다");
            return;
        }

        PlayerIncapacitation targetIncapacitation = target.GetComponent<PlayerIncapacitation>();
        if (targetIncapacitation == null || !targetIncapacitation.IsRevivable)
        {
            Feedback?.NotifyOwner($"부활 실패 — {target.name}은 부활 대상이 아니다");
            return;
        }

        if (!PlayerInteractor.IsWithinReach(
                holder,
                target.transform,
                PlayerInteractor.RangeOf(holder, k_fallbackRange),
                transform.position))
        {
            Feedback?.NotifyOwner($"부활 실패 — {target.name}이 사거리를 벗어났다");
            return;
        }

        target.ServerRevive();
        holder.GetComponent<PlayerAssistCredit>()?.ServerCreditRescue();
        Feedback?.NotifyOwner($"부활 완료: {target.name} (부활 키트 소모)");

        ServerConsume();
    }

    /// <summary>소지자 본인의 자가 부활 채널링 시작을 요청한다.</summary>
    public void RequestSelfRevive()
    {
        if (this.HasServerAuthority())
        {
            ServerBeginSelfRevive();
            return;
        }

        if (!IsOwner)
            return;

        BeginSelfReviveRpc();
    }

    /// <summary>자가 부활 채널링 취소 요청 — E를 떼거나 다른 취소 조건이 걸리면 호출한다.</summary>
    public void RequestCancelSelfRevive()
    {
        if (!IsSpawned)
        {
            ServerCancelSelfRevive();
            return;
        }
        if (!IsOwner)
            return;

        CancelSelfReviveRpc();
    }

    [Rpc(SendTo.Server)]
    private void BeginSelfReviveRpc() => ServerBeginSelfRevive();

    [Rpc(SendTo.Server)]
    private void CancelSelfReviveRpc() => ServerCancelSelfRevive();

    private void ServerBeginSelfRevive()
    {
        if (!this.HasServerAuthority() || m_selfChannel.IsActive)
            return;

        PlayerInteractor holder = Holder;
        if (holder == null)
        {
            Debug.LogWarning("[부활 키트] 든 사람이 없는 키트로 자가 부활 요청이 들어왔다 — 거부", this);
            return;
        }

        if (App.Game.Round != null && App.Game.Round.GameplayFrozen)
            return;

        PlayerIncapacitation incap = holder.GetComponent<PlayerIncapacitation>();
        if (incap == null || !(incap.IsDowned || incap.IsRevivable))
        {
            return;
        }

        ServerSelfChannelAsync(holder, incap).Forget();
    }

    private async UniTaskVoid ServerSelfChannelAsync(PlayerInteractor holder, PlayerIncapacitation incap)
    {
        Feedback?.NotifyOwner($"자가 부활 채널링 시작 ({m_selfReviveSeconds}초)");
        Gauge?.Begin(m_selfReviveSeconds, EAudioClip.ReviveLoop);

        incap.ServerSetBeingRevived(true, m_selfReviveSeconds);

        ServerChannel.Result result;
        try
        {
            result = await m_selfChannel.RunAsync(
                m_selfReviveSeconds,
                () =>
                    Holder == holder
                    && (incap.IsDowned || incap.IsRevivable)
            );
        }
        finally
        {
            Gauge?.End();
            incap.ServerSetBeingRevived(false);
        }

        switch (result)
        {
            case ServerChannel.Result.Canceled:
                Feedback?.NotifyOwner("자가 부활 취소됨");
                return;

            case ServerChannel.Result.OutOfRange:
                Feedback?.NotifyOwner("자가 부활 중단 — 대상이 부활 대상이 아니게 됨");
                return;

            case ServerChannel.Result.Completed:
                break;
        }

        if (Holder != holder)
        {
            Feedback?.NotifyOwner("자가 부활 실패 — 키트를 손에서 놓쳤다");
            return;
        }

        PlayerHealth health = holder.GetComponent<PlayerHealth>();
        if (health == null || !(incap.IsDowned || incap.IsRevivable))
        {
            Feedback?.NotifyOwner("자가 부활 실패 — 이미 복구됐거나 부활 대상이 아니다");
            return;
        }

        health.ServerRevive();
        Feedback?.NotifyOwner("자가 부활 완료 (부활 키트 소모)");
        ServerConsume();
    }

    private void ServerCancelSelfRevive() => m_selfChannel.Cancel();

    /// <summary>자가 부활 채널링을 서버에서 즉시 중단한다.</summary>
    public override void ServerCancelActiveUse() => m_selfChannel.Cancel();

    public override void OnDestroy()
    {
        m_selfChannel.Dispose();
        base.OnDestroy();
    }
}
