/// <summary>
/// 동네 깡패 돌발 이벤트 — 표적 플레이어 한 명을 정해 파이프를 들고 쫓아가 때린다(GDD 6-4).
/// NpcResistState를 쓰고 깡패 전용 config로 포기하지 않게 하며, 소란 시간은 무제한이다.
/// </summary>
public class StreetThugEvent : SpawnedNpcEventBase
{
    public override string NoticeKey => "Hud.Event.Notice.StreetThug";

    protected override ERiotBehavior RiotBehavior => ERiotBehavior.Resist;

    protected override void ApplyBehavior(NpcController npc)
    {
        npc.Reaction.StartResist(m_threat, relentless: true);
    }
}
