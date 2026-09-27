/// <summary>
/// 사망(Dead) 상태 — 아무것도 판단하지 않는다. 진입 정리는 NpcDeath가 한다.
/// </summary>
public class NpcDeadState : NpcStateBase
{
    public NpcDeadState(NpcController owner)
        : base(owner) { }

    public override void Enter() { }

    public override void Tick() { }

    public override void Exit() { }
}
