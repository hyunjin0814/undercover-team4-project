using UnityEngine;

/// <summary>
/// 밀수 운반책 돌발 이벤트 — 화물을 진 NPC가 맨홀로 향하며, 무거워서 2인 이상이 밧줄로 끌어야 한다(GDD 6-4).
/// 맨홀에 도착하면 사라지고, 제압·인계하면 경범죄로 판정된다.
/// </summary>
public class SmugglerCourierEvent : SpawnedNpcEventBase
{
    private enum EPhase
    {
        None,
        Failed,
        LidOpening,
        Descending,
    }

    [Header("밀수 운반책")]
    [Tooltip("화물 무게 — 표준 시민이 1.0. 무게 1당 끄는 속도 -25%로, 혼자서는 못 끌게 한다")]
    [Min(0f)]
    [SerializeField]
    private float m_cargoWeight = 5f;

    [Tooltip("평소 걸음 속도 배율 — 짐이 무거워 보이게 기본 걸음보다 느리다. 개체별 속도 편차 위에 곱해진다")]
    [Range(0.1f, 1f)]
    [SerializeField]
    private float m_walkSpeedMultiplier = 0.75f;

    [Tooltip("맞은 뒤 속도 배율(되돌아가지 않음). 달리기 모션 임계(4m/s)를 넘고 플레이어 달리기(8)보단 느리게")]
    [Min(0.1f)]
    [SerializeField]
    private float m_panicSpeedMultiplier = 2.5f;

    [Header("맨홀 지점")]
    [Tooltip("도망칠 맨홀 후보 — 납치(AbductionEvent)가 쓰는 것과 같은 지점을 꽂는다. " +
             "스폰 자리에서 가장 먼 곳을 고른다(이동 시간이 곧 제한시간이다). 비우면 발동하지 않는다")]
    [SerializeField]
    private Transform[] m_manholePoints;

    [Tooltip("맨홀 뚜껑이 열리는 것을 보여 주는 시간(초) — AbductionManhole의 슬라이드 시간보다 길게 둘 것")]
    [Min(0f)]
    [SerializeField]
    private float m_lidOpenSeconds = 1.4f;

    [Tooltip("맨홀 아래로 내려가는 깊이(m)")]
    [Min(0.5f)]
    [SerializeField]
    private float m_descendDepth = 3f;

    [Tooltip("내려가는 데 걸리는 시간(초)")]
    [Min(0.1f)]
    [SerializeField]
    private float m_descendSeconds = 1.5f;

    private EPhase m_phase;
    private NpcController m_endingNpc;
    private AbductionManhole m_endingManhole;
    private float m_phaseStartTime;
    private Vector3 m_descendFrom;

    public override string NoticeKey => "Hud.Event.Notice.SmugglerCourier";

    protected override ERiotBehavior RiotBehavior => ERiotBehavior.Flee;

    /// <summary>갈 곳이 없으면 발생하지 않는다 — 맨홀 지점을 안 꽂으면 운반책이 제자리에 서 있는 그림이 된다.</summary>
    public override bool CanTrigger() => base.CanTrigger() && FindFarthestManhole(Vector3.zero) != null;

    protected override void ApplyBehavior(NpcController npc)
    {
        npc.Rope.ServerSetDragWeight(m_cargoWeight, ignoreSpeedFloor: true);

        Transform manhole = FindFarthestManhole(npc.transform.position);

        npc.Health.OnDamaged += HandleDamaged;
        npc.Stun.OnStunned += HandleStunned;

        SmugglerCargo cargo = GetOrAddCargo(npc);
        cargo.OnFinished += HandleFinished;
        cargo.ServerBeginSmuggling(manhole, m_walkSpeedMultiplier, m_panicSpeedMultiplier);
    }

    /// <summary>공통 소란 타이머 대신 맨홀 도착 결말 단계를 진행한다. 항상 true를 돌려준다.</summary>
    protected override bool OnServerTick()
    {
        if (m_endingNpc == null)
        {
            m_phase = EPhase.None;
            return true;
        }

        switch (m_phase)
        {
            case EPhase.Failed:
                Debug.Log($"[돌발이벤트] {DisplayName} — 경로 불발, 도심에 잔류");
                NpcController failed = m_endingNpc;
                ClearEnding();
                failed.Reaction.StartFlee(null);
                break;

            case EPhase.LidOpening:
                if (m_endingNpc.CurrentState != NpcState.Smuggling)
                {
                    Debug.Log($"[돌발이벤트] {DisplayName} — 뚜껑 앞에서 붙잡혔다, 하강 취소");
                    if (m_endingManhole != null)
                        m_endingManhole.ServerClose();
                    ClearEnding();
                    break;
                }

                if (Time.time - m_phaseStartTime >= m_lidOpenSeconds)
                    BeginDescend();
                break;

            case EPhase.Descending:
                TickDescend();
                break;
        }

        return true;
    }

    private void HandleDamaged(NpcController npc, GameObject attacker) => Panic(npc, "피격");

    private void HandleStunned(NpcController npc, Transform by) => Panic(npc, "무력화");

    private void Panic(NpcController npc, string reason)
    {
        SmugglerCargo cargo = npc != null ? npc.GetComponent<SmugglerCargo>() : null;
        if (cargo == null || cargo.IsPanicked)
            return;

        cargo.ServerPanic();
        Debug.Log($"[돌발이벤트] {DisplayName} — {reason}, 맨홀로 달리기 시작");
    }

    private void HandleFinished(NpcController npc, bool reached)
    {
        if (npc == null || m_endingNpc != null)
            return;

        m_endingNpc = npc;
        m_phaseStartTime = Time.time;

        if (!reached)
        {
            m_phase = EPhase.Failed;
            return;
        }

        SmugglerCargo cargo = npc.GetComponent<SmugglerCargo>();
        Transform point = cargo != null ? cargo.Destination : null;
        m_endingManhole = point != null ? point.GetComponentInChildren<AbductionManhole>() : null;
        if (m_endingManhole != null)
            m_endingManhole.ServerOpen();

        m_phase = EPhase.LidOpening;
        Debug.Log($"[돌발이벤트] {DisplayName} — 맨홀 도착, 뚜껑 열림");
    }

    private void BeginDescend()
    {
        m_phase = EPhase.Descending;
        m_phaseStartTime = Time.time;
        m_descendFrom = m_endingNpc.transform.position;

        if (m_endingNpc.Agent != null)
            m_endingNpc.Agent.enabled = false;
    }

    private void TickDescend()
    {
        float t = Mathf.Clamp01((Time.time - m_phaseStartTime) / m_descendSeconds);
        m_endingNpc.transform.position = m_descendFrom + Vector3.down * (m_descendDepth * t);
        if (t < 1f)
            return;

        if (m_endingManhole != null)
            m_endingManhole.ServerClose();

        NpcController npc = m_endingNpc;
        ClearEnding();
        Debug.Log($"[돌발이벤트] {DisplayName} — 화물이 지하로 빠져나갔다");
        ServerDespawnSpawned(npc, playVfx: false);
    }

    private void ClearEnding()
    {
        m_phase = EPhase.None;
        m_endingNpc = null;
        m_endingManhole = null;
    }

    private Transform FindFarthestManhole(Vector3 origin)
    {
        Transform farthest = null;
        float bestSqr = -1f;

        if (m_manholePoints == null)
            return null;

        for (int i = 0; i < m_manholePoints.Length; i++)
        {
            Transform p = m_manholePoints[i];
            if (p == null)
                continue;

            float sqr = (p.position - origin).sqrMagnitude;
            if (sqr <= bestSqr)
                continue;

            bestSqr = sqr;
            farthest = p;
        }

        return farthest;
    }

    protected override void OnReleasing(NpcController npc) => Unsubscribe(npc);

    protected override void OnDespawning(NpcController npc) => Unsubscribe(npc);

    private void Unsubscribe(NpcController npc)
    {
        if (npc == null)
            return;

        npc.Health.OnDamaged -= HandleDamaged;
        npc.Stun.OnStunned -= HandleStunned;

        SmugglerCargo cargo;
        if (npc.TryGetComponent(out cargo))
            cargo.OnFinished -= HandleFinished;

        if (m_endingNpc == npc)
        {
            if (m_endingManhole != null)
                m_endingManhole.ServerClose();
            ClearEnding();
        }
    }

    private static SmugglerCargo GetOrAddCargo(NpcController npc)
    {
        SmugglerCargo cargo;
        return npc.TryGetComponent(out cargo) ? cargo : npc.gameObject.AddComponent<SmugglerCargo>();
    }
}
