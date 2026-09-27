/// <summary>
/// NPC FSM 상태의 베이스 — Enter/Tick/Exit를 정의한다.
/// </summary>
public abstract class NpcStateBase
{
    protected readonly NpcController m_owner;

    protected NpcStateBase(NpcController owner)
    {
        m_owner = owner;
    }

    public abstract void Enter();
    public abstract void Tick();
    public abstract void Exit();
}
