using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 밧줄이 풀린 몸이 일어나는 구간을 관리한다 — 전 피어에 기상 모션을 알리고 클립이 끝나면 후속 동작을 실행한다.
/// </summary>
public class NpcStandUp : NetworkBehaviour
{
    private NpcController m_owner;

    private System.Action m_standUpNext;
    private bool m_standUpPending;
    private float m_standUpRemaining;

    private float m_standUpDownRemaining;

    private readonly NetworkVariable<bool> m_standUpPendingSynced = new(false);

    private readonly NetworkVariable<bool> m_playingStandUpSynced = new(false);
    private bool m_playingStandUp;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
    }

    public bool IsStandingUp =>
        IsSpawned && !IsServer ? m_standUpPendingSynced.Value : m_standUpPending;

    private void SetStandUpPending(bool value)
    {
        m_standUpPending = value;
        if (IsSpawned && IsServer)
            m_standUpPendingSynced.Value = value;
    }

    public bool IsPlayingStandUp =>
        IsSpawned && !IsServer ? m_playingStandUpSynced.Value : m_playingStandUp;

    private void SetPlayingStandUp(bool value)
    {
        m_playingStandUp = value;
        if (IsSpawned && IsServer)
            m_playingStandUpSynced.Value = value;
    }

    /// <summary>기상 모션을 알리고 클립 길이 후 next를 실행한다. 묶여 있지 않으면 즉시 실행. 서버(또는 오프라인) 전용.</summary>
    public void ServerStandUpThen(System.Action next, float downSeconds = 0f)
    {
        if (IsSpawned && !IsServer)
            return;

        if (m_standUpPending)
        {
            if (next != null)
                m_standUpNext = next;
            return;
        }

        m_owner.Stun.ExitStun(false);

        NpcRopeDrag rope = m_owner.Rope;
        if (!rope.IsTethered && !rope.IsRoped)
        {
            next?.Invoke();
            return;
        }

        SetStandUpPending(true);
        m_standUpNext = next;
        m_standUpRemaining = m_owner.StunConfig.StandUpSeconds;
        m_standUpDownRemaining = Mathf.Max(0f, downSeconds - m_owner.StunConfig.StandUpSeconds);

        if (m_standUpDownRemaining <= 0f)
            m_owner.RaiseStandUp();
    }

    /// <summary>일어나기 예약과 후속 동작을 취소한다.</summary>
    internal void CancelStandUp()
    {
        SetStandUpPending(false);
        SetPlayingStandUp(false);
        m_standUpNext = null;
        m_standUpRemaining = 0f;
        m_standUpDownRemaining = 0f;
    }

    /// <summary>일어나기 대기를 진행한다. 코어 Update가 넉백·스턴 게이트 전에 호출한다.</summary>
    internal void Tick()
    {
        if (!m_standUpPending)
            return;

        if (
            (m_owner.CurrentState != NpcState.Captured && m_owner.CurrentState != NpcState.Jailed)
            || m_owner.Knockback.IsKnockedBack
            || m_owner.Stun.HasStunOverlay
        )
        {
            CancelStandUp();
            return;
        }

        if (m_standUpDownRemaining > 0f)
        {
            m_standUpDownRemaining -= Time.deltaTime;
            if (m_standUpDownRemaining > 0f)
                return;

            m_owner.RaiseStandUp();
            SetPlayingStandUp(true);
        }

        m_standUpRemaining -= Time.deltaTime;
        if (m_standUpRemaining > 0f)
            return;

        System.Action next = m_standUpNext;
        CancelStandUp();
        next?.Invoke();
    }
}
