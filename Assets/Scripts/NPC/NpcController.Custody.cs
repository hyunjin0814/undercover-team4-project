using Unity.Netcode;
using UnityEngine;

public partial class NpcController
{
    // ---- 유치장 (#228) ----

    // 유치장 내부(Jail) NavMesh 영역의 마스크 — 이름으로 한 번만 해석해 캐시한다.
    // 0이면 이 프로젝트에 Jail 영역이 없다는 뜻(단독 테스트 씬 등)이라 아래 두 API가 무동작이 된다.
    private static int s_jailAreaMask = -1;

    /// <summary>
    /// 유치장 내부(Jail 영역) 통행 허용/차단. (#415)
    /// 배회 시민은 프리팹 areaMask에서 Jail이 빠져 있어 감옥 안으로 걸어 들어갈 수 없고,
    /// <b>수감 이송에 들어가는 순간에만</b> 이 메서드로 통행을 얻는다(NpcJailedState.Enter).
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

        if (exitPoint != null && !TryWarpNear(exitPoint.position))
        {
            Debug.LogWarning($"NpcController: 유치장 출구로 워프 실패 — Jail 통행을 유지한다: {name}", this);
            return;
        }

        SetJailAccess(false);
    }

    private static int JailAreaMask
    {
        get
        {
            if (s_jailAreaMask < 0)
            {
                int area = UnityEngine.AI.NavMesh.GetAreaFromName("Jail");
                s_jailAreaMask = area >= 0 ? 1 << area : 0;
            }
            return s_jailAreaMask;
        }
    }

    /// <summary>
    /// 수감 — 인계존 판정에서 진범·경범죄로 확정된 NPC를 유치장으로 보낸다. (CustodyRouter 경유, GDD 7-2)
    /// cell(수용 지점)까지 스스로 걸어가 그 자리에 수용된다. cell이 null이면 그 자리에서 수용된 것으로 처리한다.
    /// </summary>
    public void SendToJail(Transform cell)
    {
        // FSM 전이는 서버 권위 — StartEscort와 동일하게 클라이언트 호출은 무시한다
        if (IsSpawned && !IsServer)
            return;

        EscortTarget = null; // 판정 시점에 연행은 이미 풀렸지만, 참조가 남아 있으면 여기서 끊는다
        JailCell = cell;
        m_stateMachine.ChangeState(NpcState.Jailed);
    }

    // 수갑 소모·반환(#229)은 밧줄이 소모형이 아니게 되면서 통째로 제거됐다 — 밧줄 검거엔 회수할 자원이 없다. (#369)
}
