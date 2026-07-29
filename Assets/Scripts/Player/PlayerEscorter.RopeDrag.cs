using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// PlayerEscorter의 밧줄 묶기·끌기 — 본체와 partial로 분리. (#269/#369/#390)
/// 밧줄 <b>1개당 NPC 1명</b>이라 연결은 목록이고, 동시 인원의 상한은 소지한 밧줄 개수다.
/// 장력 계산은 끌리는 <see cref="NpcController"/>가 갖는다 — 여기 남은 것은 "누구를 묶고 있나"의
/// 참조·자원 관리와 서버 권위 진입 판정이다.
/// </summary>
public partial class PlayerEscorter
{
    [Header("밧줄 끌기")]
    // 장력 튜닝 값(길이·스무딩·흔들림·간격)은 NpcRopeDragConfig에 있다 — 장력 계산과 같은 자리.
    [Tooltip("이 거리(m)를 넘게 멀어지면 밧줄이 끊겨 NPC가 풀려난다 — 벽에 막혀 못 따라오거나 놓아둔 채 걸어가면 발생. 밧줄 길이보다 넉넉해야 한다")]
    [SerializeField] private float m_ropeBreakDistance = 10f;

    // 내 밧줄에 묶여 있는 NPC들 — 서버(또는 오프라인) 진실. 끌기를 멈춰도(E) 남는다.
    private readonly List<NpcController> m_tethered = new List<NpcController>();

    // 위 목록의 클라 사본(서버만 쓴다). 표시(RopeDragView)가 선의 양 끝점을 알아야 하고,
    // 오너 조기검증(Rope.CanTarget·E 놓기 대상)도 "내가 이걸 묶었나"를 물어야 한다.
    // 항목마다 '끌고 있는가'를 싣는 이유: 줄다리기로 한 NPC에 여러 명이 걸리면
    // NpcController.IsRoped("누구든 끌고 있다")로는 내가 놓았는지를 알 수 없다.
    // ⚠ late-join 클라는 OnListChanged를 못 받는다 — 읽는 쪽이 현재 목록을 직접 훑을 것 (WantedListManager와 같은 주의).
    private readonly NetworkList<RopeTether> m_tetheredSynced = new NetworkList<RopeTether>();

    /// <summary>지금 밧줄에 묶여 있는 인원 수. 전 피어에서 유효.</summary>
    public int TetheredCount => IsSpawned && !IsServer ? m_tetheredSynced.Count : m_tethered.Count;

    // 동시에 묶을 수 있는 상한 — 로드아웃이 없으면(테스트 구성) 무제한.
    private int RopeCapacity => Loadout != null ? Loadout.RopeCount : int.MaxValue;

    /// <summary>소지한 밧줄을 전부 쓰고 있는가 — 새 대상을 묶는 것을 막는 자원 게이트.</summary>
    public bool IsAtRopeCapacity => TetheredCount >= RopeCapacity;

    /// <summary>묶인 대상을 순번으로 얻는다 — 전 피어에서 유효한 표현·검증용. 없거나 못 찾으면 null.</summary>
    public NpcController GetTetheredNpc(int index)
    {
        if (!IsSpawned || IsServer)
            return index >= 0 && index < m_tethered.Count ? m_tethered[index] : null;

        if (index < 0 || index >= m_tetheredSynced.Count)
            return null;

        // 세션이 내려가는 중에는 매니저가 이미 사라져 있다. IsSpawned만으로는 이 순간을 거를 수 없어
        // (디스폰 통지보다 매니저 소멸이 앞설 수 있다) 라운드 종료 후 씬이 바뀌는 동안
        // 표시(RopeDragView.LateUpdate)가 매 프레임 NullReferenceException을 뱉는다.
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening)
            return null;

        return manager.SpawnManager.SpawnedObjects.TryGetValue(
            m_tetheredSynced[index].NpcId, out NetworkObject npcObject)
            && npcObject.TryGetComponent(out NpcController npc)
            ? npc
            : null;
    }

    /// <summary>이 NPC가 <b>내</b> 밧줄에 묶여 있는가 — 전 피어에서 유효. 좌클릭 분기·E 놓기 대상 판정이 쓴다.</summary>
    public bool IsTetheredTo(NpcController npc) => IndexOfTether(npc) >= 0;

    /// <summary>이 NPC를 <b>내가 지금 끌고</b> 있는가 — 전 피어에서 유효.
    /// 묶여만 있는(E로 놓아둔) 대상은 <b>남이 대신 끌고 있어도</b> false다 — 그래야 줄다리기 중
    /// 내 E가 계속 '놓기'로 소비되지 않는다.</summary>
    public bool IsDraggingNpc(NpcController npc)
    {
        if (!IsTetheredTo(npc))
            return false;

        // 서버는 NPC의 앵커 목록이 단일 진실. 클라는 그게 실려 온 동기화 항목을 읽는다.
        if (!IsSpawned || IsServer)
            return npc.IsDraggedBy(transform);

        int index = IndexOfSynced(npc);
        return index >= 0 && m_tetheredSynced[index].Dragging;
    }

    // 이 NPC가 내 목록의 몇 번째인가 — 없으면 -1. 전 피어에서 유효하되 인덱스는 서버·클라가 다를 수 있다
    // (미스폰 NPC는 서버 목록에만 들어가 길이가 어긋난다).
    private int IndexOfTether(NpcController npc)
    {
        if (npc == null)
            return -1;

        return !IsSpawned || IsServer ? m_tethered.IndexOf(npc) : IndexOfSynced(npc);
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

        // 남이 끌고 있는 대상에는 밧줄을 덧건다 — 줄다리기 합류. 기존 끌기는 끊지 않는다(탈취 차단).
        // 이미 커스터디라 반응 판정·기절 지름길이 필요 없어 채널링만 태우고 바로 붙인다.
        if (NpcStateRules.CanJoinDrag(target.CurrentState))
        {
            ServerRopeChannelAsync(target, joining: true).Forget();
            return;
        }

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

        ServerRopeChannelAsync(target, joining: false).Forget();
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
        if (!IsTetheredTo(target))
        {
            // 남이 묶어 둔 대상은 가져올 수 없다 — 탈취 차단.
            // 합류는 상대가 실제로 끌고 있을 때(Escorted) 밧줄 좌클릭으로만 열린다.
            if (FindEscorterOf(target) != null)
                return;
            if (!CanBeginRopeDrag(target))
                return;
        }
        if (!NpcStateRules.CanRelease(target.CurrentState))
            return; // 체포되어 멈춘 대상만

        ServerApplyRopeDrag(target);
    }

    // 새 대상을 묶을 수 있는가 — 자원(밧줄 개수)·중복·사거리. 상태 게이트는 호출부가 각자 건다.
    private bool CanBeginRopeDrag(NpcController target)
    {
        if (m_channel.IsActive)
            return false;
        if (IsTetheredTo(target))
            return false; // 이미 내 줄에 묶여 있다 — 좌클릭은 풀기/재개로 갈린다
        if (IsAtRopeCapacity)
            return false; // 소지한 밧줄 수만큼만 — 예전의 "한 번에 1명"을 대체한 자원 게이트
        return IsInRange(target);
    }

    private async UniTaskVoid ServerRopeChannelAsync(NpcController target, bool joining)
    {
        NotifyOwner(
            joining
                ? $"줄다리기 합류 채널링 시작: {target.name} ({m_channelSeconds}초)"
                : $"밧줄 묶기 채널링 시작: {target.name} ({m_channelSeconds}초)");
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

        // 채널링 도중 상태·자원이 바뀌었을 수 있다 — 완료 시점에 재확인(다른 플레이어가 먼저 확보,
        // 그 사이 밧줄을 버려 용량이 준 경우, 합류하려던 대상을 그새 놓아버린 경우 등).
        if (target == null || IsAtRopeCapacity)
            return;
        bool stateOk = joining
            ? NpcStateRules.CanJoinDrag(target.CurrentState)
            : NpcStateRules.CanArrest(target.CurrentState);
        if (!stateOk)
            return;

        // 반응 판정은 여기서 굴리지 않는다 (#400) — 밧줄은 순수 검거 수단이 됐고, 판정은
        // NpcController.ServerReactTo가 단독으로 갖는다. 함부로 묶는 것을 막던 장치도 함께 사라졌다 —
        // 이제는 이미 반응 중인 대상이 CanArrest에서 걸리는 것이 그 역할을 대신한다.
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
        AddTether(target);

        // 커스터디 상태는 수갑 연행과 같은 Escorted를 재사용한다 — 인계존·이벤트 수명·가로채기 방지가
        // 이미 이 상태를 기준으로 판정하기 때문. 이동은 밧줄 장력이 하고 NpcEscortedState가 IsRoped를 보고
        // 추종을 건너뛴다. (#369)
        target.StartEscort(transform);
        target.StartRopeDrag(transform); // 끈 플레이어를 위협으로 기억 — 풀려나면 이쪽에서 도망친다

        // 기절한 채 묶였으면 오버레이를 걷는다 — 남겨두면 만료 해제 경로(resumeReaction: true)가
        // StartFlee를 걸어 묶자마자 도망친다. (#292)
        target.ExitStun(resumeReaction: false);

        NotifyOwner($"밧줄로 묶어 끌기 시작: {target.name} ({TetheredCount}/{RopeCapacity})");
    }

    // ---- 연결 목록 관리 (서버·오프라인 전용) ----

    // 묶기·합류·재개가 전부 여기로 온다. 직후 StartRopeDrag로 앵커가 붙으므로 항상 '끌고 있음'으로 시작한다.
    private void AddTether(NpcController npc)
    {
        if (npc == null)
            return;

        if (!m_tethered.Contains(npc))
            m_tethered.Add(npc);

        SetTetherDragging(npc, true);
    }

    // 동기화 항목의 끌기 표시를 갱신한다 — 값이 그대로면 쓰지 않는다(매 프레임 정리가 불러도 대역폭을 먹지 않게).
    // 서버 자신은 이 표시를 읽지 않는다(NpcController.IsDraggedBy가 진실) — 순전히 클라에 알리는 용도다.
    private void SetTetherDragging(NpcController npc, bool dragging)
    {
        if (!IsSpawned || !IsServer)
            return;

        int index = IndexOfSynced(npc);
        if (index < 0)
        {
            // 스폰된 대상만 동기화 목록에 실을 수 있다 — 아니면 표시 없이 끌기만 진행된다(오프라인 테스트 등)
            if (npc.NetworkObject != null && npc.NetworkObject.IsSpawned)
            {
                m_tetheredSynced.Add(
                    new RopeTether { NpcId = npc.NetworkObject.NetworkObjectId, Dragging = dragging });
            }
            return;
        }

        RopeTether entry = m_tetheredSynced[index];
        if (entry.Dragging == dragging)
            return;

        entry.Dragging = dragging;
        m_tetheredSynced[index] = entry;
    }

    /// <summary>이 대상과의 연결을 끊는다 — 밧줄 풀기(ServerBeginUnrope) 전용. 서버(또는 오프라인).</summary>
    private void RemoveTether(NpcController npc)
    {
        int index = m_tethered.IndexOf(npc);
        if (index >= 0)
            RemoveTetherAt(index);
    }

    // 인덱스로 지운다 — 매 프레임 정리가 역순 순회하며 부르기 때문. npc가 이미 파괴됐을 수 있다.
    private void RemoveTetherAt(int index)
    {
        NpcController npc = m_tethered[index];
        m_tethered.RemoveAt(index);

        if (!IsSpawned || !IsServer)
            return;

        // 살아 있으면 id로 정확히 지우고, 파괴돼 id를 못 읽으면 죽은 항목을 훑어 정리한다.
        int syncedIndex = IndexOfSynced(npc);
        if (syncedIndex >= 0)
        {
            m_tetheredSynced.RemoveAt(syncedIndex);
            return;
        }

        if (npc == null || npc.NetworkObject == null)
            PruneDeadSyncedEntries();
    }

    // 동기화 목록에서 이 NPC의 항목 위치 — 없거나 id를 못 읽으면 -1.
    private int IndexOfSynced(NpcController npc)
    {
        if (npc == null || npc.NetworkObject == null)
            return -1;

        ulong id = npc.NetworkObject.NetworkObjectId;
        for (int i = 0; i < m_tetheredSynced.Count; i++)
            if (m_tetheredSynced[i].NpcId == id)
                return i;
        return -1;
    }

    // 대상이 파괴·디스폰돼 더는 풀리지 않는 항목을 걷어낸다 — 남겨두면 원격 피어가 없는 줄을 계속 찾는다.
    private void PruneDeadSyncedEntries()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening)
            return;

        for (int i = m_tetheredSynced.Count - 1; i >= 0; i--)
            if (!manager.SpawnManager.SpawnedObjects.ContainsKey(m_tetheredSynced[i].NpcId))
                m_tetheredSynced.RemoveAt(i);
    }

    /// <summary>밧줄 연결 매 프레임 정리 — 본체 Update가 서버(또는 오프라인)에서만 호출한다.
    /// 장력은 <see cref="NpcController"/>가 계산한다 — 여기는 커스터디 이탈·거리 끊김에 따른 참조 관리만.</summary>
    private void TickTetherCleanup()
    {
        // 지우면서 도니 역순 — 각 연결은 서로 독립이라 하나가 끊겨도 나머지는 유지된다.
        for (int i = m_tethered.Count - 1; i >= 0; i--)
        {
            NpcController npc = m_tethered[i];

            // 대상이 커스터디를 벗어나면 밧줄 연결도 끊는다 — 인계 판정(→Jailed)·방치 탈주·풀기(→Idle)·
            // 라운드 종료 파괴가 전부 여기로 수렴한다(참조가 Unity 가짜 null이 되는 파괴 경로 포함, #356).
            if (npc == null
                || (npc.CurrentState != NpcState.Escorted && npc.CurrentState != NpcState.Captured))
            {
                RemoveTetherAt(i);
                continue;
            }

            // 너무 멀어지면 줄이 끊겨 풀려나 달아난다 — 벽에 막혀 못 따라오거나 놓아둔 채 걸어간 경우 (#369).
            // ReleaseDrag가 먼저인 이유: 도주(Run)가 NavMesh를 쓰는데 끌기 중엔 에이전트가 꺼져 있다.
            if (IsTooFarToTether(npc))
            {
                ReleaseDrag(npc);
                RemoveTetherAt(i);

                // 다른 참가자가 아직 잡고 있으면 도주시키지 않는다 — 내 줄만 끊긴 것이다.
                // 줄다리기에서 밀린 쪽이 빠지는 정상 결말이라, 여기서 도주시키면 이긴 쪽 손에서 사라진다.
                if (FindEscorterOf(npc) != null)
                {
                    NotifyOwner($"밧줄 끊김 — 내 줄만 끊겼다 (다른 참가자가 계속 확보 중): {npc.name}");
                    continue;
                }

                NotifyOwner($"밧줄 끊김 — 너무 멀어져 도주: {npc.name}");
                npc.StartFlee(transform);
                continue;
            }

            // 외부 요인으로 커스터디에서 벗어났으면(넉백·페널티 등 강제 상태 전이) 끌기만 정리한다 — 줄은 유지.
            if (npc.CurrentState != NpcState.Escorted)
                ReleaseDrag(npc);
        }

        AssignDragSlots();
    }

    // 끌고 있는 대상들에 자리 번호를 매긴다 — 번호를 받은 NPC가 밧줄 방향에 수직으로 벌려 부채꼴이 된다.
    // 없으면 전원이 내 발밑 한 점에 앵커돼 같은 지점으로 수렴하고, 벽 스윕이 사람은 장애물로 안 쳐서
    // (#313/#339) 서로 통과해 한 덩어리가 된다.
    // 매 프레임 다시 매기는 이유: 놓기·인계·끊김으로 구성이 수시로 바뀐다. n이 소지 밧줄 수(최대 3)라 비용이 없다.
    private void AssignDragSlots()
    {
        int draggingCount = 0;
        for (int i = 0; i < m_tethered.Count; i++)
            if (m_tethered[i].IsDraggedBy(transform))
                draggingCount++;

        int slot = 0;
        for (int i = 0; i < m_tethered.Count; i++)
        {
            if (!m_tethered[i].IsDraggedBy(transform))
                continue;

            m_tethered[i].SetDragSlot(slot, draggingCount);
            slot++;
        }
    }

    // 끊김 판정 — 수평 거리만 본다(끌기 장력과 같은 기준, 계단·경사에서 y차로 오작동하지 않게).
    private bool IsTooFarToTether(NpcController npc)
    {
        Vector3 delta = npc.transform.position - transform.position;
        delta.y = 0f;
        return delta.sqrMagnitude > m_ropeBreakDistance * m_ropeBreakDistance;
    }

    /// <summary>밧줄 끌기 놓기 — 지정한 NPC 하나만 그 자리에 풀어 체포(Captured) 상태로 세운다(에이전트 복구).
    /// 서버(또는 오프라인) 실행. 나머지 대상은 계속 끌린다.
    /// <b>밧줄은 풀리지 않는다</b> — 줄은 여전히 이 플레이어와 이어져 있고 다시 E로 끌 수 있다.
    /// 실제로 푸는 건 밧줄 좌클릭 채널링(ServerBeginUnrope)뿐이다.</summary>
    public void ReleaseDrag(NpcController npc)
    {
        if (IsSpawned && !IsServer)
            return;
        // 파괴된 대상은 건드리지 않는다 — 아래에서 NPC 쪽 상태를 직접 묻는다 (라운드 종료 정리 경로)
        if (npc == null)
            return;
        if (!m_tethered.Contains(npc) || !npc.IsDraggedBy(transform))
            return; // 안 묶었거나 이미 놓은 대상

        // 내 앵커만 뺀다 — 남이 함께 끌고 있으면(줄다리기) 대상은 계속 끌린다.
        bool stillDragged = npc.StopRopeDrag(transform); // 놓은 자리가 NavMesh 밖이면 이 플레이어가 선 자리로 대체 복귀
        SetTetherDragging(npc, false);

        NotifyOwner(
            stillDragged
                ? $"밧줄 끌기 놓기: {npc.name} — 다른 참가자가 계속 끌고 있다 (줄은 그대로)"
                : $"밧줄 끌기 놓기: {npc.name} — 묶인 채 그 자리에 정지 (줄은 그대로)");

        // 아직 아무도 안 끌고 커스터디면 그 자리에서 Captured로 멈춘다(방치 타이머·재확보로 이어짐).
        // 이미 다른 상태로 넘어갔으면(판정 후 수감·넉백·페널티) 그 행선지를 덮어쓰지 않는다. (#230)
        if (!stillDragged && npc.CurrentState == NpcState.Escorted)
            npc.StopEscort();
    }

    /// <summary>끌고 있는 대상 전부를 놓는다 — 디스폰 등 플레이어가 사라지는 경로 전용. 줄은 유지된다.</summary>
    public void ReleaseAllDrags()
    {
        if (IsSpawned && !IsServer)
            return;

        for (int i = m_tethered.Count - 1; i >= 0; i--)
            ReleaseDrag(m_tethered[i]);
    }
}
