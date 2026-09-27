using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NPC 체력 도메인 부품 — 0이 되면 쓰러지고, 쓰러진 몸이 또 맞거나 치명 피해를 받으면 죽는다.
/// 서버(또는 오프라인)만 값을 바꾸고 클라는 동기화 값을 읽는다.
/// </summary>
public class NpcHealth : NetworkBehaviour, IDamageable
{
    private NpcController m_owner;

    private readonly NetworkVariable<int> m_syncedHp = new NetworkVariable<int>();
    private int m_hp;

    private float m_secondsSinceDamage;

    private float m_regenAccumulator;

    public int MaxHp => m_owner.CommonConfig.MaxHp;

    public int CurrentHp => IsSpawned ? m_syncedHp.Value : m_hp;

    public event Action<NpcController, GameObject> OnDamaged;

    public event Action<DamageHit> OnHit;

    private void Awake()
    {
        m_owner = GetComponent<NpcController>();
    }

    /// <summary>체력 초기화 — 코어의 InitBehavior에서 서버(또는 오프라인) 1회 호출된다.</summary>
    internal void InitHealth()
    {
        SetHp(MaxHp, null);
    }

    /// <summary>플레이어 타격 피해를 적용한다. CanBeDamaged 게이트를 통과해야 한다.</summary>
    public void TakeDamage(int amount, GameObject attacker)
    {
        if (IsSpawned && !IsServer)
            return;
        if (amount <= 0)
            return;
        if (!NpcStateRules.CanBeDamaged(m_owner))
            return;

        ApplyDamage(amount, attacker);
    }

    /// <summary>차량·폭발 등 환경 피해를 적용한다(연행 중인 신병도 맞는다).</summary>
    public void TakeEnvironmentalDamage(int amount, GameObject attacker)
    {
        if (IsSpawned && !IsServer)
            return;
        if (amount <= 0)
            return;
        if (!NpcStateRules.CanTakeEnvironmentalDamage(m_owner))
            return;

        ApplyDamage(amount, attacker);
    }

    private void ApplyDamage(int amount, GameObject attacker)
    {
        OnDamaged?.Invoke(m_owner, attacker);

        int before = CurrentHp;
        SetHp(Mathf.Clamp(before - amount, 0, MaxHp), attacker, IsLethal(before, amount));

        int applied = before - CurrentHp;
        if (applied > 0)
            BroadcastDamaged(applied, attacker);

        m_secondsSinceDamage = 0f;
        m_regenAccumulator = 0f;
    }

    /// <summary>쓰러뜨리는 대신 죽이는 타격인가 — 확인사살(이미 0)이거나 오버킬. 피해를 얹기 전 값으로 판정한다.</summary>
    private bool IsLethal(int before, int amount) =>
        before == 0 || amount - before >= m_owner.CommonConfig.LethalOverkillHp;

    private void BroadcastDamaged(int amount, GameObject attacker)
    {
        bool hasAttacker = attacker != null;
        Vector3 attackerPosition = hasAttacker ? attacker.transform.position : Vector3.zero;

        if (!IsSpawned)
        {
            RaiseDamaged(amount, attackerPosition, hasAttacker);
            return;
        }

        PlayDamagedRpc(amount, attackerPosition, hasAttacker);
    }

    [Rpc(SendTo.Everyone)]
    private void PlayDamagedRpc(int amount, Vector3 attackerPosition, bool hasAttacker) =>
        RaiseDamaged(amount, attackerPosition, hasAttacker);

    private void RaiseDamaged(int amount, Vector3 attackerPosition, bool hasAttacker) =>
        OnHit?.Invoke(new DamageHit(amount, attackerPosition, hasAttacker));

    /// <summary>쓰러졌던 몸을 체력 1로 일으킨다. 체력이 0이 아니면 무동작. 서버(또는 오프라인) 전용.</summary>
    internal void ServerRestoreToOne()
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_owner.Death.IsDead || CurrentHp > 0)
            return;

        SetHp(1, null);
    }

    /// <summary>NavMesh 밖에서 굳은 몸을 체력 0을 거쳐 죽인다. 서버(또는 오프라인) 전용.</summary>
    internal void ServerKillStuck()
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_owner.Death.IsDead)
            return;

        SetHp(0, null, lethal: true);
    }

    /// <summary>방치 회복 틱 — NpcController.Update가 사망 게이트 통과 직후 매 프레임 부른다. 서버(또는 오프라인) 전용.</summary>
    internal void Tick()
    {
        if (IsSpawned && !IsServer)
            return;

        m_secondsSinceDamage += Time.deltaTime;

        if (CurrentHp >= MaxHp)
            return;
        if (m_secondsSinceDamage < m_owner.CommonConfig.RegenDelaySeconds)
            return;
        if (!NpcStateRules.CanRegenerate(m_owner))
            return;

        m_regenAccumulator += m_owner.CommonConfig.RegenHpPerSecond * Time.deltaTime;
        int wholeHp = Mathf.FloorToInt(m_regenAccumulator);
        if (wholeHp <= 0)
            return;

        m_regenAccumulator -= wholeHp;
        SetHp(Mathf.Min(CurrentHp + wholeHp, MaxHp), null);
    }

    /// <summary>수감 시 체력을 최대로 되돌린다(죽은 대상 제외). 서버(또는 오프라인) 전용.</summary>
    internal void ServerRestoreFull()
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_owner.Death.IsDead)
            return;

        SetHp(MaxHp, null);
    }

    private void SetHp(int value, GameObject attacker, bool lethal = false)
    {
        int previous = CurrentHp;
        m_hp = value;
        if (IsSpawned && IsServer)
            m_syncedHp.Value = value;

        if (lethal)
        {
            m_owner.Death.ServerEnterDead(attacker);
            return;
        }

        if (value == 0 && previous > 0)
            m_owner.Stun.EnterStunned(
                attacker != null ? attacker.transform : null,
                m_owner.StunConfig.KnockdownStunSeconds
            );
    }
}
