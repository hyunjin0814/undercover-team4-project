using UnityEngine;

/// <summary>
/// 이벤트가 손을 뗀 경범죄 NPC의 잔류 관리 — 라운드 종료 정리와 탈옥 방출 후 소란 재개를 맡는다.
/// 서버(또는 오프라인)에서 런타임에 부착되는 plain MonoBehaviour다.
/// </summary>
public class MisdemeanorLoiterer : MonoBehaviour
{
    private const float k_riotThreatRadius = 14f;

    private RoundManager Round => App.Game.Round;

    private NpcController m_controller;

    private bool m_riotPending;
    private bool m_rioting;
    private float m_riotEndTime;

    /// <summary>이벤트가 손을 떼는 NPC에 관리자를 붙인다 — 서버(또는 오프라인) 전용. 이미 붙어 있으면 무동작.</summary>
    public static void Attach(NpcController npc, string displayName)
    {
        if (npc == null)
            return;

        if (npc.GetComponent<MisdemeanorLoiterer>() == null)
            npc.gameObject.AddComponent<MisdemeanorLoiterer>();

        Debug.Log($"[돌발이벤트] {displayName} — 이벤트 추적 종료, 도심 잔류");
    }

    /// <summary>탈옥 방출 후 도주가 가라앉으면 원래 소란 행동을 재개하도록 한다. 서버(또는 오프라인) 전용.</summary>
    public static void BeginRiot(NpcController npc)
    {
        if (npc == null)
            return;

        MisdemeanorOffender offender = npc.GetComponent<MisdemeanorOffender>();
        if (offender == null || !offender.HasRiotBehavior)
            return;

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
        if (Round != null && Round.Phase != RoundPhase.InProgress)
        {
            foreach (PlayerEscorter escorter in PlayerEscorter.FindEscortersOf(m_controller))
                escorter.ReleaseDrag(m_controller);

            if (TryGetComponent(out Pickpocket thief))
                thief.ServerLoseStolenItem();

            SuddenEventUtil.DespawnOrDestroy(gameObject, playVfx: false);
            return;
        }

        if (m_controller == null)
            return;

        NpcState state = m_controller.CurrentState;

        if (state is NpcState.Captured or NpcState.Escorted or NpcState.Jailed)
        {
            if (TryGetComponent(out Pickpocket thief))
                thief.ServerDropStolenItem();

            m_riotPending = false;
            m_rioting = false;
            return;
        }

        if (m_controller.Stun.IsStunned)
            return;

        if (m_rioting && Time.time >= m_riotEndTime)
        {
            m_rioting = false;
            if (state is NpcState.Attack or NpcState.Run or NpcState.Sprinting)
            {
                Debug.Log($"[돌발이벤트] 방출 소란 종료 — 진정: {name}");
                m_controller.Reaction.StartFlee(null);
            }
            return;
        }

        if ((m_riotPending || m_rioting) && state is NpcState.Idle or NpcState.Walk)
            TryIgniteRiot();
    }

    private void TryIgniteRiot()
    {
        MisdemeanorOffender offender = GetComponent<MisdemeanorOffender>();
        if (offender == null || !offender.HasRiotBehavior)
        {
            m_riotPending = false;
            m_rioting = false;
            return;
        }

        if (offender.RiotBehavior == ERiotBehavior.Sprint)
        {
            m_controller.Reaction.StartSprint();
        }
        else
        {
            PlayerHealth threat = SuddenEventUtil.FindNearestFieldPlayer(transform.position, k_riotThreatRadius);
            if (threat == null)
                return;

            switch (offender.RiotBehavior)
            {
                case ERiotBehavior.Resist:
                    m_controller.Reaction.StartResist(threat.transform);
                    break;

                case ERiotBehavior.Flee:
                    m_controller.Reaction.StartFlee(threat.transform);
                    break;
            }
        }

        if (m_riotPending)
        {
            m_riotPending = false;
            m_rioting = true;
            m_riotEndTime = offender.RiotSeconds > 0f
                ? Time.time + offender.RiotSeconds
                : float.PositiveInfinity;
            string window = offender.RiotSeconds > 0f ? $"{offender.RiotSeconds:F0}초" : "무제한";
            Debug.Log($"[돌발이벤트] 탈옥 방출 — 소란 재개({offender.RiotBehavior}, {window}): {name}");
        }
    }
}
