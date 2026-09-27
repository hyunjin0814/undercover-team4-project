using System;
using System.Collections.Generic;

/// <summary>
/// NPC 상태 머신 — 상태를 등록하고 전이하며, 전이 전후 이벤트를 발행한다.
/// </summary>
public class NpcStateMachine
{
    private readonly Dictionary<NpcState, NpcStateBase> m_states =
        new Dictionary<NpcState, NpcStateBase>();
    private NpcStateBase m_currentState;

    public NpcState CurrentState { get; private set; }

    public event Action<NpcState> OnStateChanged;

    public event Action<NpcState> OnBeforeEnter;

    public void AddState(NpcState state, NpcStateBase stateInstance)
    {
        m_states[state] = stateInstance;
    }

    public void ChangeState(NpcState state)
    {
        if (m_currentState != null && CurrentState == state)
            return;

        if (CurrentState == NpcState.Dead)
        {
            UnityEngine.Debug.LogError(
                $"NpcStateMachine: 죽은 NPC를 {state}(으)로 되돌리려 했다 — 무시한다. "
                    + "그 시스템이 NpcDeath.OnDied로 자기 참조를 정리해야 한다"
            );
            return;
        }

        m_currentState?.Exit();
        CurrentState = state;
        m_currentState = m_states[state];
        OnBeforeEnter?.Invoke(state);
        m_currentState.Enter();
        OnStateChanged?.Invoke(state);
    }

    public void Tick()
    {
        m_currentState?.Tick();
    }
}
