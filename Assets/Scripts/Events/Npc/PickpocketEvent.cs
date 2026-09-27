using UnityEngine;

/// <summary>
/// 소매치기 돌발 이벤트 — 현장 플레이어에게 걸어가 밀착하면 물건 하나를 채고 달아난다(GDD 6-4).
/// 접근은 추격 파이프라인을 빌려 쓰고, 훔친 물건의 결말은 Pickpocket이 맡는다.
/// </summary>
public class PickpocketEvent : SpawnedNpcEventBase
{
    [Header("소매치기")]
    [Tooltip("표적에게 다가가는 제한 시간(초) — 이 안에 붙지 못하면 포기하고 시민으로 잔류한다. 걷는 속도라 표적이 계속 움직이면 못 붙는다")]
    [SerializeField]
    private float m_approachSeconds = 12f;

    private float m_giveUpTime;

    public override bool AnnounceOnBegin => false;

    protected override ERiotBehavior RiotBehavior => ERiotBehavior.Flee;

    protected override void ApplyBehavior(NpcController npc)
    {
        if (m_threat == null)
            return;

        npc.Penalty.OnPenaltyCaught += HandleReach;

        npc.Stun.OnStunned += HandleStunned;

        npc.Penalty.StartPenaltyChase(m_threat, NpcDutyKind.Pickpocket);
        m_giveUpTime = Time.time + m_approachSeconds;
    }

    protected override bool OnServerTick()
    {
        if (m_giveUpTime <= 0f || Time.time < m_giveUpTime)
            return false;

        m_giveUpTime = 0f;
        NpcController npc = PrimaryNpc;
        if (npc == null || !npc.Penalty.IsPickpocketDuty)
            return false;

        Debug.Log($"[돌발이벤트] {DisplayName} — 접근 실패, 포기하고 시민으로 섞임");
        npc.Penalty.EndPenaltyDuty();
        return true;
    }

    private void HandleReach(NpcController npc, Transform caught)
    {
        if (npc == null || npc != PrimaryNpc)
            return;

        npc.Penalty.OnPenaltyCaught -= HandleReach;
        m_giveUpTime = 0f;

        PlayerLoadout victim = caught != null ? caught.GetComponentInParent<PlayerLoadout>() : null;
        ItemBase stolen = victim != null ? GetOrAddPickpocket(npc).ServerStealFrom(victim) : null;

        if (stolen != null)
        {
            victim.GetComponent<PlayerTheftView>()?.ShowStolen();

            Debug.Log($"[돌발이벤트] {DisplayName} — {stolen.name} 탈취, 도주 시작");
        }
        else
        {
            Debug.Log($"[돌발이벤트] {DisplayName} — 뺏을 소지품이 없어 빈손으로 도주");
        }

        npc.Reaction.StartFlee(m_threat);
    }

    private void HandleStunned(NpcController npc, Transform by)
    {
        if (npc == null || npc != PrimaryNpc)
            return;

        DropStolen(npc);
    }

    protected override void OnCaptured(NpcController npc) => DropStolen(npc);

    private static void DropStolen(NpcController npc)
    {
        if (npc != null && npc.TryGetComponent(out Pickpocket thief))
            thief.ServerDropStolenItem();
    }

    protected override void OnReleasing(NpcController npc)
    {
        Unsubscribe(npc);
        m_giveUpTime = 0f;
    }

    protected override void OnDespawning(NpcController npc)
    {
        Unsubscribe(npc);
        m_giveUpTime = 0f;

        if (npc != null && npc.TryGetComponent(out Pickpocket thief))
            thief.ServerLoseStolenItem();
    }

    private void Unsubscribe(NpcController npc)
    {
        if (npc == null)
            return;

        npc.Penalty.OnPenaltyCaught -= HandleReach;
        npc.Stun.OnStunned -= HandleStunned;
    }

    private static Pickpocket GetOrAddPickpocket(NpcController npc) =>
        npc.TryGetComponent(out Pickpocket thief) ? thief : npc.gameObject.AddComponent<Pickpocket>();
}
