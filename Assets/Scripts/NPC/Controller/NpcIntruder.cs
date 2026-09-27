using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 침입 도메인 부품 — 침입자를 유치장 자물쇠까지 보내고 해제 진행을 이벤트로 알린다(GDD 6-4).
/// NpcController와 같은 GameObject에 둔다.
/// </summary>
public class NpcIntruder : NetworkBehaviour
{
    private NpcController m_owner;

    public Transform IntrudeTarget { get; private set; }

    public float IntrudeUnlockSeconds { get; private set; }

    public event Action<NpcController, bool> OnIntrudeFinished;

    public event Action<NpcController> OnIntrudeUnlockStarted;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
    }

    /// <summary>침입자를 target까지 걷게 하고, 도달하면 unlockSeconds 동안 해제 채널링을 시킨다.</summary>
    public void StartIntrude(Transform target, float unlockSeconds)
    {
        if (IsSpawned && !IsServer)
            return;

        IntrudeTarget = target;
        IntrudeUnlockSeconds = unlockSeconds;
        m_owner.StateMachine.ChangeState(NpcState.Intruding);
    }

    /// <summary>침입 이동 종료 통보 — NpcIntrudeState 전용.</summary>
    public void NotifyIntrudeFinished(bool reached) => OnIntrudeFinished?.Invoke(m_owner, reached);

    /// <summary>자물쇠 해제 착수 통보 — NpcIntrudeState 전용.</summary>
    public void NotifyIntrudeUnlockStarted() => OnIntrudeUnlockStarted?.Invoke(m_owner);
}
