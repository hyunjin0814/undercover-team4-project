using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

public enum EEscortCommand
{
    RopeDrag = 0,
    RopeResume = 1,
    Unrope = 2,
}

/// <summary>
/// 검거·연행 요청과 판정의 서버 권위 허브 — 오너 요청을 서버로 넘겨 채널링·사거리·가시선·자원을 검증한다.
/// 밧줄 연결 상태는 PlayerEscorter에 위임한다.
/// </summary>
[RequireComponent(typeof(PlayerEscorter))]
[RequireComponent(typeof(ChannelGauge))]
[RequireComponent(typeof(OwnerFeedback))]
public class PlayerEscortCommands : NetworkBehaviour
{
    private OwnerFeedback m_feedback;

    private OwnerFeedback Feedback => this.ResolveCapability(ref m_feedback);

    private ChannelGauge m_gauge;

    private ChannelGauge Gauge => this.ResolveCapability(ref m_gauge);

    [Header("밧줄 채널링 (서버 권위)")]
    [Tooltip(
        "줄다리기 합류 채널링 시간(초). 0이면 좌클릭 한 번에 즉시 합류한다 (#608). "
        + "0보다 크면 예전 홀드 채널링으로 돌아간다(되돌리기용). "
        + "새로 묶기는 무력화된 대상만 대상이 되면서 이미 즉시 적용이고(#446), 풀기 홀드도 제거됐다"
    )]
    [Min(0f)]
    [SerializeField]
    private float m_channelSeconds;

    [Tooltip(
        "밧줄을 푼 뒤 쓰러진 채로 있는 시간(초) — 이 시간이 지나면 일어난다. 마지막 구간이 기상 모션이라 "
        + "총 무력화 시간이다. 이 구간은 다시 묶을 수 있는 재포획 창이기도 하다"
    )]
    [Min(0f)]
    [SerializeField]
    private float m_unropeDownSeconds = 3f;

    private const float k_fallbackRange = 3f;

    private readonly ServerChannel m_channel = new();

    private PlayerEscorter m_escorter;
    private PlayerInteractor m_interactor;
    private PlayerLoadout m_loadout;

    private PlayerEscorter Escorter
    {
        get
        {
            if (m_escorter == null)
                m_escorter = GetComponent<PlayerEscorter>();
            return m_escorter;
        }
    }

    private PlayerInteractor Interactor
    {
        get
        {
            if (m_interactor == null)
                m_interactor = GetComponent<PlayerInteractor>();
            return m_interactor;
        }
    }

    private PlayerLoadout Loadout
    {
        get
        {
            if (m_loadout == null)
                m_loadout = GetComponent<PlayerLoadout>();
            return m_loadout;
        }
    }

    private float CaptureRange => Interactor != null ? Interactor.Range : k_fallbackRange;

    /// <summary>채널링 취소 — 오너가 호출(이동·뗌 등).</summary>
    public void CancelCapture()
    {
        if (!IsSpawned)
        {
            ServerCancelCapture();
            return;
        }
        if (!IsOwner)
            return;
        CancelCaptureRpc();
    }

    /// <summary>밧줄 묶기 시도 — 오너가 호출(Rope 아이템 좌클릭). 서버/오프라인 즉시 실행, 원격은 서버로 요청.</summary>
    public void RequestRopeDrag(NpcController target) =>
        SendCommand(EEscortCommand.RopeDrag, target);

    /// <summary>놓아둔 체포 대상의 밧줄 끌기 재개를 요청한다(Rope 좌클릭).</summary>
    public void RequestRopeResume(NpcController target) =>
        SendCommand(EEscortCommand.RopeResume, target);

    /// <summary>밧줄 풀기 시도 — 오너가 호출(Rope 좌클릭, 대상이 체포 상태일 때). 서버/오프라인 즉시 실행, 원격은 서버로 요청.</summary>
    public void RequestUnrope(NpcController target) =>
        SendCommand(EEscortCommand.Unrope, target);

    /// <summary>지금 끌고 있는 대상 전원의 밧줄 풀기를 요청한다(겨냥 없는 E).</summary>
    public void RequestUnropeAll()
    {
        if (!IsSpawned || IsServer)
        {
            ServerUnropeAllDragged();
            return;
        }
        if (!IsOwner)
            return;
        UnropeAllRequestRpc();
    }

    private void SendCommand(EEscortCommand cmd, NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned || (IsServer && RunsDirectlyOnServer(cmd)))
        {
            ServerExecute(cmd, target);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        EscortCommandRpc(cmd, new NetworkObjectReference(target.NetworkObject));
    }

    private static bool RunsDirectlyOnServer(EEscortCommand cmd) =>
        cmd is EEscortCommand.RopeDrag or EEscortCommand.RopeResume or EEscortCommand.Unrope;

    private bool IsTargetNetworkReady(NpcController target)
    {
        if (target.NetworkObject != null && target.NetworkObject.IsSpawned)
            return true;
        Debug.LogWarning(
            $"검거/제압 요청 무시 — 대상 NPC가 네트워크 스폰되지 않음: {target.name}",
            this
        );
        return false;
    }

    [Rpc(SendTo.Server)]
    private void CancelCaptureRpc() => ServerCancelCapture();

    /// <summary>대상 지정 연행 명령을 받아 대상 유효성을 확인하고 서버 실행으로 넘긴다.</summary>
    [Rpc(SendTo.Server)]
    private void EscortCommandRpc(EEscortCommand cmd, NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj)
            && targetObj.TryGetComponent(out NpcController target))
        {
            ServerExecute(cmd, target);
        }
    }

    private void ServerExecute(EEscortCommand cmd, NpcController target)
    {
        switch (cmd)
        {
            case EEscortCommand.RopeDrag:
                ServerBeginRopeDrag(target);
                break;
            case EEscortCommand.RopeResume:
                ServerResumeRopeDrag(target);
                break;
            case EEscortCommand.Unrope:
                ServerBeginUnrope(target);
                break;
            default:
                Debug.LogWarning($"PlayerEscortCommands: 알 수 없는 연행 명령 {(int)cmd}", this);
                break;
        }
    }

    [Rpc(SendTo.Server)]
    private void UnropeAllRequestRpc() => ServerUnropeAllDragged();

    private void ServerCancelCapture() => m_channel.Cancel();

    /// <summary>진행 중인 채널링을 서버에서 즉시 중단한다. 서버(또는 오프라인) 전용.</summary>
    public void ServerCancelChannel()
    {
        if (IsSpawned && !IsServer)
            return;
        m_channel.Cancel();
    }

    /// <summary>밧줄 묶기 진입 — 검증 후 채널링을 시작한다. 서버(또는 오프라인) 실행.</summary>
    private void ServerBeginRopeDrag(NpcController target)
    {
        if (!CanBeginRopeDrag(target))
            return;

        if (NpcStateRules.CanJoinDrag(target.CurrentState))
        {
            ServerPlayRopeBind(target);

            if (m_channelSeconds <= 0f)
                ServerApplyRopeDrag(target);
            else
                ServerRopeJoinChannelAsync(target).Forget();

            return;
        }

        if (!NpcStateRules.CanRopeBind(target))
            return;

        ServerPlayRopeBind(target);
        ServerApplyRopeDrag(target);
    }

    /// <summary>체포되어 멈춘 대상을 채널링·반응 판정 없이 즉시 다시 끈다.</summary>
    private void ServerResumeRopeDrag(NpcController target)
    {
        if (m_channel.IsActive)
            return;
        if (!CanResumeRopeDrag(target))
            return;
        if (!IsInRange(target))
            return;

        ServerPlayRopeBind(target);
        ServerApplyRopeDrag(target);
    }

    /// <summary>이 대상에 밧줄 끌기 재개를 걸 수 있는지 판정한다(서버·클라 공용).</summary>
    public bool CanResumeRopeDrag(NpcController target)
    {
        if (target == null)
            return false;
        if (IsRopeBlocked(target))
            return false;

        if (Escorter.IsTetheredTo(target))
            return NpcStateRules.CanRelease(target.CurrentState)
                || NpcStateRules.CanJoinDrag(target.CurrentState);

        return NpcStateRules.CanRelease(target.CurrentState)
            && PlayerEscorter.FindEscorterOf(target) == null
            && !Escorter.IsAtRopeCapacity;
    }

    /// <summary>이 대상에 밧줄을 걸 수 없는지(수감 중) 판정한다(서버·클라 공용).</summary>
    public static bool IsRopeBlocked(NpcController target) =>
        target != null
        && (target.CurrentState == NpcState.Jailed
            || NpcStateRules.IsPlayingStandUp(target));

    private bool CanBeginRopeDrag(NpcController target)
    {
        if (m_channel.IsActive)
            return false;
        if (IsRopeBlocked(target))
            return false;
        if (Escorter.IsTetheredTo(target))
            return false;
        if (Escorter.IsAtRopeCapacity)
            return false;
        return IsInRange(target);
    }

    private async UniTaskVoid ServerRopeJoinChannelAsync(NpcController target)
    {
        Feedback?.NotifyOwner($"줄다리기 합류 채널링 시작: {target.name} ({m_channelSeconds}초)");
        Gauge?.Begin(m_channelSeconds, EAudioClip.None);

        ServerChannel.Result result;
        try
        {
            result = await m_channel.RunAsync(
                m_channelSeconds, () => target != null && IsInRange(target));
        }
        finally
        {
            Gauge?.End();
        }

        switch (result)
        {
            case ServerChannel.Result.OutOfRange:
                Feedback?.NotifyOwner("합류 실패 — 대상이 범위를 벗어남");
                return;

            case ServerChannel.Result.Canceled:
                Feedback?.NotifyOwner("합류 취소됨 (홀드 뗌)");
                return;
        }

        if (target == null || Escorter.IsAtRopeCapacity)
            return;
        if (IsRopeBlocked(target))
            return;
        if (!NpcStateRules.CanJoinDrag(target.CurrentState))
            return;

        ServerApplyRopeDrag(target);
    }

    private void ServerPlayRopeBind(NpcController target)
    {
        if (target == null || Escorter.IsTetheredTo(target))
            return;

        App.Game.Fx?.PlayEverywhere(EFx.RopeBind, target.transform.position);
    }

    private void ServerApplyRopeDrag(NpcController target)
    {
        Escorter.AddTether(target);

        if (target.Death.IsDead)
        {
            target.Rope.StartRopeDrag(transform);
            Feedback?.NotifyOwner(
                $"시체를 밧줄로 묶어 끌기 시작: {target.name} "
                    + $"({Escorter.TetheredCount}/{Escorter.RopeCapacity})"
            );
            return;
        }

        target.Custody.StartEscort(transform);
        target.Rope.StartRopeDrag(transform);

        target.Stun.ExitStun(resumeReaction: false);

        Feedback?.NotifyOwner($"밧줄로 묶어 끌기 시작: {target.name} ({Escorter.TetheredCount}/{Escorter.RopeCapacity})");
    }

    /// <summary>검증 후 밧줄을 즉시 푼다 — Captured면 누구나, Escorted면 자기 줄만. 서버(또는 오프라인) 실행.</summary>
    private void ServerBeginUnrope(NpcController target)
    {
        if (m_channel.IsActive)
            return;
        if (Loadout != null && !Loadout.HasRope)
            return;
        if (!CanUnrope(target))
            return;
        if (!IsInRange(target))
            return;

        ServerApplyUnrope(target);
    }

    /// <summary>지금 끌고 있는 대상 전원의 밧줄을 사거리·가시선 검사 없이 푼다. 서버(또는 오프라인) 실행.</summary>
    private void ServerUnropeAllDragged()
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_channel.IsActive)
            return;
        if (Loadout != null && !Loadout.HasRope)
            return;

        var dragged = new List<NpcController>(Escorter.ServerTethered);
        for (int i = 0; i < dragged.Count; i++)
        {
            NpcController target = dragged[i];
            if (target == null || !Escorter.IsDraggingNpc(target))
                continue;

            ServerApplyUnrope(target);
        }
    }

    /// <summary>소지·사거리 검사 없이 내 밧줄을 전부 푼다(정리 경로).</summary>
    public void ServerUnropeEverything()
    {
        if (IsSpawned && !IsServer)
            return;

        ServerCancelChannel();

        var tethered = new List<NpcController>(Escorter.ServerTethered);
        for (int i = 0; i < tethered.Count; i++)
        {
            if (tethered[i] != null)
                ServerApplyUnrope(tethered[i]);
        }
    }

    /// <summary>이 대상에 밧줄 풀기를 걸 수 있는가 — 서버 가드와 클라 조기검증(Rope)이 함께 쓰는 단일 기준.</summary>
    public bool CanUnrope(NpcController target) =>
        target != null
        && (NpcStateRules.CanRelease(target.CurrentState) || Escorter.IsTetheredTo(target));

    private void ServerApplyUnrope(NpcController target)
    {
        if (target.Death.IsDead)
        {
            Escorter.ReleaseDrag(target);
            Escorter.RemoveTether(target);
            Feedback?.NotifyOwner($"시체를 내려놓았다: {target.name}");
            return;
        }

        System.Action afterStandUp = target.Custody.ReleaseFromCustody;
        float downSeconds = m_unropeDownSeconds;

        if (Escorter.IsTetheredTo(target))
        {
            bool othersHold = Escorter.HasOtherTether(target);

            Escorter.ReleaseDrag(target);

            if (!othersHold)
                target.StandUp.ServerStandUpThen(afterStandUp, downSeconds);

            Escorter.RemoveTether(target);

            if (othersHold)
            {
                Feedback?.NotifyOwner($"내 밧줄만 풀었다 — 다른 참가자가 계속 확보 중: {target.name}");
                return;
            }

            Feedback?.NotifyOwner($"밧줄 풀기 완료 — 일어난 뒤 배회 복귀: {target.name}");
            return;
        }

        Feedback?.NotifyOwner($"밧줄 풀기 완료 — 배회 복귀: {target.name}");
        target.StandUp.ServerStandUpThen(afterStandUp, downSeconds);
    }

    private bool IsInRange(NpcController target) =>
        PlayerInteractor.IsWithinReach(Interactor, target.transform, CaptureRange, transform.position);

    public override void OnNetworkDespawn()
    {
        ServerCancelCapture();
    }

    public override void OnDestroy()
    {
        m_channel.Dispose();
        base.OnDestroy();
    }
}
