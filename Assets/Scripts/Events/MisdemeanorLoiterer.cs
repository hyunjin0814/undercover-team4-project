using UnityEngine;

/// <summary>
/// 잔류 경범죄자 관리 (#310 후속) — 돌발 이벤트가 손을 뗀 NPC(이탈·소란 종료·판정 인계된 난동꾼·침입자)의
/// 최종 안전망이다. NPC는 배회 시민으로 섞여 들거나 유치장에 수감돼 있지만 <see cref="MisdemeanorOffender"/>
/// 마커가 남아 있어 언제든 제압·연행해 인계하면 경범죄로 판정된다(수익은 첫 판정에만 — ArrestJudge가 비운다).
/// "아까 놓친 그 놈"을 다시 잡는 재미(GDD 6-5의 재발견 철학)가 잔류의 존재 이유다.
///
/// 맡는 일은 둘이다:
///  1. <b>라운드 종료 정리</b> — 이벤트에서 떨어져 나온 스폰물이 다음 라운드까지 남지 않게 하는 누수 방지.
///  2. <b>탈옥 방출 후 소란 재개</b> — 방출된 난동꾼이 조용한 시민으로 남는 대신 원래 이벤트 행동으로
///     복귀한다(팀 확정 2026-07-23): 도주가 가라앉으면 난동자는 그 자리 저항 난동을, 난동꾼은 근처
///     플레이어를 보고 도주 소란을 재개한다. 소란 시간(마커에 기록된 원래 값)이 다하면 다시 진정·잔류하고,
///     제압·재수감되면 소란은 그대로 취소된다 — 이벤트 본편과 같은 수명 규칙이다.
///
/// 이벤트가 추적을 끊는 시점에 서버(또는 오프라인)에서만 AddComponent로 붙는다 —
/// 마커(MisdemeanorOffender)와 같은 이유로 복제가 필요 없는 plain MonoBehaviour다.
/// 이벤트는 이 시점부터 비활성(IsActive=false)이 되어 같은 종류의 새 이벤트가 다시 추첨될 수 있다.
/// </summary>
public class MisdemeanorLoiterer : MonoBehaviour
{
    // 소란 재개 시 위협으로 삼을 플레이어 탐색 반경(m) — 이 안에 아무도 없으면 배회하며 기다렸다가,
    // 누가 다가오면 그때 소란을 시작한다. 대상 없는 저항/도주는 곧 흐지부지 배회로 돌아오므로
    // (NpcResistState·NpcFleeState의 이탈 판정) 두 행동 모두 이 게이트를 공유한다.
    private const float k_riotThreatRadius = 14f;

    private RoundManager Round => App.Game.Round;

    private NpcController m_controller;

    private bool m_riotPending;   // 방출됨 — 도주가 가라앉으면 소란을 재개한다
    private bool m_rioting;       // 소란 재개 중 — 시간이 다하면 진정한다
    private float m_riotEndTime;  // 재개한 소란의 종료 시각(Time.time)

    /// <summary>이벤트가 손을 떼는 NPC에 관리자를 붙인다 — 서버(또는 오프라인) 전용. 이미 붙어 있으면 무동작.</summary>
    public static void Attach(NpcController npc, string displayName)
    {
        if (npc == null)
            return;

        if (npc.GetComponent<MisdemeanorLoiterer>() == null)
            npc.gameObject.AddComponent<MisdemeanorLoiterer>();

        Debug.Log($"[돌발이벤트] {displayName} — 이벤트 추적 종료, 도심 잔류");
    }

    /// <summary>
    /// 탈옥 방출 통보 — 도주가 가라앉으면 마커에 기록된 원래 소란 행동을 재개한다. (#310 후속)
    /// JailbreakEvent.ReleaseInmate가 호출한다. 서버(또는 오프라인) 전용.
    /// </summary>
    public static void BeginRiot(NpcController npc)
    {
        if (npc == null)
            return;

        MisdemeanorOffender offender = npc.GetComponent<MisdemeanorOffender>();
        if (offender == null || !offender.HasRiotBehavior)
            return; // 재개할 소란이 없는 개체(침입자 등) — 기존대로 배회 잔류

        MisdemeanorLoiterer loiterer = npc.GetComponent<MisdemeanorLoiterer>();
        if (loiterer == null)
            loiterer = npc.gameObject.AddComponent<MisdemeanorLoiterer>();

        loiterer.m_riotPending = true;
        loiterer.m_rioting = false;
    }

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();
    }

    private void Update()
    {
        // 라운드가 끝나면 정리한다 — 일괄 정리라 소멸 연출은 끈다 (이벤트 ServerReset과 같은 이유)
        if (Round != null && Round.Phase != RoundPhase.InProgress)
        {
            // 연행 중이던 플레이어가 파괴된 참조를 쥐지 않게 먼저 놓게 한다 (이벤트 Despawn들과 동일)
            // 줄다리기로 여러 명이 걸려 있을 수 있다 — 전원에게서 이 대상의 줄만 뺀다 (#390).
            foreach (PlayerEscorter escorter in PlayerEscorter.FindEscortersOf(m_controller))
                escorter.ReleaseDrag(m_controller);

            SuddenEventUtil.DespawnOrDestroy(gameObject, playVfx: false);
            return;
        }

        if (m_controller == null)
            return;

        NpcState state = m_controller.CurrentState;

        // 제압·연행·재수감되면 소란 재개는 취소된다 — 이후는 기존 재검거·수감 흐름이 이어받는다
        if (state is NpcState.Captured or NpcState.Escorted or NpcState.Jailed)
        {
            m_riotPending = false;
            m_rioting = false;
            return;
        }

        // 소란 시간이 다하면 진정하고 배회 잔류로 되돌아간다 (이벤트 본편과 동일한 수명 규칙)
        if (m_rioting && Time.time >= m_riotEndTime)
        {
            m_rioting = false;
            if (state is NpcState.Attack or NpcState.Run)
            {
                Debug.Log($"[돌발이벤트] 방출 소란 종료 — 진정: {name}");
                m_controller.StartFlee(null); // 위협 없는 도주 — 곧 배회(Idle)로 가라앉는다
            }
            return;
        }

        // 소란 점화·재점화 — 방출 직후(pending)든 소란 창 안에서 흐지부지 배회로 돌아온 경우든,
        // 배회 중 근처에 플레이어가 나타나면 소란을 (다시) 건다. 대상 없는 소란은 성립하지 않으므로
        // 반경 안에 아무도 없으면 배회하며 기다린다.
        if ((m_riotPending || m_rioting) && state is NpcState.Idle or NpcState.Walk)
            TryIgniteRiot();
    }

    // 마커에 기록된 행동으로 소란을 (재)점화한다 — 근처 플레이어가 있을 때만.
    private void TryIgniteRiot()
    {
        MisdemeanorOffender offender = GetComponent<MisdemeanorOffender>();
        if (offender == null || !offender.HasRiotBehavior)
        {
            m_riotPending = false;
            m_rioting = false;
            return;
        }

        PlayerData threat = SuddenEventUtil.FindNearestFieldPlayer(transform.position, k_riotThreatRadius);
        if (threat == null)
            return; // 대기 — 다음 틱에 다시 본다

        switch (offender.RiotBehavior)
        {
            case SpawnedNpcEvent.Behavior.Resist:
                m_controller.StartResist(threat.transform); // 그 자리 저항 난동 — 다가온 플레이어가 표적
                break;

            case SpawnedNpcEvent.Behavior.Flee:
                m_controller.StartFlee(threat.transform); // 도주 소란 — 다가온 플레이어에게서 달아난다
                break;
        }

        // 소란 창은 첫 점화 때만 연다 — 창 안의 재점화는 남은 시간을 그대로 쓴다
        if (m_riotPending)
        {
            m_riotPending = false;
            m_rioting = true;
            m_riotEndTime = Time.time + offender.RiotSeconds;
            Debug.Log($"[돌발이벤트] 탈옥 방출 — 소란 재개({offender.RiotBehavior}, {offender.RiotSeconds:F0}초): {name}");
        }
    }
}
