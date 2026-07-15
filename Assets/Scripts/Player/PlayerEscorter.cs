using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어의 검거·연행 서버 권위 허브. (#59, #56/#118 네트워크 전환)
/// 오너 클라의 아이템/상호작용(Handcuffs·NpcSubdueInteractable)이 이 컴포넌트의 요청 API를 호출하면,
/// 요청을 서버로 넘겨(ServerRpc) 서버가 채널링·사거리·반응 판정을 실행한다.
/// 그 결과 NpcController 상태 변경은 서버에서 일어나고 NetworkVariable로 전 피어에 동기화된다.
/// 체포 채널링: Handcuffs가 좌클릭 누름에 RequestCapture, 뗌에 CancelCapture를 요청 (#91).
/// 놓기·재연행은 상호작용키(E) — PlayerInteractor가 RequestRelease, NpcSubdueInteractable이 RequestEscort (#91).
/// 한 번에 1명만 연행 가능 (동시 1명 제약).
/// </summary>
public class PlayerEscorter : NetworkBehaviour
{
    [Header("수갑 채널링 (서버 권위)")]
    [Tooltip("체포 채널링 시간(초)")]
    [SerializeField] private float m_channelSeconds = 3f;

    // 사거리는 조준·윤곽선과 같은 기준을 쓴다 — PlayerInteractor.Range 재사용 (#147 패턴, #184).
    // "윤곽선은 뜨는데 체포가 안 되는" 거리 불일치를 구조적으로 차단한다.
    private const float k_fallbackRange = 3f; // 테스트 구성 등 PlayerInteractor가 없을 때

    private PlayerInteractor m_interactor;

    private PlayerInteractor Interactor
    {
        get
        {
            if (m_interactor == null) m_interactor = GetComponent<PlayerInteractor>();
            return m_interactor;
        }
    }

    private float CaptureRange => Interactor != null ? Interactor.Range : k_fallbackRange;

    // 거리 기준점 — 조준 레이캐스트·윤곽선 게이트와 동일한 AimOrigin(카메라).
    // 루트(발밑) 기준이면 카메라 오프셋만큼 사거리 경계에서 판정이 어긋난다 (#147 관례, #184)
    private Vector3 AimOriginPosition =>
        Interactor != null ? Interactor.AimOrigin.position : transform.position;

    /// <summary>지금 연행 중인 NPC. 없으면 null. 서버(또는 오프라인)에서만 유효.</summary>
    public NpcController EscortingNpc { get; private set; }

    // 연행 여부를 클라이언트에도 알리는 동기화 플래그 — 서버만 기록한다.
    // 오너 클라의 Handcuffs가 "놓기/체포" 분기를 하려면 자기가 연행 중인지 알아야 하는데,
    // EscortingNpc는 서버에서만 세팅되므로 이 플래그가 없으면 클라에서 놓기가 안 된다 (#118 리뷰).
    private readonly NetworkVariable<bool> m_isEscortingSynced = new(false);

    /// <summary>연행 중 여부. 서버·오프라인은 실제 참조로, 원격 피어는 동기화 플래그로 판정.</summary>
    public bool IsEscorting => IsSpawned && !IsServer ? m_isEscortingSynced.Value : EscortingNpc != null;

    // 서버 채널링 생명주기(CTS 소유·재진입 가드)는 ServerChannel에 위임 (#109)
    private readonly ServerChannel m_channel = new();

    // ---- 오너 클라 진입점 (아이템/상호작용이 호출) ----

    /// <summary>체포 시도 — 오너가 호출. 서버/오프라인은 즉시 실행, 원격 클라는 서버로 요청을 넘긴다.</summary>
    public void RequestCapture(NpcController target)
    {
        if (target == null)
            return;

        // 서버(호스트 포함)·오프라인은 로컬 NpcController 참조로 바로 실행 — 네트워크 직렬화 불필요.
        // (호스트는 이미 대상을 들고 있어 RPC가 불필요하고, NetworkObjectReference는 스폰된 대상에서만
        //  생성 가능해 스폰 안 된 NPC를 넘기면 예외가 난다 — 이 우회가 호스트 검거 회귀를 막는다, #118)
        if (!IsSpawned || IsServer)
        {
            ServerBeginCapture(target);
            return;
        }
        if (!IsOwner)
            return; // 남의 플레이어 오브젝트에서 온 호출 방지

        if (!IsTargetNetworkReady(target))
            return;
        CaptureRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>채널링 취소 — 오너가 호출(이동·뗌 등).</summary>
    public void CancelCapture()
    {
        if (!IsSpawned) { ServerCancelCapture(); return; }
        if (!IsOwner) return;
        CancelCaptureRpc();
    }

    /// <summary>연행 놓기 — 오너가 호출.</summary>
    public void RequestRelease()
    {
        if (!IsSpawned) { Release(); return; }
        if (!IsOwner) return;
        ReleaseRpc();
    }

    /// <summary>도주 NPC 근접 제압 — 오너가 호출.</summary>
    public void RequestSubdueCapture(NpcController target)
    {
        if (target == null) return;
        if (!IsSpawned || IsServer) { ServerSubdueCapture(target); return; } // 서버/오프라인 즉시 실행
        if (!IsOwner) return;
        if (!IsTargetNetworkReady(target)) return;
        SubdueCaptureRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>체포되어 멈춘 NPC 재연행 — 오너가 호출(E, NpcSubdueInteractable). (#91)</summary>
    public void RequestEscort(NpcController target)
    {
        if (target == null) return;
        if (!IsSpawned || IsServer) { ServerEscort(target); return; } // 서버/오프라인 즉시 실행
        if (!IsOwner) return;
        if (!IsTargetNetworkReady(target)) return;
        EscortRequestRpc(new NetworkObjectReference(target.NetworkObject));
    }

    // 원격 클라 → 서버로 대상을 넘기려면 스폰돼 있어야 한다(NetworkObjectReference 제약).
    // 스폰 안 된 NPC(씬 배치 후 미스폰 등)면 참조 생성이 예외를 던지므로 미리 걸러 경고만 남긴다.
    private bool IsTargetNetworkReady(NpcController target)
    {
        if (target.NetworkObject != null && target.NetworkObject.IsSpawned)
            return true;
        Debug.LogWarning($"검거/제압 요청 무시 — 대상 NPC가 네트워크 스폰되지 않음: {target.name}", this);
        return false;
    }

    // ---- 서버 RPC (오너 → 서버) ----
    // NGO 2.x 유니버설 RPC: 오너가 자기 플레이어 오브젝트에서 서버로 보내므로 소유권 문제 없음

    [Rpc(SendTo.Server)]
    private void CaptureRequestRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerBeginCapture(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void CancelCaptureRpc() => ServerCancelCapture();

    [Rpc(SendTo.Server)]
    private void ReleaseRpc() => Release();

    [Rpc(SendTo.Server)]
    private void SubdueCaptureRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerSubdueCapture(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void EscortRequestRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out NpcController target))
        {
            ServerEscort(target);
        }
    }

    // ---- 서버 실행 (권위) ----

    /// <summary>체포 진입 — 대상 검증 후 채널링 시작. 서버(또는 오프라인)에서만 실행.</summary>
    private void ServerBeginCapture(NpcController target)
    {
        if (m_channel.IsActive)
            return; // 중복 채널링 방지
        if (IsEscorting)
            return; // 연행 중엔 체포 불가 — 놓기는 상호작용키(E)의 RequestRelease 전용 (#91)
        if (!NpcStateRules.IsCapturable(target.CurrentState))
            return; // 연행 중(가로채기 방지 #59)·체포됨(재연행은 E 경로 #91) — 클라 검증·윤곽선과 단일 기준 (#184)
        if (!IsInRange(target))
            return; // 사거리 밖이면 시작조차 안 함

        ServerChannelAsync(target).Forget();
    }

    private async UniTaskVoid ServerChannelAsync(NpcController target)
    {
        NotifyOwner($"구속 채널링 시작: {target.name} ({m_channelSeconds}초)");
        NotifyChannelGaugeStart(m_channelSeconds);

        // 프레임 루프 기반 keepAlive — 채널링 도중 거리 이탈을 즉시 실패시킨다 (#91, 도주형 NPC 대응 GDD 6장)
        ServerChannel.Result result;
        try
        {
            result = await m_channel.RunAsync(
                m_channelSeconds, () => target != null && IsInRange(target));
        }
        finally
        {
            // 완료·뗌·거리이탈·예외 어떤 경로로 끝나도 게이지 숨김을 보장한다 (#184)
            NotifyChannelGaugeEnd();
        }

        switch (result)
        {
            case ServerChannel.Result.OutOfRange:
                NotifyOwner("구속 실패 — 대상이 범위를 벗어남");
                return;

            case ServerChannel.Result.Canceled:
                NotifyOwner("구속 취소됨 (홀드 뗌)");
                return;

            case ServerChannel.Result.Completed:
                break; // 아래 반응 판정으로 진행
        }

        // 채널링 성공 순간 반응 판정 (GDD 6-1, #76)
        ReactionType reaction = ResolveReaction(target);
        switch (reaction)
        {
            case ReactionType.Flee:
                // 뿌리치고 도주 — 근접 제압 홀드 또는 테이저(후속)로만 잡힌다
                NotifyOwner($"체포 실패 — 뿌리치고 도주: {target.name}");
                target.StartFlee(transform); // 이 플레이어(서버측 transform)로부터 도주
                break;

            case ReactionType.Resist:
                // 그 자리에서 저항 — 제압 게이지를 깎아야 체포된다
                NotifyOwner($"체포 실패 — 저항 시작: {target.name}");
                target.StartResist();
                break;

            default:
                // 체포 성공 → 이 플레이어를 따라 연행 (#59)
                NotifyOwner($"NPC 구속됨: {target.name}");
                StartEscort(target);
                break;
        }
    }

    private void ServerCancelCapture() => m_channel.Cancel();

    /// <summary>도주 NPC 근접 제압 — 서버 실행. 도주 중일 때만 그 자리에서 체포.</summary>
    private void ServerSubdueCapture(NpcController target)
    {
        if (target.CurrentState == NpcState.Run)
            target.CaptureBySubdue();
    }

    /// <summary>재연행 — 서버 실행. 체포되어 멈춘 대상만 연행 시작(동시 1명 가드는 StartEscort). (#91)</summary>
    private void ServerEscort(NpcController target)
    {
        if (target.CurrentState == NpcState.Captured)
            StartEscort(target);
    }

    private bool IsInRange(NpcController target)
    {
        return (target.transform.position - AimOriginPosition).sqrMagnitude
            <= CaptureRange * CaptureRange;
    }

    /// <summary>채널링 성공 순간의 반응. 기절 중이거나 신원이 없으면 순응(즉시 연행) 취급. (#76)</summary>
    private static ReactionType ResolveReaction(NpcController target)
    {
        if (target.CurrentState == NpcState.Stunned)
            return ReactionType.Compliant;

        CitizenIdentity identity = target.GetComponent<CitizenIdentity>();
        return identity != null ? identity.Reaction : ReactionType.Compliant;
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

    // ---- 채널링 게이지 피드백 (#184) ----
    // NotifyOwner와 동일 분기 — 호스트 오너·오프라인은 직접 호출, 원격 오너에게만 RPC.
    // 데디케이티드 서버 등 HUD가 없는 환경에선 Instance가 null이라 무동작(안전).

    private void NotifyChannelGaugeStart(float seconds)
    {
        if (IsSpawned && IsServer && !IsOwner)
        {
            ChannelGaugeStartRpc(seconds);
            return;
        }
        ChannelingGaugeUI.Instance?.Show(seconds);
    }

    private void NotifyChannelGaugeEnd()
    {
        if (IsSpawned && IsServer && !IsOwner)
        {
            ChannelGaugeEndRpc();
            return;
        }
        ChannelingGaugeUI.Instance?.Hide();
    }

    [Rpc(SendTo.Owner)]
    private void ChannelGaugeStartRpc(float seconds) => ChannelingGaugeUI.Instance?.Show(seconds);

    [Rpc(SendTo.Owner)]
    private void ChannelGaugeEndRpc() => ChannelingGaugeUI.Instance?.Hide();

    // ---- 서버 내부 연행 상태 조작 ----

    /// <summary>연행 시작. 이미 다른 NPC를 연행 중이면 무시된다 (동시 1명 제약). 서버(또는 오프라인) 실행.</summary>
    public void StartEscort(NpcController npc)
    {
        if (IsSpawned && !IsServer)
            return; // 연행 상태는 서버 권위 — NpcController 상태 메서드와 동일한 방어 컨벤션 (#118 리뷰)
        if (IsEscorting || npc == null)
            return;

        SetEscorting(npc);
        npc.StartEscort(transform);
        NotifyOwner($"연행 시작: {npc.name}");
    }

    /// <summary>연행 놓기 — NPC는 그 자리에서 체포 상태로 멈춘다. 다시 다가가 재연행 가능. 서버(또는 오프라인) 실행.</summary>
    public void Release()
    {
        if (IsSpawned && !IsServer)
            return; // 서버 권위 방어 — 클라 직접 호출은 무시 (요청은 RequestRelease 경유)
        if (!IsEscorting)
            return;

        NotifyOwner($"연행 놓기: {EscortingNpc.name} — 그 자리에서 체포 상태로 정지");
        EscortingNpc.StopEscort();
        SetEscorting(null);
    }

    // EscortingNpc와 동기화 플래그를 함께 갱신 — 서버(또는 오프라인)에서만 호출된다
    private void SetEscorting(NpcController npc)
    {
        EscortingNpc = npc;
        if (IsSpawned && IsServer)
            m_isEscortingSynced.Value = npc != null;
    }

    private void Update()
    {
        // 참조 정리는 서버(또는 오프라인)에서만 — 연행 상태 자체가 서버 권위다 (#56/#118).
        // 클라이언트에서는 EscortingNpc가 서버 로직으로만 세팅되므로 여기서 건드리지 않는다.
        if (IsSpawned && !IsServer)
            return;

        // 거리 이탈 등으로 NPC 쪽에서 연행이 스스로 풀린 경우 참조를 정리한다
        if (EscortingNpc != null && EscortingNpc.CurrentState != NpcState.Escorted)
            SetEscorting(null);
    }

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
