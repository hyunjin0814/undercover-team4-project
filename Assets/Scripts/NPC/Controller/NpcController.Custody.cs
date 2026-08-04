using Unity.Netcode;
using UnityEngine;

public partial class NpcController
{
    // ---- 유치장 (#228) ----

    /// <summary>
    /// 유치장 내부(Jail 영역) 통행 허용/차단. (#415/#492)
    /// 배회 시민은 프리팹 areaMask에서 Jail이 빠져 있어 감옥 안으로 걸어 들어갈 수 없고,
    /// <b>경찰이 신병을 확보한 동안에만</b> 이 메서드로 통행을 얻는다 — 내주고 회수하는 주체는
    /// <see cref="JailIntake"/>다(수감 진입 시 NpcJailedState.Enter도 한 번 더 확인차 부른다).
    /// 차단은 문이 아니라 NavMesh 영역이 한다 — 유치장 내부 폴리곤이 통째로 Jail이라, 통행이 없으면
    /// 문이 열려 있어도 문턱을 넘는 경로 자체가 잡히지 않는다.
    /// 이동은 서버 권위이므로 서버(또는 오프라인)에서만 의미가 있다.
    /// </summary>
    public void SetJailAccess(bool allowed)
    {
        if (m_agent == null)
            return;

        int mask = JailAreaMask;
        if (mask == 0)
            return; // Jail 영역이 없는 프로젝트 — 종전대로 전 영역 통행

        if (allowed)
            m_agent.areaMask |= mask;
        else
            m_agent.areaMask &= ~mask;
    }

    /// <summary>
    /// 유치장 밖으로 내보낸다 — 탈옥 방출(#231)이 부른다. 문 밖 출구 지점으로 워프한 뒤 Jail 통행을 회수하므로,
    /// 풀려난 대상이 창살 안에 갇히지도(경로가 없어 제자리 고착) 이후 감옥 안을 배회하지도 않는다. (#415)
    /// 워프가 실패하면(출구가 NavMesh 밖) 통행을 회수하지 않는다 — 갇히는 것보다 낫다.
    /// 서버(또는 오프라인) 전용.
    /// </summary>
    public void ServerExitJail(Transform exitPoint)
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_agent == null)
            return;

        // 앉은 자세를 워프보다 먼저 푼다 — 뒤에 두면 창살 밖으로 옮겨진 몸이 한두 프레임 앉은 채로 보인다.
        // 수감 상태의 Exit도 같은 호출을 하지만(다른 이탈 경로 대비) 여기서 순서를 잡아 주는 것이 표현상 맞다. (#462)
        SetSeated(false);

        if (exitPoint != null && !TryWarpNear(exitPoint.position))
        {
            Debug.LogWarning(
                $"NpcController: 유치장 출구로 워프 실패 — Jail 통행을 유지한다: {name}",
                this
            );
            return;
        }

        SetJailAccess(false);
    }

    /// <summary>유치장 내부(Jail) NavMesh 영역 마스크 — 없는 프로젝트면 0.
    /// 실제 해석은 <see cref="JailArea"/>가 한다 (#492에서 사본 통합).</summary>
    public static int JailAreaMask => JailArea.Mask;

    // ---- 반출 표식 (#517) ----

    private bool m_jailExtracted;

    // 동기화 플래그 — 서버만 기록한다(착석 m_seatedSynced와 같은 관례).
    // 클라도 읽어야 한다: E 조준 피드백(NpcSubdueInteractable.CanInteract)이 이 값으로 분기를 고른다.
    private readonly NetworkVariable<bool> m_jailExtractedSynced = new(false);

    /// <summary>
    /// 유치장에서 반출돼(#492) 밧줄 없이 데려가는 중인 수감자인가 — 거리 이탈로 멈춰 서도(Captured) 유지된다.
    /// 전 피어에서 유효. (#517)
    ///
    /// <b>왜 표식이 필요한가</b> — 멈추면 상태가 Captured가 되는데, 그것만으로는 "반출된 수감자"와
    /// "방금 제압한 신병"을 구분할 수 없다. 구분이 없으면 E가 밧줄 끌기로 새서 반출 흐름으로 되돌릴
    /// 입력이 사라진다. <see cref="IsDelivered"/>로는 못 가른다 — 게이트 판정을 통과해 끌려가는 중인
    /// 대상도 그 값이 참이다.
    /// </summary>
    public bool IsJailExtracted =>
        IsSpawned && !IsServer ? m_jailExtractedSynced.Value : m_jailExtracted;

    /// <summary>반출 표식 지정 — 켜는 곳은 <see cref="JailIntake"/>의 반출 하나뿐이다. 서버(또는 오프라인). (#517)
    /// 끄는 곳은 셋이다: 커스터디 이탈(재착석·도주·석방), 밧줄에 묶임, 그리고 여기 직접 호출.</summary>
    public void SetJailExtracted(bool value)
    {
        if (IsSpawned && !IsServer)
            return;

        m_jailExtracted = value;
        if (IsSpawned && IsServer)
            m_jailExtractedSynced.Value = value;
    }

    // ---- 착석 (#462) ----

    private bool m_seated;

    // 동기화 플래그 — 서버만 기록한다(끌기 m_ropedSynced와 같은 관례). NpcAnimationDriver가 이 값으로
    // 앉기 모션을 고른다. 속도로는 못 가른다: 걸어와 멈춘 것과 앉은 것이 둘 다 속도 0이다.
    private readonly NetworkVariable<bool> m_seatedSynced = new(false);

    /// <summary>유치장 좌석에 앉아 있는가 — 서버·오프라인은 실제 값, 원격 피어는 동기화 플래그. (#462)</summary>
    public bool IsSeated => IsSpawned && !IsServer ? m_seatedSynced.Value : m_seated;

    /// <summary>착석 지정 — 앉을 때 true, 수감이 풀릴 때 false. 서버(또는 오프라인) 전용.
    /// 호출부는 <see cref="NpcJailedState"/>(도착 판정을 그쪽이 쥔다). (#462)</summary>
    public void SetSeated(bool value)
    {
        if (IsSpawned && !IsServer)
            return;

        m_seated = value;
        if (IsSpawned && IsServer)
            m_seatedSynced.Value = value;
    }

    /// <summary>
    /// 수감 — 판정에서 진범·경범죄로 확정된 NPC를 좌석으로 보낸다. (<see cref="JailIntake"/> 경유, GDD 7-2)
    /// 유치장 안에서 플레이어가 놓는 순간 불린다 (#492 — CustodyRouter가 자동 이송하던 경로는 폐기).
    /// seat(좌석)까지 스스로 걸어가 그 자리에 앉는다. seat이 null이면 그 자리에서 수용된 것으로 처리한다.
    /// 좌석 배정은 보내는 쪽(JailZone.ReserveSeat)이 한다 — 자리 계산이 아니라 손으로 배치한 목록이다. (#462)
    /// </summary>
    public void SendToJail(Transform seat)
    {
        // FSM 전이는 서버 권위 — StartEscort와 동일하게 클라이언트 호출은 무시한다
        if (IsSpawned && !IsServer)
            return;

        EscortTarget = null; // 판정 시점에 연행은 이미 풀렸지만, 참조가 남아 있으면 여기서 끊는다
        JailSeat = seat;
        m_stateMachine.ChangeState(NpcState.Jailed);
    }

    // 수갑 소모·반환(#229)은 밧줄이 소모형이 아니게 되면서 통째로 제거됐다 — 밧줄 검거엔 회수할 자원이 없다. (#369)
}
