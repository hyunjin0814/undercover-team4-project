using System;
using UnityEngine;

/// <summary>
/// 밀수 운반책의 목적지를 들고 운반을 시작·가속시키는 부품. 스폰 직후 런타임에 부착된다.
/// </summary>
public class SmugglerCargo : MonoBehaviour
{
    public Transform Destination { get; private set; }

    public float SpeedMultiplier { get; private set; } = 1f;

    public bool IsPanicked { get; private set; }

    public event Action<NpcController, bool> OnFinished;

    private float m_walkMultiplier = 1f;
    private float m_panicMultiplier = 1f;

    /// <summary>맨홀 목적지를 설정하고 FSM을 Smuggling 상태로 넘긴다. 서버(또는 오프라인) 전용.</summary>
    public void ServerBeginSmuggling(
        Transform destination,
        float walkMultiplier,
        float panicMultiplier
    )
    {
        Destination = destination;
        m_walkMultiplier = Mathf.Max(0.1f, walkMultiplier);
        m_panicMultiplier = Mathf.Max(m_walkMultiplier, panicMultiplier);
        SpeedMultiplier = m_walkMultiplier;
        IsPanicked = false;

        GetComponent<NpcController>().StateMachine.ChangeState(NpcState.Smuggling);
    }

    /// <summary>맞으면 상태는 유지한 채 맨홀로 달리도록 속도를 올린다. 서버(또는 오프라인) 전용.</summary>
    public void ServerPanic()
    {
        if (IsPanicked)
            return;

        IsPanicked = true;
        SpeedMultiplier = m_panicMultiplier;
    }

    /// <summary>운반 종료 통보 — <see cref="NpcSmuggleState"/> 전용.</summary>
    public void NotifyFinished(NpcController npc, bool reached) => OnFinished?.Invoke(npc, reached);
}
