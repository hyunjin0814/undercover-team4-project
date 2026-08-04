using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 검거·연행 <b>요청과 판정</b>의 서버 권위 허브. (#59, #56/#118 네트워크 전환, #269/#369)
/// 오너 클라의 아이템/상호작용(Rope·NpcSubdueInteractable·PlayerInteractor)이
/// 여기 요청 API를 호출하면, 요청을 서버로 넘겨(ServerRpc) 서버가 채널링·사거리·가시선·자원을 검증하고
/// 그 결과 NpcController 상태 변경은 서버에서 일어난다.
///
/// 밧줄 연결 <b>상태</b>는 <see cref="PlayerEscorter"/>가 소유한다 — 이쪽은 입력이 올 때만 돌고
/// 저쪽은 매 프레임 도는, 구동 주체가 다른 관심사다. 의존은 <c>Commands → Escorter</c> 한 방향뿐이며
/// 목록을 직접 만지지 않고 <c>AddTether</c>/<c>RemoveTether</c>/<c>ReleaseDrag</c>로 위임한다.
///
/// 채널링 게이지 피드백(#184)은 공통 기반 <see cref="ChanneledInteractionBehaviour"/>가 제공한다.
/// </summary>
[RequireComponent(typeof(PlayerEscorter))]
public class PlayerEscortCommands : ChanneledInteractionBehaviour
{
    [Header("밧줄 채널링 (서버 권위)")]
    [Tooltip(
        "밧줄 채널링 시간(초) — 줄다리기 합류와 풀기에 쓴다. 새로 묶기는 무력화된 대상만 대상이 되면서 "
        + "채널링 없이 즉시 적용으로 바뀌어 이 값을 쓰지 않는다 (#446)"
    )]
    [SerializeField]
    private float m_channelSeconds = 3f;

    // 사거리는 조준·윤곽선과 같은 기준을 쓴다 — PlayerInteractor.Range 재사용 (#147 패턴, #184).
    // "윤곽선은 뜨는데 체포가 안 되는" 거리 불일치를 구조적으로 차단한다.
    private const float k_fallbackRange = 3f; // 테스트 구성 등 PlayerInteractor가 없을 때

    // 서버 채널링 생명주기(CTS 소유·재진입 가드)는 ServerChannel에 위임 (#109)
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

    // 밧줄 소지 확인용 로드아웃 (#229/#369). 테스트 구성 등 없을 수 있어 null 허용.
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

    // ---- 오너 클라 진입점 (아이템/상호작용이 호출) ----

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

    /// <summary>밧줄 묶기 시도 — 오너가 호출(Rope 아이템 좌클릭). 서버/오프라인 즉시 실행, 원격은 서버로 요청. (#269)</summary>
    public void RequestRopeDrag(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned || IsServer)
        {
            ServerBeginRopeDrag(target);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        RopeDragRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>밧줄 끌기 재개 — 오너가 호출(E, NpcSubdueInteractable). 놓아뒀던 체포 대상을 다시 끈다. (#91 재연행의 자리, #369)</summary>
    public void RequestRopeResume(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned || IsServer)
        {
            ServerResumeRopeDrag(target);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        RopeResumeRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>밧줄 풀기 시도 — 오너가 호출(Rope 좌클릭, 대상이 체포 상태일 때). 서버/오프라인 즉시 실행, 원격은 서버로 요청. (#290 → #369)</summary>
    public void RequestUnrope(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned || IsServer)
        {
            ServerBeginUnrope(target);
            return;
        } // 서버/오프라인 즉시 실행
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        UnropeRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>끌기 놓기 — 오너가 호출(E). 조준한 대상 하나만 놓는다, 나머지는 계속 끌린다. (#390)</summary>
    public void RequestRelease(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned)
        {
            Escorter.ReleaseDrag(target);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        ReleaseRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>유치장 반출 요청 — 오너가 호출(앉은 수감자에 E). 밧줄을 쓰지 않으므로 용량 게이트를 타지 않는다. (#492)</summary>
    public void RequestJailRelease(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned)
        {
            ServerJailRelease(target);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        JailReleaseRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>멈춘 수감자 추종 재개 요청 — 오너가 호출(거리 이탈로 멈춘 반출 수감자에 E).
    /// 정지(<see cref="RequestEscortHalt"/>)의 역방향이다 — 밧줄을 쓰지 않으므로 용량 게이트를 타지 않는다. (#517)</summary>
    public void RequestEscortResume(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned)
        {
            ServerEscortResume(target);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        EscortResumeRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>따라오는 수감자 정지 요청 — 오너가 호출(반출된 수감자에 E). 밧줄과 무관한 추종을 끊는다. (#492)</summary>
    public void RequestEscortHalt(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned)
        {
            ServerEscortHalt(target);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        EscortHaltRpc(new NetworkObjectReference(target.NetworkObject));
    }

    // 원격 클라 → 서버로 대상을 넘기려면 스폰돼 있어야 한다(NetworkObjectReference 제약).
    // 스폰 안 된 NPC(씬 배치 후 미스폰 등)면 참조 생성이 예외를 던지므로 미리 걸러 경고만 남긴다.
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

    // ---- 서버 RPC (오너 → 서버) ----
    // NGO 2.x 유니버설 RPC: 오너가 자기 플레이어 오브젝트에서 서버로 보내므로 소유권 문제 없음

    [Rpc(SendTo.Server)]
    private void CancelCaptureRpc() => ServerCancelCapture();

    [Rpc(SendTo.Server)]
    private void RopeDragRequestRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerBeginRopeDrag(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void RopeResumeRequestRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerResumeRopeDrag(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void UnropeRequestRpc(NetworkObjectReference targetRef)
    {
        if (
            targetRef.TryGet(out NetworkObject targetObj)
            && targetObj.TryGetComponent(out NpcController target)
        )
        {
            ServerBeginUnrope(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void ReleaseRpc(NetworkObjectReference targetRef)
    {
        if (
            targetRef.TryGet(out NetworkObject targetObj)
            && targetObj.TryGetComponent(out NpcController target)
        )
        {
            Escorter.ReleaseDrag(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void EscortResumeRpc(NetworkObjectReference targetRef)
    {
        if (
            targetRef.TryGet(out NetworkObject targetObj)
            && targetObj.TryGetComponent(out NpcController target)
        )
        {
            ServerEscortResume(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void EscortHaltRpc(NetworkObjectReference targetRef)
    {
        if (
            targetRef.TryGet(out NetworkObject targetObj)
            && targetObj.TryGetComponent(out NpcController target)
        )
        {
            ServerEscortHalt(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void JailReleaseRpc(NetworkObjectReference targetRef)
    {
        if (
            targetRef.TryGet(out NetworkObject targetObj)
            && targetObj.TryGetComponent(out NpcController target)
        )
        {
            ServerJailRelease(target);
        }
    }

    // ---- 서버 실행: 채널 제어 ----

    private void ServerCancelCapture() => m_channel.Cancel();

    /// <summary>
    /// 진행 중인 채널링을 서버 권위로 즉시 중단한다 — 밧줄을 채널링 중 버리는 등 아이템 소유권 이전
    /// 경로에서 서버가 직접 호출한다(Rope.ServerCancelActiveUse).
    /// 오너에 묶인 <see cref="CancelCapture"/>와 달리 소유권과 무관하므로 데디케이티드 서버에서도 동작한다.
    /// 서버(또는 오프라인) 전용.
    /// </summary>
    public void ServerCancelChannel()
    {
        if (IsSpawned && !IsServer)
            return;
        m_channel.Cancel();
    }

    // ---- 서버 실행: 밧줄 묶기 (#269/#369) ----

    /// <summary>밧줄 묶기 진입 — 검증 후 채널링을 시작한다. 서버(또는 오프라인) 실행. (#369)</summary>
    private void ServerBeginRopeDrag(NpcController target)
    {
        if (!CanBeginRopeDrag(target))
            return;

        // 남이 끌고 있는 대상에는 밧줄을 덧건다 — 줄다리기 합류. 기존 끌기는 끊지 않는다(탈취 차단).
        // 이미 커스터디라 반응 판정이 필요 없어 채널링만 태우고 바로 붙인다.
        // 합류는 제압이 아니라 이미 확보된 신병에 대한 조작이라 좌클릭 홀드가 그대로 남아 있다 (#446).
        if (NpcStateRules.CanJoinDrag(target.CurrentState))
        {
            ServerRopeJoinChannelAsync(target).Forget();
            return;
        }

        // 새로 묶기는 무력화된 대상만 — 깨어 있는 NPC를 좌클릭 3초 홀드로 묶던 경로는 제거됐다 (#446).
        // 홀드가 없어져 판정을 통과하면 그 자리에서 즉시 묶인다: 원래 기절 대상에만 있던 지름길이
        // (기절 지속이 채널보다 짧아 콤보가 깨지던 문제, #269) 이제 유일한 경로가 됐다.
        // 클라 조기검증·조준 피드백(Rope)과 단일 기준 (#184).
        if (!NpcStateRules.CanRopeBind(target))
            return;

        ServerApplyRopeDrag(target);
    }

    /// <summary>밧줄 끌기 재개 — 이미 체포되어 멈춘 대상을 채널링·반응 판정 없이 즉시 다시 끈다. (#369)
    /// 수갑 시절의 재연행(ServerEscort)이 그랬듯, 이미 확보된 신병에 반응 판정을 다시 굴리면
    /// 잡아 둔 대상이 그 자리에서 도망치게 된다.</summary>
    private void ServerResumeRopeDrag(NpcController target)
    {
        if (m_channel.IsActive)
            return;
        if (!IsInRange(target))
            return;

        // 이미 내 줄에 묶여 있는(E로 놓아둔) 대상은 용량 게이트를 타지 않는다 — 새 밧줄을 쓰지 않으므로.
        // 태우면 밧줄을 꽉 채워 놓아둔 순간 아무도 다시 못 끌게 된다.
        bool ownRope = Escorter.IsTetheredTo(target);
        if (!ownRope)
        {
            // 남이 묶어 둔 대상은 가져올 수 없다 — 탈취 차단.
            // 합류는 상대가 실제로 끌고 있을 때(Escorted) 밧줄 좌클릭으로만 열린다.
            if (PlayerEscorter.FindEscorterOf(target) != null)
                return;
            if (!CanBeginRopeDrag(target))
                return;
        }

        // 기본은 체포되어 멈춘 대상(Captured)이고, 내 줄이 걸려 있으면 남이 계속 끄는 중(Escorted)도
        // 재개할 수 있다 — 줄다리기에서 E로 빠졌다 다시 끼는 정상 플레이다 (#398).
        // 줄이 없으면 Captured만 — 그 차이가 탈취 차단이다.
        if (!NpcStateRules.CanRelease(target.CurrentState)
            && !(ownRope && NpcStateRules.CanJoinDrag(target.CurrentState)))
            return;

        ServerApplyRopeDrag(target);
    }

    // 새 대상을 묶을 수 있는가 — 자원(밧줄 개수)·중복·사거리. 상태 게이트는 호출부가 각자 건다.
    private bool CanBeginRopeDrag(NpcController target)
    {
        if (m_channel.IsActive)
            return false;
        if (Escorter.IsTetheredTo(target))
            return false; // 이미 내 줄에 묶여 있다 — 좌클릭은 풀기/재개로 갈린다
        if (Escorter.IsAtRopeCapacity)
            return false; // 소지한 밧줄 수만큼만 — 예전의 "한 번에 1명"을 대체한 자원 게이트. 운반 중인 동료도 한 칸을 차지한다 (#365)
        return IsInRange(target);
    }

    // 줄다리기 합류 채널링 — 새로 묶기가 즉시 적용으로 바뀌면서(#446) 이 채널은 합류 전용이 됐다.
    private async UniTaskVoid ServerRopeJoinChannelAsync(NpcController target)
    {
        NotifyOwner($"줄다리기 합류 채널링 시작: {target.name} ({m_channelSeconds}초)");
        NotifyChannelGaugeStart(m_channelSeconds);

        // 수갑 체포와 동일한 keepAlive — 도중 거리 이탈은 즉시 실패시킨다 (#91)
        ServerChannel.Result result;
        try
        {
            result = await m_channel.RunAsync(
                m_channelSeconds, () => target != null && IsInRange(target));
        }
        finally
        {
            NotifyChannelGaugeEnd(); // 어떤 경로로 끝나도 게이지 숨김 보장 (#184)
        }

        switch (result)
        {
            case ServerChannel.Result.OutOfRange:
                NotifyOwner("합류 실패 — 대상이 범위를 벗어남");
                return;

            case ServerChannel.Result.Canceled:
                NotifyOwner("합류 취소됨 (홀드 뗌)");
                return;
        }

        // 채널링 도중 상태·자원이 바뀌었을 수 있다 — 완료 시점에 재확인(그 사이 밧줄을 버려 용량이
        // 준 경우, 합류하려던 대상을 끌던 사람이 그새 놓아버린 경우 등).
        if (target == null || Escorter.IsAtRopeCapacity)
            return;
        if (!NpcStateRules.CanJoinDrag(target.CurrentState))
            return;

        // 반응 판정은 여기서 굴리지 않는다 (#400) — 밧줄은 순수 검거 수단이 됐고, 판정은
        // NpcController.ServerReactTo가 단독으로 갖는다.
        ServerApplyRopeDrag(target);
    }

    // 실제 끌기 진입 — 검증이 끝난 뒤의 상태 조작만 담당한다. 서버(또는 오프라인).
    // 합류(남이 이미 끌고 있음)에도 그대로 쓴다: 아래 셋은 같은 상태를 다시 쓰거나 앵커를 더할 뿐이라
    // 기존 참가자를 건드리지 않는다.
    //
    // 세 호출의 순서가 전부 강제다:
    //   StartEscort → StartRopeDrag : 에이전트를 끈 뒤에 전이하면 직전 상태 Exit이 꺼진 에이전트에
    //                                 isStopped를 써 에러가 난다 (넉백 ServerApplyKnockback과 같은 순서).
    //   StartEscort → ExitStun      : EnterStunned가 Escorted를 만나면 StopEscort로 연행을 끊으므로
    //                                 뒤집으면 방금 건 커스터디가 풀린다.
    private void ServerApplyRopeDrag(NpcController target)
    {
        Escorter.AddTether(target);

        // 커스터디 상태는 수갑 연행과 같은 Escorted를 재사용한다 — 유치장 판정·이벤트 수명·가로채기 방지가
        // 이미 이 상태를 기준으로 판정하기 때문. 이동은 밧줄 장력이 하고 NpcEscortedState가 IsRoped를 보고
        // 추종을 건너뛴다. (#369)
        target.StartEscort(transform);
        target.StartRopeDrag(transform); // 끈 플레이어를 위협으로 기억 — 풀려나면 이쪽에서 도망친다

        // 기절한 채 묶였으면 오버레이를 걷는다 — 남겨두면 만료 해제 경로(resumeReaction: true)가
        // StartFlee를 걸어 묶자마자 도망친다. (#292)
        target.ExitStun(resumeReaction: false);

        NotifyOwner($"밧줄로 묶어 끌기 시작: {target.name} ({Escorter.TetheredCount}/{Escorter.RopeCapacity})");
    }

    // ---- 서버 실행: 밧줄 풀기 채널링 (#290 → #369) ----
    // 묶기 채널링의 역방향 — 밧줄을 든 좌클릭으로 체포되어 멈춘 NPC를 풀어 배회로 돌려보낸다.
    // 묶기와 같은 m_channel·게이지·사거리 판정을 재사용한다(대상 상태가 갈라 주므로 채널 하나면 충분).

    /// <summary>밧줄 풀기 진입 — 중복·사거리 검증 후 채널링 시작. 서버(또는 오프라인) 실행. (#369/#390)
    /// 두 갈래다: 놓아둔 체포(Captured)는 <b>누구나</b> 풀어 배회로 돌려보낼 수 있고(오검거 구제·방해 수단),
    /// 끌리는 중(Escorted)이면 <b>자기 줄만</b> 뺄 수 있다 — 줄다리기에서 손을 떼는 수단이다.
    /// 남이 끌고 있는 줄까지 풀 수 있게 하면 탈취 차단의 우회로가 된다 — 뺏을 필요도 없이 다 풀어버린다.
    /// 다른 대상을 끌고 있어도 풀기는 가능하다.</summary>
    private void ServerBeginUnrope(NpcController target)
    {
        if (m_channel.IsActive)
            return; // 묶기/풀기 채널링 중복 방지 (한 채널 공유)
        if (Loadout != null && !Loadout.HasRope)
            return; // 밧줄을 들고 있어야 풀 수 있다
        if (!CanUnrope(target))
            return; // 클라 조기검증(Rope.Use)과 단일 기준 (#184/#369)
        if (!IsInRange(target))
            return; // 사거리 밖이면 시작조차 안 함

        ServerUnropeChannelAsync(target).Forget();
    }

    /// <summary>이 대상에 밧줄 풀기를 걸 수 있는가 — 서버 가드와 클라 조기검증(Rope)이 함께 쓰는 단일 기준.</summary>
    public bool CanUnrope(NpcController target) =>
        target != null
        && (NpcStateRules.CanRelease(target.CurrentState) || Escorter.IsTetheredTo(target));

    private async UniTaskVoid ServerUnropeChannelAsync(NpcController target)
    {
        NotifyOwner($"밧줄 풀기 채널링 시작: {target.name} ({m_channelSeconds}초)");
        NotifyChannelGaugeStart(m_channelSeconds);

        // 체포 채널링과 동일한 keepAlive — 도중 거리 이탈은 즉시 실패시킨다.
        ServerChannel.Result result;
        try
        {
            result = await m_channel.RunAsync(
                m_channelSeconds,
                () => target != null && IsInRange(target)
            );
        }
        finally
        {
            NotifyChannelGaugeEnd(); // 어떤 경로로 끝나도 게이지 숨김 보장
        }

        if (result != ServerChannel.Result.Completed)
        {
            NotifyOwner("밧줄 풀기 중단 (홀드 뗌 / 거리 이탈)");
            return;
        }

        // 채널링 도중 상태가 바뀌었을 수 있다 — 완료 시점에 재확인(예: 그새 다른 플레이어가 끌기 재개).
        if (!CanUnrope(target))
            return;

        // <b>유치장 안에서는 석방하지 않는다</b> (#492) — 아래 ReleaseDrag가 이미 Captured로 세워 뒀으므로
        // 그대로 끝낸다. 판정을 통과한 대상이면 다음 틱에 JailIntake가 좌석에 앉히고, 안 통과했으면
        // 그 자리에 서 있는다(다시 묶어 끌고 나가면 된다).
        //
        // 여기서 배회로 돌려보내면 두 가지가 깨진다: ① 판정까지 통과한 수감 대상이 계상 없이(0원)
        // 풀려나고, ② 배회 시민이 유치장 안을 걸어 다녀 "시민은 유치장에 못 들어간다"(#415)가
        // 없애려던 그림이 다시 생긴다. E로 놓는 것과 결과가 같아지는 것이 조작 일관성에도 맞다.
        //
        // <b>JailArea를 직접 보는 것은 "유치장을 아는 것"이 아니다</b> — 이 허브가 유치장 오브젝트를
        // 찾아 조작하는 것은 여전히 ServerJailRelease 하나뿐이고(JailIntake에 위임), 여기 쓰는 것은
        // "이 좌표가 Jail 영역 위인가"를 답하는 정적 판정 유틸이다. 앉히는 판단은 JailIntake가 쥔다.
        //
        // 밧줄은 소모되지 않아 대상에 남은 게 없다 — 회수할 자원 없이 배회로 돌려보내기만 한다 (#369).
        bool insideJail = JailArea.Contains(target.transform.position);
        System.Action afterStandUp = insideJail ? null : target.ReleaseFromCustody;

        // 내 줄이 걸려 있으면 그것부터 뺀다 — 줄다리기 중이면 여기서 끝이다(남은 참가자가 계속 끈다).
        // 마지막 한 명이었으면 대상이 커스터디에서 풀려 아래 배회 복귀로 이어진다. (#390 규칙 8)
        if (Escorter.IsTetheredTo(target))
        {
            // 내 줄을 빼기 전에 물어야 한다 — 뺀 뒤에는 대상의 묶임 표시가 이미 내려가
            // "묶여 누워 있었는가"를 알 수 없다 (#513)
            bool othersHold = Escorter.HasOtherTether(target);

            Escorter.ReleaseDrag(target);

            // 마지막 줄이 풀리는 순간이 곧 일어나는 순간이다 (#513) — 여기까지는 묶인 채 누워 있었다.
            // RemoveTether보다 <b>앞</b>이어야 한다: 줄을 먼저 빼면 묶임 표시가 내려가
            // ServerStandUpThen이 "이미 서 있다"로 오판해 일어나기가 통째로 생략된다.
            if (!othersHold)
                target.ServerStandUpThen(afterStandUp);

            Escorter.RemoveTether(target);

            if (othersHold)
            {
                NotifyOwner($"내 밧줄만 풀었다 — 다른 참가자가 계속 확보 중: {target.name}");
                return;
            }

            NotifyOwner(insideJail
                ? $"밧줄 풀기 완료 — 일어난 뒤 유치장 안 그 자리에 둔다: {target.name}"
                : $"밧줄 풀기 완료 — 일어난 뒤 배회 복귀: {target.name}");
            return;
        }

        // 줄이 안 걸린 체포 대상(제압만으로 잡힌 Captured) — 이미 서 있으니 일어날 것도 없다.
        NotifyOwner(insideJail
            ? $"밧줄 풀기 완료 — 유치장 안이라 그 자리에 둔다: {target.name}"
            : $"밧줄 풀기 완료 — 배회 복귀: {target.name}");
        afterStandUp?.Invoke();
    }

    // 본부 인계 요청(#414)은 제거됐다 (#492) — 판정 트리거가 인계 단말에서 유치장 앞 보안 게이트로
    // 옮겨져 JailIntake가 직접 ArrestJudge.Judge를 부른다. 플레이어가 보낼 요청 자체가 없어졌다.

    // ---- 서버 실행: 유치장 반출 (#492) ----

    // 반출 실행 — 사거리만 확인하고 나머지(상태·좌석·정산)는 JailIntake가 판단한다.
    // 유치장을 아는 것은 저쪽이고 여기는 요청 허브일 뿐이다.
    private void ServerJailRelease(NpcController target)
    {
        if (IsSpawned && !IsServer)
            return;

        if (target == null || !IsInRange(target))
            return;

        // JailIntake는 매니저가 아니라 장소 오브젝트라 App 파사드 대상이 아니다 (JailLock·JailZone과 같은 관례).
        // E 입력 때만 도는 경로라 매 프레임 탐색 비용도 없다.
        JailIntake intake = FindFirstObjectByType<JailIntake>();
        if (intake == null)
        {
            Debug.LogWarning("PlayerEscortCommands: JailIntake가 없어 반출할 수 없다", this);
            return;
        }

        intake.ServerExtract(target, transform);
    }

    // 추종 재개 실행 — 거리 이탈로 멈춘 반출 수감자를 다시 따라오게 한다. 서버(또는 오프라인). (#517)
    //
    // 밧줄을 걸지 않는다: 반출(JailIntake.ServerExtract)과 같은 방식으로 StartEscort만 부르면
    // NpcEscortedState의 추종·속도 부스트·거리 이탈이 그대로 동작한다. 따라서 밧줄 소지·용량과 무관하다.
    //
    // 소유권을 묻지 않는다 — 정지(ServerEscortHalt)와 같은 취급이다. 남이 꺼낸 수감자를 대신
    // 데려가는 것은 신병을 뺏는 행위가 아니라 이미 정산에서 빠진 대상을 도로 넣어 주는 협동이다.
    private void ServerEscortResume(NpcController target)
    {
        if (IsSpawned && !IsServer)
            return;

        // 상태 + 반출 표식 — 방금 제압한 신병(같은 Captured)이 이리로 새면 밧줄 없이 끌려간다
        if (!NpcStateRules.CanResumeUnropedEscort(target))
            return;

        if (!IsInRange(target))
            return;

        target.StartEscort(transform);
        NotifyOwner($"수감자 추종 재개: {target.name}");
    }

    // 추종 정지 실행 — 밧줄 없이 따라오는 수감자를 그 자리에 세운다(Captured). 서버(또는 오프라인).
    // 유치장 안이면 JailIntake가 그 Captured를 보고 좌석에 다시 앉힌다 — 여기서 유치장을 알 필요는 없다.
    //
    // 소유권을 묻지 않는다: 남이 꺼낸 수감자도 세울 수 있다. 밧줄 놓기(Captured 대상 풀기)가
    // 누구에게나 열려 있는 것과 같은 취급이고, 세우는 것은 신병을 뺏는 행위가 아니라 멈추는 행위다.
    private void ServerEscortHalt(NpcController target)
    {
        if (IsSpawned && !IsServer)
            return;

        // 밧줄 끌기 중인 대상은 여기 못 온다 — 그쪽 E는 놓기/줄다리기 복귀로 이미 갈린다
        if (!NpcStateRules.IsFollowingUnroped(target))
            return;

        if (!IsInRange(target))
            return;

        target.StopEscort();
        NotifyOwner($"수감자 정지: {target.name}");
    }

    // ---- 공통 ----

    private bool IsInRange(NpcController target) =>
        PlayerInteractor.IsWithinReach(Interactor, target.transform, CaptureRange, transform.position);

    // 채널링 게이지와 오너 피드백(NotifyOwner)은 기반 ChanneledInteractionBehaviour가 제공한다. (#184/#91)

    public override void OnNetworkDespawn()
    {
        ServerCancelCapture();
    }

    public override void OnDestroy()
    {
        m_channel.Dispose();
        base.OnDestroy(); // NetworkBehaviour의 파괴 시 네트워크 정리 — 생략하면 정리 로직이 통째로 건너뛰어진다
    }
}
