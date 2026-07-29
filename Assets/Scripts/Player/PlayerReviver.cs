using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 다운된 동료 구조(리바이브) — 서버 권위 채널링. (#105, GDD 7-5)
/// 오너가 다운된 아군을 조준한 채 상호작용 버튼을 누르고 있으면(홀드) 서버가 T초 채널링을 돌리고,
/// 완료 시 대상의 HP를 일부 회복시켜 무력화를 해제한다(PlayerData.ServerRevive).
/// 버튼을 떼거나 대상이 사거리를 벗어나면 실패. 서버 권위·RPC 구조는 PlayerEscorter를 본뜬다.
/// </summary>
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerReviver : ChanneledInteractionBehaviour
{
    [Header("구조 채널링 (서버 권위)")]
    [Tooltip("구조 채널링 시간(초)")]
    [SerializeField] private float m_reviveSeconds = 3f;

    // 사거리는 조준·윤곽선과 같은 기준 — PlayerInteractor.Range 재사용 (#147 패턴, #184)
    private const float k_fallbackRange = 3f; // 테스트 구성 등 PlayerInteractor가 없을 때

    private PlayerInputHandler m_inputHandler;
    private PlayerInteractor m_interactor;      // 조준 대상 조회용
    private PlayerIncapacitation m_incapacitation; // 내가 다운 중이면 구조 불가
    private PlayerData m_selfData;              // 자기 자신 제외 판정용

    // 서버 채널링 생명주기(CTS 소유·재진입 가드)는 ServerChannel에 위임 (#109)
    private readonly ServerChannel m_channel = new();

    /// <summary>지금 조준 중인 '다운된 아군'. 없으면 null. 임시 구조 HUD 프롬프트용(오너 전용). (#105)</summary>
    public PlayerData CurrentReviveTarget => IsOwner ? FindAllyTarget(IncapacitationCause.Down) : null;

    /// <summary>
    /// 지금 조준 중인 '기능 정지(Die)된 아군'. 없으면 null. 구조 불가 안내용(오너 전용). (#364)
    /// 히트박스가 Die에서도 켜져 윤곽선은 잡히는데(운반 조준용, #365) 구조는 거부되므로,
    /// 안내가 없으면 "조준은 되는데 홀드해도 아무 일이 없는" 상태가 된다.
    /// </summary>
    public PlayerData CurrentDeadTarget => IsOwner ? FindAllyTarget(IncapacitationCause.Die) : null;

    public override void OnNetworkSpawn()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_interactor = GetComponent<PlayerInteractor>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_selfData = GetComponent<PlayerData>();

        // 입력 구독은 오너만 — 서버 RPC 수신·채널링은 enabled와 무관하게 동작하므로
        // (PlayerEscorter처럼) 컴포넌트를 비활성화하지 않는다.
        if (IsOwner)
        {
            m_inputHandler.OnInteractStarted += HandleInteractStarted;
            m_inputHandler.OnInteractCanceled += HandleInteractCanceled;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner)
        {
            m_inputHandler.OnInteractStarted -= HandleInteractStarted;
            m_inputHandler.OnInteractCanceled -= HandleInteractCanceled;
        }
        ServerCancelRevive();
    }

    // ---- 오너 입력 핸들러 ----

    private void HandleInteractStarted()
    {
        // 내가 다운 중이면 구조할 수 없다
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return;

        PlayerData target = FindAllyTarget(IncapacitationCause.Down);
        if (target != null)
            RequestBeginRevive(target);
    }

    private void HandleInteractCanceled() => RequestCancelRevive();

    // 조준 중인 대상이 지정한 무력화 원인의 아군이면 그 PlayerData를, 아니면 null을 반환한다. (#105, #364)
    private PlayerData FindAllyTarget(IncapacitationCause cause)
    {
        GameObject targetObj = m_interactor != null ? m_interactor.CurrentTarget : null;
        if (targetObj == null)
            return null;

        PlayerData target = targetObj.GetComponentInParent<PlayerData>();
        if (target == null || target == m_selfData)
            return null; // 자기 자신 제외

        PlayerIncapacitation targetIncap = target.GetComponent<PlayerIncapacitation>();
        return targetIncap != null && targetIncap.Cause == cause ? target : null;
    }

    // ---- 오너 클라 진입점 (서버/오프라인은 즉시 실행, 원격 클라는 서버로 요청) ----

    /// <summary>구조 채널링 시작 요청 — 오너가 호출.</summary>
    public void RequestBeginRevive(PlayerData target)
    {
        if (target == null)
            return;

        // 서버(호스트 포함)·오프라인은 로컬 참조로 바로 실행 (PlayerEscorter.RequestCapture 관례)
        if (!IsSpawned || IsServer)
        {
            ServerBeginRevive(target);
            return;
        }
        if (!IsOwner)
            return; // 남의 플레이어 오브젝트에서 온 호출 방지
        if (!IsTargetNetworkReady(target))
            return;
        BeginReviveRpc(new NetworkObjectReference(target.NetworkObject));
    }

    /// <summary>구조 채널링 취소 요청 — 오너가 호출(버튼 뗌).</summary>
    public void RequestCancelRevive()
    {
        if (!IsSpawned) { ServerCancelRevive(); return; }
        if (!IsOwner) return;
        CancelReviveRpc();
    }

    // 원격 클라 → 서버로 대상을 넘기려면 스폰돼 있어야 한다(NetworkObjectReference 제약).
    private bool IsTargetNetworkReady(PlayerData target)
    {
        if (target.NetworkObject != null && target.NetworkObject.IsSpawned)
            return true;
        Debug.LogWarning($"구조 요청 무시 — 대상이 네트워크 스폰되지 않음: {target.name}", this);
        return false;
    }

    // ---- 서버 RPC (오너 → 서버) ----

    [Rpc(SendTo.Server)]
    private void BeginReviveRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObj) &&
            targetObj.TryGetComponent(out PlayerData target))
        {
            ServerBeginRevive(target);
        }
    }

    [Rpc(SendTo.Server)]
    private void CancelReviveRpc() => ServerCancelRevive();

    // ---- 서버 실행 (권위) ----

    private void ServerBeginRevive(PlayerData target)
    {
        if (m_channel.IsActive || target == null)
            return;

        // --- [Issue #148] 변조된 클라이언트의 비정상 RPC 호출 방어를 위한 서버 측 검증 ---
        
        // 1. 구조자가 다운된 상태인지 검증
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
        {
            Debug.LogWarning($"[Server] 다운 상태인 플레이어({m_selfData.name})가 구조를 시도하여 거부됨.");
            return;
        }

        // 2. 구조 대상이 자기 자신인지 검증 (자가 구조 방지)
        if (target == m_selfData)
        {
            Debug.LogWarning($"[Server] 플레이어({m_selfData.name})가 자가 구조(Self-revive)를 시도하여 거부됨.");
            return;
        }
        // --------------------------------------------------------------------------------

        PlayerIncapacitation targetIncap = target.GetComponent<PlayerIncapacitation>();
        if (targetIncap == null || !targetIncap.IsDowned)
        {
            // HP0 다운만 구조 대상 — 매달기(#101)·기절(#252)은 스스로 풀린다
            // (둘 다 히트박스가 꺼져 조준도 안 되지만 위조 RPC 방어로 여기서도 본다)
            // Die는 히트박스가 켜져 있어 실제로 여기까지 온다 — 조준·홀드가 되는데 침묵하면 버그로 보인다 (#364)
            if (targetIncap != null && targetIncap.IsDead)
                NotifyOwner($"구조 불가 — {target.name}은 기능 정지 상태다. 본부로 이송해야 복구된다");
            return;
        }
        if (!IsInRange(target))
            return; // 사거리 밖이면 시작조차 안 함

        ServerChannelAsync(target, targetIncap).Forget();
    }

    private async UniTaskVoid ServerChannelAsync(PlayerData target, PlayerIncapacitation targetIncap)
    {
        NotifyOwner($"구조 채널링 시작: {target.name} ({m_reviveSeconds}초)");
        NotifyChannelGaugeStart(m_reviveSeconds);

        // keepAlive 생략 — 단일 Delay로 대기하고, 완료 시점에만 거리·중복복구를 검사한다 (기존 동작 유지)
        ServerChannel.Result result;
        try
        {
            result = await m_channel.RunAsync(m_reviveSeconds);
        }
        finally
        {
            // 완료·뗌·예외 어떤 경로로 끝나도 게이지 숨김을 보장한다 (#184)
            NotifyChannelGaugeEnd();
        }

        switch (result)
        {
            case ServerChannel.Result.Canceled:
                NotifyOwner("구조 취소됨 (홀드 뗌)");
                return;

            case ServerChannel.Result.OutOfRange:
                // PlayerReviver는 keepAlive를 넘기지 않아 이 사유는 발생하지 않는다 — 완료 시점 검사가 담당.
                break;

            case ServerChannel.Result.Completed:
                break;
        }

        // 채널링 동안 대상이 파괴됐거나 사거리를 벗어났으면 실패
        if (target == null || !IsInRange(target))
        {
            NotifyOwner("구조 실패 — 대상이 범위를 벗어남");
            return;
        }
        // 다른 동료가 먼저 살렸다면 중복 구조 방지.
        // 채널링(3초) 도중 구조 제한시간이 끝나 Die로 떨어졌을 수도 있다 — 한 발 늦은 구조는 실패다 (#364)
        if (!targetIncap.IsDowned)
        {
            NotifyOwner(
                targetIncap.IsDead
                    ? $"구조 실패 — 제한시간 초과로 기능 정지됨: {target.name} (본부 이송 필요)"
                    : "구조 취소 — 대상이 이미 복구됨"
            );
            return;
        }

        NotifyOwner($"구조 완료: {target.name}");
        target.ServerRevive();
    }

    private void ServerCancelRevive() => m_channel.Cancel();

    private bool IsInRange(PlayerData target)
    {
        float range = m_interactor != null ? m_interactor.Range : k_fallbackRange;
        // 기준점은 조준·윤곽선 게이트와 동일한 AimOrigin(카메라) (#184)
        Vector3 origin = m_interactor != null ? m_interactor.AimOrigin.position : transform.position;
        // 사거리 + 가시선 — 거리만 보면 위조 RPC로 벽 너머 구조가 된다 (#360)
        return (target.transform.position - origin).sqrMagnitude <= range * range
            && (m_interactor == null || m_interactor.HasLineOfSightTo(target.transform));
    }

    // ---- 오너 로그 피드백 ----

    // 판정 로그는 서버에서 찍히므로 원격 클라 오너는 결과를 볼 수 없다 — 오너 콘솔에도 같은 로그를 전달한다.
    // Scanner.NotifyOwner/PlayerEscorter.NotifyOwner와 동일 패턴 (#109).
    private void NotifyOwner(string message)
    {
        Debug.Log(message); // 서버(호스트)·오프라인 콘솔
        if (IsSpawned && IsServer && !IsOwner)
            OwnerLogRpc(message); // 원격 클라가 오너인 경우에만 전달 (호스트 오너는 위에서 이미 찍음)
    }

    [Rpc(SendTo.Owner)]
    private void OwnerLogRpc(string message) => Debug.Log($"[서버 판정] {message}");

    // 채널링 게이지 피드백(NotifyChannelGaugeStart/End)은 기반 ChanneledInteractionBehaviour가 제공한다. (#184)

    public override void OnDestroy()
    {
        m_channel.Dispose();
        base.OnDestroy(); // NetworkBehaviour 내부 정리 — 반드시 호출
    }
}