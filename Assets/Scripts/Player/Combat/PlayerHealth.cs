using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 HP를 서버 권위로 관리한다 — 피해·부활을 처리하고 HP 0이면 다운 무력화로 넘긴다.
/// </summary>
public class PlayerHealth : NetworkBehaviour, IDamageable
{
    [Header("스테이터스")]
    [SerializeField]
    private int m_maxHp = 100;

    [Tooltip("부활 시 회복되는 HP — 부분 회복 (GDD 7-5, #105)")]
    [SerializeField]
    private int m_reviveHp = 50;

    private readonly NetworkVariable<int> m_syncedHp = new NetworkVariable<int>();
    private int m_hp;

    private PlayerIncapacitation m_incapacitation;

    private PlayerPenaltyView m_penaltyView;

    public ulong PlayerId => OwnerClientId;
    public int MaxHp => m_maxHp;
    public int CurrentHp => IsSpawned ? m_syncedHp.Value : m_hp;

    public bool IsTargetable =>
        CurrentHp > 0 && (m_incapacitation == null || !m_incapacitation.IsIncapacitated);

    public bool IsDamageable =>
        IsTargetable
        || (CurrentHp > 0 && m_incapacitation != null && m_incapacitation.IsLaunched);

    private static readonly System.Collections.Generic.List<PlayerHealth> s_instances = new();

    public static System.Collections.Generic.IReadOnlyList<PlayerHealth> All => s_instances;

    private void OnEnable() => s_instances.Add(this);

    private void OnDisable() => s_instances.Remove(this);

    /// <summary>반경 안에서 비행(Launched) 중인 플레이어를 등록 목록으로 모은다(물리 쿼리에 안 걸리므로).</summary>
    public static void CollectLaunched(
        Vector3 origin,
        float radius,
        System.Collections.Generic.List<PlayerHealth> results
    )
    {
        results.Clear();

        System.Collections.Generic.IReadOnlyList<PlayerIncapacitation> candidates =
            PlayerIncapacitation.All;
        float maxSqr = radius * radius;
        for (int i = 0; i < candidates.Count; i++)
        {
            PlayerIncapacitation incapacitation = candidates[i];

            if (incapacitation == null || !incapacitation.IsLaunched)
                continue;

            if ((incapacitation.transform.position - origin).sqrMagnitude > maxSqr)
                continue;

            PlayerHealth player = incapacitation.GetComponent<PlayerHealth>();
            if (player != null && player.IsDamageable && !player.IsTargetable)
                results.Add(player);
        }
    }

    private void Awake()
    {
        m_hp = m_maxHp;
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_penaltyView = GetComponent<PlayerPenaltyView>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            SetHp(m_maxHp);
        }
    }

    private const int k_tutorialMinHp = 10;

    public void ModifyHp(int delta) => ApplyHpDelta(delta, skipGrace: false);

    private void ApplyHpDelta(int delta, bool skipGrace)
    {
        if (IsSpawned && !IsServer) return;

        int floor = TutorialDirector.IsActive ? k_tutorialMinHp : 0;
        SetHp(Mathf.Clamp(CurrentHp + delta, floor, m_maxHp), skipGrace);
    }

    public event System.Action<DamageHit> OnDamaged;

    /// <summary>피해를 적용하고 OnDamaged를 발행한다. HP 0이면 다운 유예, 이미 다운이면 즉시 사망이다.</summary>
    public void TakeDamage(int amount, GameObject attacker)
    {
        if (IsSpawned && !IsServer)
            return;
        if (amount <= 0)
            return;

        ApplyDamage(amount, attacker, skipGrace: false);
    }

    /// <summary>다운 유예 없는 피해를 적용한다 — HP 0에 도달하면 곧바로 기능 정지(차량·폭발용).</summary>
    public void TakeLethalDamage(int amount, GameObject attacker)
    {
        if (IsSpawned && !IsServer)
            return;
        if (amount <= 0)
            return;

        ApplyDamage(amount, attacker, skipGrace: true);
    }

    private void ApplyDamage(int amount, GameObject attacker, bool skipGrace)
    {
        if (m_incapacitation != null && m_incapacitation.IsDowned)
        {
            m_incapacitation.ServerFinishOff();

            if (attacker != null && attacker.TryGetComponent(out PlayerKillCredit killCredit))
            {
                string victimName = GetComponent<PlayerNameTag>()?.DisplayName;
                killCredit.ServerCreditKill(
                    string.IsNullOrEmpty(victimName) ? "동료" : victimName,
                    friendlyFire: true
                );
            }
            return;
        }

        int before = CurrentHp;
        ApplyHpDelta(-amount, skipGrace);

        int applied = before - CurrentHp;
        if (applied <= 0)
            return;

        BroadcastDamaged(applied, attacker);
    }

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
        OnDamaged?.Invoke(new DamageHit(amount, attackerPosition, hasAttacker));

    /// <summary>HP를 일부 회복하고 무력화를 해제한다. 서버(또는 오프라인) 전용.</summary>
    public void ServerRevive()
    {
        if (IsSpawned && !IsServer)
            return;
        if (CurrentHp > 0)
            return;

        SetHp(Mathf.Clamp(m_reviveHp, 1, m_maxHp));
        m_incapacitation?.Recover();

        GetComponent<PlayerLoadout>()?.ServerNotifyHeldItemsChanged();
    }

    private void SetHp(int value) => SetHp(value, skipGrace: false);

    private void SetHp(int value, bool skipGrace)
    {
        int previous = CurrentHp;
        m_hp = value;
        if (IsSpawned && IsServer)
            m_syncedHp.Value = value;

        if (value != 0 || previous <= 0)
            return;

        if (skipGrace)
        {
            Debug.Log($"[Die] 즉사 피해 — 유예 없이 기능 정지: {name}", this);
            m_incapacitation?.Incapacitate(IncapacitationCause.Die);
            return;
        }

        m_incapacitation?.Incapacitate(IncapacitationCause.Down);
    }

    /// <summary>라운드 사이 상태 초기화 — HP 풀 회복 + 다운 해제 + 끌려가기 해제. 서버(또는 오프라인)에서만. (상점 진입)</summary>
    public void ServerResetState()
    {
        if (IsSpawned && !IsServer)
            return;
        SetHp(m_maxHp);
        m_incapacitation?.Recover();
        m_incapacitation?.ServerResetRound();
        m_penaltyView?.StopCarried();
    }
}
