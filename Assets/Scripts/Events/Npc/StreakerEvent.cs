/// <summary>
/// 공연음란범 돌발 이벤트 — 속옷 차림 NPC가 도심을 쉬지 않고 질주한다(GDD 6-4).
/// 진정·잔류 없이 라운드당 한 명이며, 잡아서 인계하면 이벤트가 풀린다.
/// </summary>
public class StreakerEvent : SpawnedNpcEventBase
{
    public override string NoticeKey => "Hud.Event.Notice.Streaker";

    protected override ERiotBehavior RiotBehavior => ERiotBehavior.Sprint;

    protected override void ApplyBehavior(NpcController npc)
    {
        npc.Reaction.StartSprint();
    }
}
