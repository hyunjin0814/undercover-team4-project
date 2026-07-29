using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어의 검거·연행 서버 권위 허브. (#59, #56/#118 네트워크 전환)
/// 오너 클라의 아이템/상호작용(Rope·NpcSubdueInteractable)이 이 컴포넌트의 요청 API를 호출하면,
/// 요청을 서버로 넘겨(ServerRpc) 서버가 채널링·사거리·반응 판정을 실행한다.
/// 그 결과 NpcController 상태 변경은 서버에서 일어나고 NetworkVariable로 전 피어에 동기화된다.
/// 검거는 밧줄로 이관됐다(#369): Rope가 좌클릭에 RequestRopeDrag/RequestUnrope, 뗌에 CancelCapture(채널 취소).
/// 놓기·끌기 재개는 상호작용키(E) — PlayerInteractor가 RequestRelease, NpcSubdueInteractable이 RequestRopeResume.
/// 한 번에 1명만 연행 가능 (동시 1명 제약).
/// 채널링 게이지 피드백(#184)은 공통 기반 ChanneledInteractionBehaviour가 제공한다.
/// </summary>
public partial class PlayerEscorter : ChanneledInteractionBehaviour
{
    [Header("수갑 채널링 (서버 권위)")]
    [Tooltip("체포 채널링 시간(초)")]
    [SerializeField]
    private float m_channelSeconds = 3f;

    [Tooltip(
        "도주 NPC 근접 제압(E 홀드) 채널링 시간(초) — 딸깍 한 번이 아니라 붙어서 홀드를 유지해야 잡힌다 (#332)"
    )]
    [SerializeField]
    private float m_subdueChannelSeconds = 3f;

    // 밧줄 끌기(#269) 관련 필드·상태·로직은 PlayerEscorter.RopeDrag.cs로 분리돼 있다 (partial).

    // 사거리는 조준·윤곽선과 같은 기준을 쓴다 — PlayerInteractor.Range 재사용 (#147 패턴, #184).
    // "윤곽선은 뜨는데 체포가 안 되는" 거리 불일치를 구조적으로 차단한다.
    private const float k_fallbackRange = 3f; // 테스트 구성 등 PlayerInteractor가 없을 때

    private PlayerInteractor m_interactor;

    private PlayerInteractor Interactor
    {
        get
        {
            if (m_interactor == null)
                m_interactor = GetComponent<PlayerInteractor>();
            return m_interactor;
        }
    }

    // 수갑 자원 게이트·소모용 로드아웃 (#229). 테스트 구성 등 없을 수 있어 null 허용.
    private PlayerLoadout m_loadout;

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

    // 거리 기준점 — 조준 레이캐스트·윤곽선 게이트와 동일한 AimOrigin(카메라).
    // 루트(발밑) 기준이면 카메라 오프셋만큼 사거리 경계에서 판정이 어긋난다 (#147 관례, #184)
    private Vector3 AimOriginPosition =>
        Interactor != null ? Interactor.AimOrigin.position : transform.position;

    // 밧줄 연결 목록·용량 게이트(TetheredCount·IsTetheredTo·IsDraggingNpc·IsAtRopeCapacity)는
    // PlayerEscorter.RopeDrag.cs에, 장력 계산·추종 상태·밧줄 길이는 NpcController(끌리는 쪽)에 있다.
    // 예전의 '한 번에 1명' 게이트(IsBusy)는 자원 게이트 IsAtRopeCapacity가 물려받았다.

    /// <summary>
    /// 해당 NPC를 밧줄에 묶고 있는 플레이어를 찾는다 — 없으면 null. 서버(또는 오프라인)에서만 유효.
    /// 인계 판정(ArrestJudge)이 이 결과로 끌기를 물리적으로 풀기 때문에, 빼면 인계자가 "알 수 없음"이 되고
    /// 끌기가 안 풀린 채(에이전트 꺼진 채) 상태 전이가 일어나 NavMeshAgent 예외가 난다. (#269)
    /// 여러 명이 걸려 있으면 그중 하나다 — 전원이 필요하면 <see cref="FindEscortersOf"/>.
    /// </summary>
    public static PlayerEscorter FindEscorterOf(NpcController npc)
    {
        if (npc == null)
            return null;

        PlayerEscorter[] escorters = FindObjectsByType<PlayerEscorter>(FindObjectsSortMode.None);
        foreach (PlayerEscorter escorter in escorters)
            if (escorter.IsTetheredTo(npc))
                return escorter;

        return null;
    }

    /// <summary>
    /// 해당 NPC에 밧줄을 걸고 있는 플레이어를 <b>전부</b> 찾는다 — 아무도 없으면 빈 목록. 서버(또는 오프라인) 전용.
    /// 줄다리기로 여러 명이 한 대상에 걸릴 수 있어, 인계 판정·오검거 페널티처럼 "관여한 사람 전원"을
    /// 알아야 하는 쪽이 이걸 쓴다.
    /// </summary>
    public static List<PlayerEscorter> FindEscortersOf(NpcController npc)
    {
        var found = new List<PlayerEscorter>();
        if (npc == null)
            return found;

        PlayerEscorter[] escorters = FindObjectsByType<PlayerEscorter>(FindObjectsSortMode.None);
        foreach (PlayerEscorter escorter in escorters)
            if (escorter.IsTetheredTo(npc))
                found.Add(escorter);

        return found;
    }

    // 서버 채널링 생명주기(CTS 소유·재진입 가드)는 ServerChannel에 위임 (#109)
    private readonly ServerChannel m_channel = new();

    // 지금 도는 채널링이 '도주 제압 홀드'인지 — E 뗌 취소가 수갑 채널링(좌클릭 홀드)을 오발로 끊지 않게
    // 종류를 구분한다. 서버(또는 오프라인)에서만 유효. (#332)
    private bool m_subdueChanneling;

    // ---- 오너 클라 진입점 (아이템/상호작용이 호출) ----

    // 검거 진입점은 밧줄로 이관됐다 — RequestRopeDrag/RequestUnrope는 PlayerEscorter.RopeDrag.cs에 있다. (#269/#369)

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

    /// <summary>끌기 놓기 — 오너가 호출(E). 조준한 대상 하나만 놓는다, 나머지는 계속 끌린다. (#390)</summary>
    public void RequestRelease(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned)
        {
            ReleaseDrag(target);
            return;
        }
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        ReleaseRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>도주 NPC 근접 제압 홀드 시작 — 오너가 호출(E 누름). 3초 홀드를 채워야 잡힌다. (#332)</summary>
    public void RequestSubdueCapture(NpcController target)
    {
        if (target == null)
            return;
        if (!IsSpawned || IsServer)
        {
            ServerBeginSubdue(target);
            return;
        } // 서버/오프라인 즉시 실행
        if (!IsOwner)
            return;
        if (!IsTargetNetworkReady(target))
            return;
        SubdueCaptureRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>도주 제압 홀드 취소 — 오너가 호출(E 뗌). 수갑 채널링은 건드리지 않는다(서버가 종류로 가드). (#332)</summary>
    public void RequestCancelSubdue()
    {
        if (!IsSpawned)
        {
            ServerCancelSubdue();
            return;
        }
        if (!IsOwner)
            return;
        CancelSubdueRpc();
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
    private void CancelSubdueRpc() => ServerCancelSubdue();

    [Rpc(SendTo.Server)]
    private void ReleaseRpc(NetworkObjectReference targetRef)
    {
        if (
            targetRef.TryGet(out NetworkObject targetObj)
            && targetObj.TryGetComponent(out NpcController target)
        )
        {
            ReleaseDrag(target);
        }
    }

    // 밧줄 끌기 요청 RPC(RopeDragRequestRpc)는 PlayerEscorter.RopeDrag.cs에 있다. (#269)

    [Rpc(SendTo.Server)]
    private void SubdueCaptureRpc(NetworkObjectReference targetRef)
    {
        if (
            targetRef.TryGet(out NetworkObject targetObj)
            && targetObj.TryGetComponent(out NpcController target)
        )
        {
            ServerBeginSubdue(target);
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

    // ---- 서버 실행 (권위) ----

    // 좌클릭 체포 채널링(ServerBeginCapture/ServerChannelAsync)은 밧줄 묶기로 대체되어 제거됐다. (#369)
    // 연행(StartEscort)은 밧줄 경로(PlayerEscorter.RopeDrag.cs)가 이어받는다.
    // 반응 판정은 검거에서 완전히 빠졌다 (#400) — 스캔·피격이 트리거이고 NpcController.ServerReactTo가 갖는다.

    private void ServerCancelCapture() => m_channel.Cancel();

    /// <summary>
    /// 진행 중인 체포/제압/해제 채널링을 서버 권위로 즉시 중단한다 — 수갑을 채널링 중 버리는 등
    /// 아이템 소유권 이전 경로에서 서버가 직접 호출한다(Handcuffs.ServerCancelActiveUse).
    /// 오너에 묶인 CancelCapture와 달리 소유권과 무관하므로 데디케이티드 서버에서도 동작한다. 서버(또는 오프라인) 전용.
    /// </summary>
    public void ServerCancelChannel()
    {
        if (IsSpawned && !IsServer)
            return;
        m_channel.Cancel();
    }

    /// <summary>
    /// 도주 NPC 근접 제압 홀드 진입 — 검증 후 채널링 시작. 서버(또는 오프라인) 실행. (#332)
    /// 딸깍 한 번에 잡히던 것을 저항형 연타 제압과 균형을 맞춰 홀드로 바꿨다 — 붙어서
    /// m_subdueChannelSeconds를 채워야 하고, 뗌·사거리 이탈·대상 상태 변화면 무산된다.
    /// </summary>
    private void ServerBeginSubdue(NpcController target)
    {
        if (m_channel.IsActive)
            return; // 체포/해제/제압 채널링 중복 방지 (한 채널 공유)
        if (IsAtRopeCapacity)
            return; // 소지한 밧줄을 전부 쓰고 있으면 새로 확보 불가 — 밧줄 없음(0개)도 여기서 걸린다 (#369 → #390)
        if (target.CurrentState != NpcState.Run)
            return; // 도주 중일 때만 — 저항은 타격 연타, 배회는 수갑 채널링이 정식 경로
        if (!IsInRange(target))
            return; // 사거리 밖이면 시작조차 안 함

        ServerSubdueChannelAsync(target).Forget();
    }

    private async UniTaskVoid ServerSubdueChannelAsync(NpcController target)
    {
        m_subdueChanneling = true;
        NotifyOwner($"제압 홀드 시작: {target.name} ({m_subdueChannelSeconds}초)");
        NotifyChannelGaugeStart(m_subdueChannelSeconds);

        // 도주 대상은 계속 달아나는 중 — 사거리 유지가 곧 추격이고, 뿌리치거나(상태 변화) 놓치면 무산된다
        ServerChannel.Result result;
        try
        {
            result = await m_channel.RunAsync(
                m_subdueChannelSeconds,
                () => target != null && target.CurrentState == NpcState.Run && IsInRange(target)
            );
        }
        finally
        {
            m_subdueChanneling = false;
            NotifyChannelGaugeEnd(); // 어떤 경로로 끝나도 게이지 숨김 보장 (#184)
        }

        switch (result)
        {
            case ServerChannel.Result.OutOfRange:
                NotifyOwner($"제압 실패 — 대상을 놓침: {(target != null ? target.name : "?")}");
                return;

            case ServerChannel.Result.Canceled:
                NotifyOwner("제압 취소됨 (홀드 뗌)");
                return;
        }

        // 홀드 완주 — 아직 도주 중이면 그 자리에서 체포
        if (target != null && target.CurrentState == NpcState.Run)
            target.CaptureBySubdue();
    }

    // E 뗌 취소 — 도주 제압 홀드만 끊는다. 수갑 체포/해제 채널링(좌클릭 홀드)은 종류가 달라 건드리지 않는다 (#332)
    private void ServerCancelSubdue()
    {
        if (m_subdueChanneling)
            m_channel.Cancel();
    }

    // ---- 밧줄 풀기 채널링 (서버 권위, #290 → #369) ----
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
        && (NpcStateRules.CanRelease(target.CurrentState) || IsTetheredTo(target));

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

        // 내 줄이 걸려 있으면 그것부터 뺀다 — 줄다리기 중이면 여기서 끝이다(남은 참가자가 계속 끈다).
        // 마지막 한 명이었으면 대상이 커스터디에서 풀려 아래 배회 복귀로 이어진다. (#390 규칙 8)
        if (IsTetheredTo(target))
        {
            ReleaseDrag(target);
            RemoveTether(target);

            if (FindEscorterOf(target) != null)
            {
                NotifyOwner($"내 밧줄만 풀었다 — 다른 참가자가 계속 확보 중: {target.name}");
                return;
            }
        }

        // 밧줄은 소모되지 않아 대상에 남은 게 없다 — 회수할 자원 없이 배회로 돌려보내기만 한다 (#369).
        NotifyOwner($"밧줄 풀기 완료 — 배회 복귀: {target.name}");
        target.ReleaseFromCustody();
    }

    private bool IsInRange(NpcController target)
    {
        // 사거리 + 가시선 — 거리만 보면 위조 RPC로 벽 너머 제압·검거가 된다 (#360).
        // Interactor 없는 구성(테스트 등)은 종전대로 거리만 본다.
        return (target.transform.position - AimOriginPosition).sqrMagnitude
                <= CaptureRange * CaptureRange
            && (Interactor == null || Interactor.HasLineOfSightTo(target.transform));
    }

    // ---- 오너 로그 피드백 ----

    // 판정 로그는 서버에서 찍히므로 원격 클라 오너는 결과를 볼 수 없다 — 오너 콘솔에도 같은 로그를 전달한다 (#91).
    // 정식 UI 피드백(#65 계열)이 생기면 이 RPC를 그 이벤트 전달 경로로 확장한다.
    private void NotifyOwner(string message)
    {
        Debug.Log(message); // 서버(호스트)·오프라인 콘솔
        if (IsSpawned && IsServer && !IsOwner)
            OwnerLogRpc(message); // 원격 클라가 오너인 경우에만 전달 (호스트 오너는 위에서 이미 찍음)
    }

    [Rpc(SendTo.Owner)]
    private void OwnerLogRpc(string message) => Debug.Log($"[서버 판정] {message}");

    // 채널링 게이지 피드백(NotifyChannelGaugeStart/End)은 기반 ChanneledInteractionBehaviour가 제공한다. (#184)

    // ---- 서버 내부 연행 상태 조작 ----

    // 놓기는 대상 단위(ReleaseDrag(npc))다 — ArrestJudge는 판정된 그 NPC를, E 놓기는 조준 대상을 넘긴다.
    // 밧줄 끌기 서버 로직(ServerBeginRopeDrag·AddTether·ReleaseDrag·TickTetherCleanup)은
    // PlayerEscorter.RopeDrag.cs로 분리돼 있다 (partial). (#269)

    private void Update()
    {
        // 참조 정리는 서버(또는 오프라인)에서만 — 끌기 상태 자체가 서버 권위다 (#56/#118).
        if (IsSpawned && !IsServer)
            return;

        // 밧줄 연결 매 프레임 정리 — 장력 자체는 끌리는 NpcController가 자기 Update에서 돈다.
        // 대상이 파괴되는 경로(라운드 종료 시 NPC가 씬과 함께 destroy)도 여기서 함께 걸러진다 — 목록에서
        // 빠지면 동기화 목록도 같이 정리되므로, 참조와 플래그가 어긋난 채 남는 일이 없다. (#356)
        TickTetherCleanup();
    }

    public override void OnNetworkDespawn()
    {
        ServerCancelCapture();
        ReleaseAllDrags();
    }

    public override void OnDestroy()
    {
        m_channel.Dispose();
        base.OnDestroy(); // NetworkBehaviour의 파괴 시 네트워크 정리 — 생략하면 정리 로직이 통째로 건너뛰어진다
    }
}
