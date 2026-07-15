using UnityEngine;
using UnityEngine.AI;
using Unity.Netcode;

/// <summary>
/// 괴한 — 괴한 습격(돌발 이벤트 · 현장)에서 스폰되는 위협 개체. (GDD 6-4/7-4, #106)
/// 저항형 NPC와 달리 <b>제압 대상이 아니다</b>(NpcSubdueInteractable가 없어 E로 반응하지 않는다) —
/// 가장 가까운 <b>행동 가능한</b> 현장 플레이어를 NavMesh로 추격하며 근접 시 주기적으로 HP를 깎는다
/// (<see cref="IDamageable.TakeDamage"/> 공통 경로). 표적이 다운되면 즉시 버리고 다른 플레이어로 옮겨간다 —
/// 다운은 이미 무력화이므로 계속 때릴 이유가 없고, 동료가 구조(#105)하러 올 여지를 남긴다.
/// 플레이어는 회피하거나, 다운되면 동료 구조(#105)에 의존한다.
/// 지속 시간 관리·디스폰은 <see cref="ThugAssaultEvent"/>가 담당하고, 이 컴포넌트는 추격·타격 행동만 맡는다.
///
/// 서버 권위 — 추격·타격 판단은 서버(또는 오프라인) 전용이고, 클라이언트는 NetworkTransform으로 동기화된
/// 위치만 표현한다. HP 감소는 PlayerData의 동기화 HP를 통해 전 클라에 반영된다. (#56 패턴, NpcController와 동일)
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
public class ThugAttacker : NetworkBehaviour
{
    [Header("추격")]
    [Tooltip("이 반경(m) 안에서 가장 가까운 현장 플레이어를 표적으로 삼는다 (다운된 플레이어 제외)")]
    [SerializeField] private float m_targetSearchRadius = 40f;
    [Tooltip("표적을 다시 고르는 주기(초) — 이동 목적지는 매 프레임 갱신되므로 추격 자체는 끊기지 않는다")]
    [SerializeField] private float m_retargetInterval = 0.25f;

    [Header("근접 타격")]
    [Tooltip("표적과 이 거리(m) 이내로 붙으면 타격한다")]
    [SerializeField] private float m_attackRange = 2f;
    [Tooltip("타격 주기(초)")]
    [SerializeField] private float m_attackInterval = 1.2f;
    [Tooltip("타격 1회당 플레이어 HP 감소량")]
    [SerializeField] private int m_attackDamage = 12;

    [Header("소란 (#81 패닉 전파)")]
    [Tooltip("습격이 주변 시민을 패닉시키는 전파 반경(m)")]
    [SerializeField] private float m_disturbanceRadius = 8f;
    [Tooltip("소란 펄스 주기(초)")]
    [SerializeField] private float m_disturbancePulseInterval = 1f;

    private NavMeshAgent m_agent;
    private PlayerData m_target;
    private float m_nextRetargetTime;
    private float m_nextAttackTime;
    private float m_nextPulseTime;

    /// <summary>타격을 한 번 휘두를 때 발행 — 전 피어에서 발생한다(서버는 로컬 발행 + ClientRpc 중계).
    /// 애니메이션 표현(<see cref="ThugAnimationDriver"/>)이 구독해 타격 모션을 재생한다. (#56 서버 권위 패턴)</summary>
    public event System.Action OnAttack;

    private void Awake()
    {
        m_agent = GetComponent<NavMeshAgent>();
    }

    public override void OnNetworkSpawn()
    {
        // 클라이언트의 위치는 NetworkTransform이 담당 — NavMeshAgent를 켜두면 동기화 위치와 싸운다 (NpcController와 동일)
        if (IsSpawned && !IsServer)
            m_agent.enabled = false;
    }

    private void Update()
    {
        // 추격·타격은 서버 전용 (오프라인 폴백 포함) — 클라이언트는 동기화된 위치만 표현한다 (#56)
        if (IsSpawned && !IsServer)
            return;

        PlayerData target = AcquireTarget();
        if (target == null)
        {
            if (m_agent.enabled && m_agent.isOnNavMesh)
                m_agent.isStopped = true;
            return;
        }

        ChaseAndAttack(target);
        EmitDisturbancePulse();
    }

    // 표적 유지 — 유효한 표적은 계속 쫓고, 다운·소멸한 표적은 즉시 버린다. 새 표적 선정은 주기적으로만 한다.
    private PlayerData AcquireTarget()
    {
        // 다운되거나 사라진 표적은 주기를 기다리지 않고 바로 놓아준다 — 쓰러진 플레이어를 계속 쫓지 않게
        if (m_target != null && !m_target.IsTargetable)
            m_target = null;

        // 재탐색은 주기적으로 — 매 프레임 씬을 훑지 않고, 표적이 프레임마다 흔들리지도 않는다
        if (Time.time >= m_nextRetargetTime)
        {
            m_nextRetargetTime = Time.time + m_retargetInterval;
            m_target = SuddenEventUtil.FindNearestFieldPlayer(transform.position, m_targetSearchRadius);
        }

        return m_target;
    }

    private void ChaseAndAttack(PlayerData target)
    {
        Vector3 targetPosition = target.transform.position;

        if (m_agent.enabled && m_agent.isOnNavMesh)
        {
            m_agent.isStopped = false;
            m_agent.SetDestination(targetPosition);
        }

        float sqrDistance = (targetPosition - transform.position).sqrMagnitude;
        if (sqrDistance > m_attackRange * m_attackRange)
            return; // 아직 사거리 밖 — 계속 추격

        if (Time.time < m_nextAttackTime)
            return;
        m_nextAttackTime = Time.time + m_attackInterval;

        NotifyAttack(); // 스윙 모션은 명중 여부와 무관하게 재생 (전 피어)

        // 표적은 AcquireTarget에서 이미 행동 가능(IsTargetable)한 것만 걸러진다 — 다운된 표적을 때릴 일은 없다
        ((IDamageable)target).TakeDamage(m_attackDamage, gameObject);
        Debug.Log($"괴한 습격 타격: {name} → {target.name} (-{m_attackDamage})");
    }

    // 습격 자체가 소란의 원천 — 주기적으로 주변 시민을 패닉시킨다 (#81, 저항 NPC의 EmitDisturbancePulse와 동일 패턴)
    private void EmitDisturbancePulse()
    {
        if (Time.time < m_nextPulseTime)
            return;
        m_nextPulseTime = Time.time + m_disturbancePulseInterval;
        NpcController.BroadcastDisturbance(transform.position, m_disturbanceRadius);
    }

    // 타격 스윙을 전 피어에 알린다 — 애니메이션 표현용. (SuddenEventManager.AnnounceEvent와 동일 패턴)
    private void NotifyAttack()
    {
        OnAttack?.Invoke(); // 서버·오프라인 로컬 발행
        if (IsSpawned && IsServer)
            PlayAttackClientRpc();
    }

    [ClientRpc]
    private void PlayAttackClientRpc()
    {
        // 서버(호스트)는 위에서 이미 발행했으므로 원격 클라에서만 중계
        if (IsServer)
            return;
        OnAttack?.Invoke();
    }

}
