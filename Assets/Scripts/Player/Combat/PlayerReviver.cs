using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 다운된 동료 구조(리바이브) — 서버 권위 채널링. (#105, GDD 7-5, #725)
/// 오너가 다운된 아군을 조준한 채 E를 <b>탭</b>하면(꾹 누르지 않는다) 서버가 T초 채널링을 돌리고,
/// 완료 시 대상의 HP를 일부 회복시켜 무력화를 해제한다(PlayerHealth.ServerRevive).
/// 서버 권위·RPC 구조는 PlayerEscorter를 본뜬다.
///
/// <b>채널링 중 취소 조건</b>(<see cref="HandleCancelTrigger"/>)은 이동 입력·아이템 사용·버리기·E
/// 재입력(토글)이다. 사거리 이탈·대상의 유예 만료·나 자신의 무력화는 서버 keepAlive
/// (<see cref="ServerChannelAsync"/>)가 이미 본다 — 오너 쪽은 입력 감시만 하면 된다.
///
/// 채널링 시작/종료마다 대상의 <c>PlayerIncapacitation.ServerSetBeingRevived</c>를 불러 다운 유예
/// 시계를 얼리고 되살린다 — 자세한 근거는 그쪽 문서에 있다.
/// </summary>
[RequireComponent(typeof(PlayerInputHandler))]
[RequireComponent(typeof(ChannelGauge))]
public class PlayerReviver : ChanneledInteractionBehaviour
{
    private ChannelGauge m_gauge;

    // 프리팹 직렬화에 의존하므로 lazy로 잡는다 — RequireComponent는 기존 프리팹 자산을 소급 보정하지 않는다.
    // 같은 플레이어 오브젝트의 PlayerEscortCommands와 이 컴포넌트를 공유한다 (게이지 토큰 주의 — 후속 이슈).
    private ChannelGauge Gauge
    {
        get
        {
            if (m_gauge == null)
            {
                m_gauge = GetComponent<ChannelGauge>();
                if (m_gauge == null)
                    Debug.LogError(
                        "PlayerReviver: ChannelGauge가 프리팹에 없다 — 프리팹을 열어 추가하고 저장할 것",
                        this
                    );
            }
            return m_gauge;
        }
    }

    [Header("구조 채널링 (서버 권위)")]
    [Tooltip("구조 채널링 시간(초)")]
    [SerializeField]
    private float m_reviveSeconds = 3f;

    // 사거리는 조준·윤곽선과 같은 기준 — PlayerInteractor.Range 재사용 (#147 패턴, #184)
    private const float k_fallbackRange = 3f; // 테스트 구성 등 PlayerInteractor가 없을 때

    private PlayerInputHandler m_inputHandler;
    private PlayerInteractor m_interactor; // 조준 대상 조회용
    private PlayerIncapacitation m_incapacitation; // 내가 다운 중이면 구조 불가
    private PlayerHealth m_selfHealth; // 자기 자신 제외 판정용

    // 서버 채널링 생명주기(CTS 소유·재진입 가드)는 ServerChannel에 위임 (#109)
    private readonly ServerChannel m_channel = new();

    // 오너 로컬 상태 — "지금 내가 구조 채널링 중인가". 서버 m_channel.IsActive는 원격 오너에게는
    // 항상 false라(채널링이 서버 인스턴스에서만 돈다) E 재입력의 토글 여부를 이걸로 판단한다.
    // 서버가 채널 시작/종료를 확정할 때만(ServerNotifyChannelStarted/Ended) 바뀐다 — 낙관적으로
    // 미리 켜면 서버가 거부했을 때 이 값만 계속 true로 남아 다음 E가 '취소'로 오작동한다. (#725)
    private bool m_isChanneling;

    // 전 피어 동기화 버전 — 구조 채널링 모션은 오너뿐 아니라 제3자·구조자 본인 3인칭 화면에도 보여야
    // 하므로, 오너 전용인 m_isChanneling과 별도로 서버 권위 값을 둔다. (PlayerAnimationDriver가 읽는다, #725)
    private readonly NetworkVariable<bool> m_isChannelingSynced = new NetworkVariable<bool>();

    /// <summary>지금 구조 채널링 중인가 — 전 피어에서 같은 답을 본다. 채널링 모션 구동용. (#725)</summary>
    public bool IsChanneling =>
        IsSpawned && !IsServer ? m_isChannelingSynced.Value : m_channel.IsActive;

    /// <summary>지금 조준 중인 '다운된 아군'. 없으면 null. 임시 구조 HUD 프롬프트용(오너 전용). (#105)</summary>
    public PlayerHealth CurrentReviveTarget =>
        IsOwner ? FindAllyTarget(IncapacitationCause.Down) : null;

    /// <summary>
    /// 지금 조준 중인 '기능 정지(Die)된 아군'. 없으면 null. 구조 불가 안내용(오너 전용). (#364)
    /// 히트박스가 Die에서도 켜져 윤곽선은 잡히는데(운반 조준용, #365) 구조는 거부되므로,
    /// 안내가 없으면 "조준은 되는데 홀드해도 아무 일이 없는" 상태가 된다.
    /// </summary>
    public PlayerHealth CurrentDeadTarget =>
        IsOwner ? FindAllyTarget(IncapacitationCause.Die) : null;

    public override void OnNetworkSpawn()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_interactor = GetComponent<PlayerInteractor>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_selfHealth = GetComponent<PlayerHealth>();

        // 입력 구독은 오너만 — 서버 RPC 수신·채널링은 enabled와 무관하게 동작하므로
        // (PlayerEscorter처럼) 컴포넌트를 비활성화하지 않는다.
        if (IsOwner)
        {
            m_inputHandler.OnInteractStarted += HandleInteractStarted;
            m_inputHandler.OnUseItemStarted += HandleCancelTrigger;
            m_inputHandler.OnDropItem += HandleCancelTrigger;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner)
        {
            m_inputHandler.OnInteractStarted -= HandleInteractStarted;
            m_inputHandler.OnUseItemStarted -= HandleCancelTrigger;
            m_inputHandler.OnDropItem -= HandleCancelTrigger;
        }
        ServerCancelRevive();
    }

    // 오너 전용 — 채널링 중 이동 입력을 감시한다. 다른 취소 조건은 이벤트라 구독으로 잡히지만,
    // 이동은 값이 계속 바뀌는 상태라 폴링이 맞다 (PlayerInteractor.Update 관례).
    private void Update()
    {
        if (!IsOwner || !m_isChanneling)
            return;

        if (m_inputHandler != null && m_inputHandler.MoveInput.sqrMagnitude > 0.0001f)
            HandleCancelTrigger();
    }

    // ---- 오너 입력 핸들러 ----

    // E — 채널링 중이 아니면 시작, 채널링 중이면 토글 취소. (#725)
    private void HandleInteractStarted()
    {
        if (m_isChanneling)
        {
            RequestCancelRevive();
            return;
        }

        // 내가 다운 중이면 구조할 수 없다
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return;

        PlayerHealth target = FindAllyTarget(IncapacitationCause.Down);
        if (target != null)
            RequestBeginRevive(target);
    }

    // 이동·아이템 사용·버리기 — 채널링 중일 때만 취소로 이어진다. (#725)
    private void HandleCancelTrigger()
    {
        if (m_isChanneling)
            RequestCancelRevive();
    }

    // 조준 중인 대상이 지정한 무력화 원인의 아군이면 그 PlayerHealth를, 아니면 null을 반환한다. (#105, #364)
    private PlayerHealth FindAllyTarget(IncapacitationCause cause)
    {
        GameObject targetObj = m_interactor != null ? m_interactor.CurrentTarget : null;
        if (targetObj == null)
            return null;

        PlayerHealth target = targetObj.GetComponentInParent<PlayerHealth>();
        if (target == null || target == m_selfHealth)
            return null; // 자기 자신 제외

        PlayerIncapacitation targetIncap = target.GetComponent<PlayerIncapacitation>();
        if (targetIncap == null || targetIncap.Cause != cause)
            return null;

        // 회수 불가한 몸(맨홀 납치, #775)은 부활 대상이 아니다 — 조준 프롬프트도 뜨면 안 된다.
        // Down 분기는 영향받지 않는다 — 몸 회수 불가는 Die로 확정된 뒤에만 성립한다.
        if (cause == IncapacitationCause.Die && !targetIncap.IsRevivable)
            return null;

        return target;
    }

    // ---- 오너 클라 진입점 (서버/오프라인은 즉시 실행, 원격 클라는 서버로 요청) ----

    /// <summary>구조 채널링 시작 요청 — 오너가 호출.</summary>
    public void RequestBeginRevive(PlayerHealth target)
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

    // 원격 클라 → 서버로 대상을 넘기려면 스폰돼 있어야 한다(NetworkObjectReference 제약).
    private bool IsTargetNetworkReady(PlayerHealth target)
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

    // ---- 서버 실행 (권위) ----

    private void ServerBeginRevive(PlayerHealth target)
    {
        if (m_channel.IsActive || target == null)
            return;

        // --- [Issue #148] 변조된 클라이언트의 비정상 RPC 호출 방어를 위한 서버 측 검증 ---

        // 1. 구조자가 다운된 상태인지 검증
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
        {
            Debug.LogWarning(
                $"[Server] 다운 상태인 플레이어({m_selfHealth.name})가 구조를 시도하여 거부됨."
            );
            return;
        }

        // 2. 구조 대상이 자기 자신인지 검증 (자가 구조 방지)
        if (target == m_selfHealth)
        {
            Debug.LogWarning(
                $"[Server] 플레이어({m_selfHealth.name})가 자가 구조(Self-revive)를 시도하여 거부됨."
            );
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
                NotifyOwner(
                    $"구조 불가 — {target.name}은 기능 정지 상태다. 부활 키트로 일으켜야 한다 (#613)"
                );
            return;
        }
        if (!IsInRange(target))
            return; // 사거리 밖이면 시작조차 안 함

        ServerChannelAsync(target, targetIncap).Forget();
    }

    private async UniTaskVoid ServerChannelAsync(
        PlayerHealth target,
        PlayerIncapacitation targetIncap
    )
    {
        NotifyOwner($"구조 채널링 시작: {target.name} ({m_reviveSeconds}초)");
        // 루프음이 게이지와 같은 경로라 "취소했는데 소리가 계속 난다"가 구조적으로 생기지 않는다 (#725)
        Gauge?.Begin(m_reviveSeconds, EAudioClip.ReviveLoop);
        ServerNotifyChannelStarted();

        // 다운 유예 시계를 얼린다 — 채널링 중에는 셧다운이 멈추고, 아래 finally에서 항상 되살린다 (#725)
        targetIncap.ServerSetBeingRevived(true, m_reviveSeconds);

        // 대상이 구조 대상이 아니게 되거나(#364) 내가 도중에 무력화되면(#725) 즉시 끊는다 — 게이지가
        // 끝까지 차오른 뒤에야 실패를 통보받는 것을 막는다. 거리 검사는 종전대로 완료 시점에만 한다.
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
            // 완료·취소·예외 어떤 경로로 끝나도 게이지 숨김과 유예 시계 해동을 보장한다 (#184, #725)
            Gauge?.End();
            targetIncap.ServerSetBeingRevived(false);
            ServerNotifyChannelEnded();
        }

        switch (result)
        {
            case ServerChannel.Result.Canceled:
                NotifyOwner("구조 취소됨");
                return;

            case ServerChannel.Result.OutOfRange:
                // 여기서는 '거리 이탈'이 아니라 대상이 구조 대상에서 벗어난 것이다 (keepAlive, #364).
                NotifyOwner(
                    target != null && targetIncap.IsDead
                        ? $"구조 중단 — 제한시간 초과로 기능 정지됨: {target.name} (본부 이송 필요)"
                        : "구조 중단 — 대상이 구조 대상이 아니게 됨"
                );
                return;

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
        GetComponent<PlayerAssistCredit>()?.ServerCreditRescue(); // 정산 "최다 팀원 구조" 집계 (#739)
    }

    private void ServerCancelRevive() => m_channel.Cancel();

    // m_isChanneling은 오너 로컬 상태라, 원격 오너에게는 서버가 확정 시점에만 RPC로 알려 바꾼다
    // (NotifyOwner·ChannelGauge.Begin과 동일 관례). 호스트 오너·오프라인은 직접 대입. (#725)
    // 동기화 NetworkVariable은 전 피어가 보므로 여기서 함께 쓴다 — 서버 컨텍스트에서만 호출되니 안전하다.
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

    // 채널링 게이지와 오너 피드백(NotifyOwner)은 기반 ChanneledInteractionBehaviour가 제공한다. (#184/#91)

    public override void OnDestroy()
    {
        m_channel.Dispose();
        base.OnDestroy(); // NetworkBehaviour 내부 정리 — 반드시 호출
    }
}
