using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// PlayerEscorter의 밧줄 묶기·끌기(#269/#369) — 본체와 partial로 분리. NPC를 밧줄 장력으로
/// 매 프레임 끌어당기는 서버 권위 로직과 그 상태·동기화 필드를 담는다.
/// </summary>
public partial class PlayerEscorter
{
    [Header("밧줄 끌기 (#269)")]
    [Tooltip("밧줄 길이(m) — 이 거리를 넘어야 NPC가 끌려온다. 안쪽이면 밧줄이 늘어져 당기지 않는다")]
    [SerializeField] private float m_ropeLength = 1.6f;

    [Tooltip("끌리는 몸이 목표 위치를 따라잡는 데 걸리는 시간(초) — 클수록 늦게, 크게 휘며 따라온다")]
    [SerializeField] private float m_dragSmoothTime = 0.14f;

    [Tooltip("몸이 밧줄 방향으로 도는 민감도(1/초) — 클수록 즉각 방향을 맞춘다")]
    [SerializeField] private float m_dragTurnSharpness = 6f;

    [Tooltip("끌리며 좌우로 흔들리는 최대 각(도) — 0이면 흔들리지 않는다")]
    [SerializeField] private float m_dragSwayAngle = 7f;

    [Tooltip("흔들림 주기 — 끌린 거리 1m당 위상(라디안)")]
    [SerializeField] private float m_dragSwayFrequency = 1.6f;

    [Tooltip("이 거리(m)를 넘게 멀어지면 밧줄이 끊겨 NPC가 풀려난다 — 벽에 막혀 못 따라오거나 놓아둔 채 걸어가면 발생. 밧줄 길이보다 넉넉해야 한다")]
    [SerializeField] private float m_ropeBreakDistance = 10f;

    /// <summary>지금 밧줄로 끌고 있는 NPC. 없으면 null. 서버(또는 오프라인)에서만 유효. (#269)</summary>
    public NpcController DraggingNpc { get; private set; }

    /// <summary>
    /// 지금 이 플레이어의 밧줄에 묶여 있는 NPC — 끌기를 멈춰도(E) 유지된다. 서버(또는 오프라인)에서만 유효. (#369)
    /// 놓기는 손에서 줄을 놓는 게 아니라 <b>끌기를 멈추는 것</b>이다: 대상은 묶인 채 그 자리에 서고
    /// 밧줄은 여전히 이 플레이어와 이어져 있다. 실제로 푸는 건 밧줄 좌클릭 채널링(풀기)뿐이고,
    /// 그 외에는 인계 판정·방치 탈주처럼 대상이 커스터디를 벗어날 때 저절로 끊긴다.
    /// </summary>
    public NpcController TetheredNpc { get; private set; }

    // 묶여 있는 대상을 클라이언트에도 알린다 — 서버만 기록한다(연행 플래그와 동일 관례).
    // 단순 bool이 아니라 대상 참조인 이유: 원격 피어의 밧줄 표시(RopeDragView)가 선의 양 끝점을
    // 알아야 하는데, TetheredNpc는 서버에서만 세팅되므로 누구와 이어져 있는지 알 방법이 없다.
    private readonly NetworkVariable<NetworkObjectReference> m_tetheredNpcSynced = new();

    // 끌고 있는 중인지 — 오너 클라의 입력 게이트(아이템 사용 차단·E 놓기)가 봐야 해서 따로 동기화한다.
    // 묶여 있음(위)과 다르다: 놓은 뒤에도 줄은 이어져 있지만 끌고 있지는 않다. (#369)
    private readonly NetworkVariable<bool> m_isDraggingSynced = new(false);

    // 끌기 추종 상태 (서버·오프라인 전용) — 매 프레임 이어지는 값이라 SetDragging에서 초기화한다.
    private Vector3 m_dragVelocity;   // SmoothDamp 관성
    private Quaternion m_dragFacing;  // 흔들림을 뺀 몸 방향 — 여기에 sway를 얹어 최종 회전을 만든다
    private float m_dragTravel;       // 끌린 누적 거리(m) — 흔들림 위상의 기준

    /// <summary>밧줄 끌기 중 여부. 서버·오프라인은 실제 참조로, 원격 피어는 동기화 플래그로 판정. (#269)</summary>
    public bool IsDragging =>
        IsSpawned && !IsServer ? m_isDraggingSynced.Value : DraggingNpc != null;

    /// <summary>밧줄이 어딘가에 묶여 있는가 — 끌고 있지 않아도 참이다. 새 대상을 묶는 것을 막는다. (#369)</summary>
    public bool IsTethered =>
        IsSpawned && !IsServer ? m_tetheredNpcSynced.Value.NetworkObjectId != 0 : TetheredNpc != null;

    /// <summary>묶여 있는 NPC의 트랜스폼 — 전 피어에서 유효한 표현 계층용 접근자. 없으면 null. (#269/#369)</summary>
    public Transform TetheredNpcTransform
    {
        get
        {
            if (TetheredNpc != null) return TetheredNpc.transform;
            if (!IsSpawned) return null;

            // 세션이 내려가는 중에는 NetworkManager가 이미 사라져 있는데, TryGet은 내부에서 그것을
            // 참조하므로 그대로 부르면 NullReferenceException이 난다. IsSpawned만으로는 이 순간을
            // 거를 수 없다 — 디스폰 통지보다 매니저 소멸이 앞설 수 있어, 라운드 종료 후 씬이 바뀌는
            // 동안 표시(RopeDragView.LateUpdate)가 매 프레임 예외를 뱉는다.
            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsListening) return null;

            return m_tetheredNpcSynced.Value.TryGet(out NetworkObject npcObject, manager) ? npcObject.transform : null;
        }
    }

    /// <summary>밧줄 길이(m) — 표시(늘어짐 정도)와 서버 장력 판정이 같은 값을 쓴다. (#269)</summary>
    public float RopeLength => m_ropeLength;

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

    /// <summary>밧줄 묶기 진입 — 검증 후 채널링을 시작한다. 서버(또는 오프라인) 실행. (#369)</summary>
    private void ServerBeginRopeDrag(NpcController target)
    {
        if (!CanBeginRopeDrag(target))
            return;
        if (!NpcStateRules.CanArrest(target.CurrentState))
            return; // 이미 신병이 확보됐거나 다른 시스템이 소유한 상태 제외 — 클라 검증·윤곽선과 단일 기준 (#184)

        // 기절 대상은 채널링 없이 즉시 묶는다 — 기절 지속(2.67초)이 채널(3초)보다 짧아 채널을 걸면
        // 묶기 전에 깨어나 테이저→밧줄 콤보가 깨진다. (#269)
        // 상태값이 아니라 IsStunned를 보는 이유: 스턴이 오버레이가 되면서 테이저 기절은 CurrentState를
        // 바꾸지 않는다(넉백 KO만 NpcState.Stunned). 상태로 보면 이 지름길이 조용히 죽어
        // 기절 대상에게도 채널링을 요구하게 되고, 깨어나기 전에 못 묶어 콤보가 깨진다. (#292)
        if (target.IsStunned)
        {
            ServerApplyRopeDrag(target);
            return;
        }

        ServerRopeChannelAsync(target).Forget();
    }

    /// <summary>밧줄 끌기 재개 — 이미 체포되어 멈춘 대상을 채널링·반응 판정 없이 즉시 다시 끈다. (#369)
    /// 수갑 시절의 재연행(ServerEscort)이 그랬듯, 이미 확보된 신병에 반응 판정을 다시 굴리면
    /// 잡아 둔 대상이 그 자리에서 도망치게 된다.</summary>
    private void ServerResumeRopeDrag(NpcController target)
    {
        if (!CanBeginRopeDrag(target))
            return;
        if (!NpcStateRules.CanRelease(target.CurrentState))
            return; // 체포되어 멈춘 대상만

        ServerApplyRopeDrag(target);
    }

    // 묶기·재개가 공유하는 진입 조건 — 상태 게이트만 각자 다르다.
    private bool CanBeginRopeDrag(NpcController target)
    {
        if (m_channel.IsActive)
            return false;
        if (IsBusy)
            return false; // 한 번에 1명
        if (IsCarryingPlayer)
            return false; // 동료를 끌고 있으면 그 줄이 쓰이는 중이다 (#365)
        if (!HasRope)
            return false;
        if (TetheredNpc != null && TetheredNpc != target)
            return false; // 밧줄은 하나뿐 — 다른 대상에 묶여 있으면 먼저 풀어야 한다 (#369)
        return IsInRange(target);
    }

    private async UniTaskVoid ServerRopeChannelAsync(NpcController target)
    {
        NotifyOwner($"밧줄 묶기 채널링 시작: {target.name} ({m_channelSeconds}초)");
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
                NotifyOwner("묶기 실패 — 대상이 범위를 벗어남");
                return;

            case ServerChannel.Result.Canceled:
                NotifyOwner("묶기 취소됨 (홀드 뗌)");
                return;
        }

        // 채널링 도중 상태가 바뀌었을 수 있다 — 완료 시점에 재확인(다른 플레이어가 먼저 확보 등).
        if (target == null || !NpcStateRules.CanArrest(target.CurrentState) || IsBusy)
            return;

        // 반응 판정은 여기서 굴리지 않는다 (#400) — 밧줄은 순수 검거 수단이 됐고, 판정은
        // NpcController.ServerReactTo가 단독으로 갖는다. 함부로 묶는 것을 막던 장치도 함께 사라졌다 —
        // 이제는 이미 반응 중인 대상이 CanArrest에서 걸리는 것이 그 역할을 대신한다.
        ServerApplyRopeDrag(target);
    }

    // 실제 끌기 진입 — 검증·판정이 끝난 뒤의 상태 조작만 담당한다. 서버(또는 오프라인).
    private void ServerApplyRopeDrag(NpcController target)
    {
        SetTethered(target);
        SetDragging(target);

        // 커스터디 상태는 수갑 연행과 같은 Escorted를 재사용한다 — 인계존·이벤트 수명·가로채기 방지가
        // 이미 이 상태를 기준으로 판정하기 때문. 이동은 밧줄 장력이 하고 NpcEscortedState가 IsRoped를 보고
        // 추종을 건너뛴다. 상태 전이가 StartRopeDrag(에이전트 끄기)보다 먼저다 — 뒤집으면 직전 상태 Exit이
        // 꺼진 에이전트에 isStopped를 써 에러가 난다(넉백 ServerApplyKnockback과 같은 순서). (#369)
        target.StartEscort(transform);
        target.StartRopeDrag(transform); // 끈 플레이어를 위협으로 기억 — 풀려나면 이쪽에서 도망친다

        // 기절한 채 묶였으면 오버레이를 걷는다 (#292 — 수갑 체포 성공 분기에 있던 처리를 밧줄로 옮긴 것).
        // 남겨두면 Update의 스턴 게이트가 끌기 Tick을 막고, 만료 해제 경로(resumeReaction: true)를 타면
        // StartFlee가 걸려 묶자마자 도망친다. 그래서 강제 해제다.
        // StartEscort 뒤에 두는 이유: EnterStunned가 Escorted를 만나면 StopEscort로 연행을 끊으므로
        // 순서를 뒤집으면 방금 건 커스터디가 풀린다.
        target.ExitStun(resumeReaction: false);

        NotifyOwner($"밧줄로 묶어 끌기 시작: {target.name}");
    }

    // DraggingNpc와 동기화 플래그를 함께 갱신 — 서버(또는 오프라인)에서만 호출된다(SetEscorting과 동일 관례).
    private void SetDragging(NpcController npc)
    {
        DraggingNpc = npc;

        if (npc != null)
        {
            // 새 끌기의 추종 상태를 초기화한다 — 이전 끌기의 관성·위상이 남으면 첫 프레임에 튄다
            m_dragVelocity = Vector3.zero;
            m_dragFacing = npc.transform.rotation;
            m_dragTravel = 0f;
        }

        if (IsSpawned && IsServer)
            m_isDraggingSynced.Value = npc != null;
    }

    // TetheredNpc와 동기화 참조를 함께 갱신 — 서버(또는 오프라인)에서만 호출된다.
    // 값이 그대로면 쓰지 않는다 — 매 프레임 정리(TickRopeDrag)가 호출해도 대역폭을 먹지 않게.
    private void SetTethered(NpcController npc)
    {
        TetheredNpc = npc;

        if (!IsSpawned || !IsServer)
            return;

        // 스폰된 대상만 참조로 넘길 수 있다(NetworkObjectReference 제약) — 아니면 표시 없이 끌기만 진행된다
        bool syncable = npc != null && npc.NetworkObject != null && npc.NetworkObject.IsSpawned;
        ulong desired = syncable ? npc.NetworkObject.NetworkObjectId : 0;
        if (m_tetheredNpcSynced.Value.NetworkObjectId == desired)
            return;

        m_tetheredNpcSynced.Value = syncable ? new NetworkObjectReference(npc.NetworkObject) : default;
    }

    /// <summary>
    /// 밧줄 끌기·연결 매 프레임 처리 — 본체 Update가 서버(또는 오프라인)에서만 호출한다. (#269/#369)
    /// </summary>
    private void TickRopeDrag()
    {
        // 대상이 커스터디를 벗어나면 밧줄 연결도 끊는다 — 인계 판정(→Jailed)·방치 탈주·풀기(→Idle)·
        // 라운드 종료 파괴가 전부 여기로 수렴한다(참조가 Unity 가짜 null이 되는 파괴 경로 포함, #356).
        // 끌기 중 강제 전이(넉백·페널티)는 아래 끌기 가드가 먼저 잡는다.
        if (TetheredNpc == null
            || (TetheredNpc.CurrentState != NpcState.Escorted
                && TetheredNpc.CurrentState != NpcState.Captured))
        {
            SetTethered(null); // 값이 이미 비었으면 아무것도 쓰지 않는다
        }
        // 너무 멀어지면 줄이 끊겨 풀려나 달아난다 — 벽에 막혀 못 따라오거나(끌기 중) 놓아둔 채 걸어간 경우(#369).
        // 끌던 중이면 먼저 놓아 에이전트를 되살린다(StopRopeDrag) — 도주(Run)가 NavMesh를 쓰기 때문.
        // 방치 탈주(NpcCapturedState.Escape)와 같은 반응: 끌던 플레이어에게서 도주한다.
        else if (IsTooFarToTether(TetheredNpc))
        {
            NpcController broken = TetheredNpc;
            NotifyOwner($"밧줄 끊김 — 너무 멀어져 도주: {broken.name}");
            if (DraggingNpc != null)
                ReleaseDrag();
            broken.StartFlee(transform);
            SetTethered(null);
            return;
        }

        if (DraggingNpc == null)
            return;

        // 외부 요인으로 커스터디에서 벗어났으면(넉백·페널티 등 강제 상태 전이) 끌기를 정리한다.
        if (DraggingNpc.CurrentState != NpcState.Escorted)
        {
            ReleaseDrag();
            return;
        }

        ServerUpdateDrag();
    }

    // 끊김 판정 — 수평 거리만 본다(끌기 장력과 같은 기준, 계단·경사에서 y차로 오작동하지 않게).
    private bool IsTooFarToTether(NpcController npc)
    {
        Vector3 delta = npc.transform.position - transform.position;
        delta.y = 0f;
        return delta.sqrMagnitude > m_ropeBreakDistance * m_ropeBreakDistance;
    }

    /// <summary>
    /// 끌리는 NPC를 밧줄 장력으로 끌어당긴다 — 서버(또는 오프라인) 매 프레임. (#269)
    /// 뒤 고정점에 강체로 붙이지 않는다: (1) 밧줄 길이를 넘을 때만 당기고 (2) 늦게 따라오게 해서
    /// 코너를 돌면 몸이 바깥으로 끌려나오는 궤적이 생긴다.
    /// </summary>
    private void ServerUpdateDrag()
    {
        Vector3 anchor = transform.position;
        Vector3 npcPosition = DraggingNpc.transform.position;

        Vector3 toNpc = npcPosition - anchor;
        toNpc.y = 0f;
        float distance = toNpc.magnitude;

        // 밧줄이 늘어져 있으면(길이 안쪽) 당기지 않는다 — 제자리에서 돌기만 하면 NPC는 가만히 있다
        Vector3 target = npcPosition;
        if (distance > m_ropeLength)
            target = anchor + toNpc / distance * m_ropeLength;

        // 높이는 끄는 플레이어 기준으로 시드만 한다 — 실제 지면 스냅·벽 판정은 NPC 쪽
        // NpcController.ServerDragTo가 지형을 보고 확정한다 (#369)
        target.y = anchor.y;

        Vector3 next = Vector3.SmoothDamp(npcPosition, target, ref m_dragVelocity, m_dragSmoothTime);

        // 몸 방향은 플레이어 회전이 아니라 밧줄 방향 — 제자리에서 마우스만 돌려도 NPC가 같이 돌지 않는다
        Vector3 ropeDirection = anchor - next;
        ropeDirection.y = 0f;
        if (ropeDirection.sqrMagnitude > 0.0001f)
        {
            Quaternion facing = Quaternion.LookRotation(ropeDirection);
            m_dragFacing = Quaternion.Slerp(
                m_dragFacing, facing, 1f - Mathf.Exp(-m_dragTurnSharpness * Time.deltaTime));
        }

        // 끌린 거리에 비례해 좌우로 흔들린다 — 시간이 아니라 거리 기준이라 멈추면 흔들림도 멈춘다
        m_dragTravel += (next - npcPosition).magnitude;
        float sway = Mathf.Sin(m_dragTravel * m_dragSwayFrequency) * m_dragSwayAngle;

        DraggingNpc.ServerDragTo(next, m_dragFacing * Quaternion.Euler(0f, sway, 0f));
    }

    /// <summary>밧줄 끌기 놓기 — NPC를 그 자리에 풀어 체포(Captured) 상태로 세운다(에이전트 복구). 서버(또는 오프라인) 실행. (#269/#369)
    /// <b>밧줄은 풀리지 않는다</b> — 줄은 여전히 이 플레이어와 이어져 있고(TetheredNpc), 다시 E로 끌 수 있다.
    /// 실제로 푸는 건 밧줄 좌클릭 채널링(ServerBeginUnrope)뿐이다.</summary>
    public void ReleaseDrag()
    {
        if (IsSpawned && !IsServer)
            return;
        if (DraggingNpc == null)
            return;

        NotifyOwner($"밧줄 끌기 놓기: {DraggingNpc.name} — 묶인 채 그 자리에 정지 (줄은 그대로)");
        DraggingNpc.StopRopeDrag(transform); // 놓은 자리가 NavMesh 밖이면 이 플레이어가 선 자리로 대체 복귀

        // 아직 커스터디면 그 자리에서 Captured로 멈춘다(방치 타이머·재확보로 이어짐). 이미 다른 상태로
        // 넘어갔으면(판정 후 수감·넉백·페널티) 그 행선지를 덮어쓰지 않는다. (#230)
        if (DraggingNpc.CurrentState == NpcState.Escorted)
            DraggingNpc.StopEscort();

        SetDragging(null);
    }
}
