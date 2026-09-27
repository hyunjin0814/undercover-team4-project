using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 폭발 적용 — 반경 내 플레이어·NPC에 피해를 주고 생사와 무관하게 래그돌로 날린다.
/// 세기는 BombBlastProfile이, 폭발 시점은 BombDevice가 정한다.
/// </summary>
public class BombBlast : NetworkBehaviour
{
    [Header("폭발 세기 (인스펙터 조절)")]
    [SerializeField]
    private BombBlastProfile m_profile = new BombBlastProfile();

    [Header("가림 판정")]
    [SerializeField]
    private LayerMask m_blockMask = 1;

    [Header("생존자 발사 (#815 문 재사용)")]
    [Tooltip("살아남은 NPC가 누워 있는 시간(초) — 비행 시간보다 넉넉히 길게 둘 것")]
    [SerializeField]
    private float m_launchStunSeconds = 6f;

    [Tooltip("살아남은 플레이어의 비행 상태 서버 최대시간(초) — 오너의 정착 통보가 안 오는 경우 " +
             "(연결 끊김 등)의 안전장치. 정상 정착(1~3초)보다 넉넉히 잡을 것")]
    [SerializeField]
    private float m_launchMaxSeconds = 6f;

    private const float k_occlusionOriginHeight = 0.4f;
    private const float k_occlusionTargetHeight = 1f;

    private const float k_occlusionProbeRadius = 0.2f;

    private readonly List<Transform> m_blastBuffer = new List<Transform>();

    private readonly List<NetworkObject> m_deathBuffer = new List<NetworkObject>();

    private static readonly Collider[] s_blastColliders = new Collider[256];

    private static readonly HashSet<NpcController> s_blastNpcs = new HashSet<NpcController>();

    public float ExplosionRadius => m_profile.Radius;

    /// <summary>폭심에서 <paramref name="targetPosition"/>이 받는 피해량 — 반경 밖이면 0.</summary>
    public int EvaluateDamage(Vector3 targetPosition)
    {
        return m_profile.EvaluateDamage(targetPosition - transform.position);
    }

    private Vector3 EvaluateRagdollImpulse(Vector3 targetPosition)
    {
        return m_profile.EvaluateRagdollImpulse(targetPosition - transform.position, transform.forward);
    }

    /// <summary>대상이 폭심에서 환경에 가려졌는지 판정한다.</summary>
    private bool IsOccluded(Vector3 targetPosition) =>
        AimOcclusion.IsEnvironmentBlocked(
            transform.position + Vector3.up * k_occlusionOriginHeight,
            targetPosition + Vector3.up * k_occlusionTargetHeight,
            m_blockMask,
            k_occlusionProbeRadius,
            transform
        );

    /// <summary>터진다 — 서버(또는 오프라인) 전용. 호출 시점과 중복 방지는 <see cref="BombDevice"/>가 쥔다.</summary>
    public void ServerExplode()
    {
        Vector3 origin = transform.position;

        m_deathBuffer.Clear();
        SuddenEventUtil.CollectDamageablePlayers(origin, m_profile.Radius, m_blastBuffer);
        for (int i = 0; i < m_blastBuffer.Count; i++)
        {
            Transform target = m_blastBuffer[i];
            if (IsOccluded(target.position))
                continue;

            bool hasHealth = target.TryGetComponent(out PlayerHealth health);
            if (hasHealth)
                health.TakeLethalDamage(EvaluateDamage(target.position), gameObject);

            PlayerEscorter escorter = target.GetComponent<PlayerEscorter>();
            if (escorter != null)
                escorter.ReleaseAllDrags();

            if (!hasHealth)
                continue;

            if (health.CurrentHp == 0)
            {
                if (target.TryGetComponent(out NetworkObject victim))
                    m_deathBuffer.Add(victim);
            }
            else
            {
                ServerLaunchSurvivor(health, EvaluateRagdollImpulse(target.position));
            }
        }

        NotifyBlastDeaths();

        ServerBlastNpcs();

        Debug.Log(
            $"[폭탄] 폭발 (반경 {m_profile.Radius}m, 폭심 피해 {m_profile.PeakDamage},"
                + $" 가장자리 비율 {m_profile.DamageEdgeFalloff}, 사망 {m_deathBuffer.Count}명)"
        );
    }

    /// <summary>폭발 사망자와 서버가 계산한 임펄스를 전 피어에 알린다.</summary>
    private void NotifyBlastDeaths()
    {
        if (m_deathBuffer.Count == 0)
            return;

        Vector3[] impulses = new Vector3[m_deathBuffer.Count];
        for (int i = 0; i < impulses.Length; i++)
            impulses[i] = EvaluateRagdollImpulse(m_deathBuffer[i].transform.position);

        if (!IsSpawned || !IsServer)
        {
            for (int i = 0; i < m_deathBuffer.Count; i++)
                ApplyBlastRagdoll(m_deathBuffer[i], impulses[i]);
            return;
        }

        NetworkObjectReference[] victims = new NetworkObjectReference[m_deathBuffer.Count];
        ulong[] authorities = new ulong[m_deathBuffer.Count];
        ulong serverId = NetworkManager.ServerClientId;

        for (int i = 0; i < victims.Length; i++)
        {
            victims[i] = m_deathBuffer[i];

            PlayerIncapacitation incap = m_deathBuffer[i].GetComponent<PlayerIncapacitation>();
            authorities[i] =
                incap != null && incap.IsOwnershipHandoverPending
                    ? incap.BodyOwnerClientId
                    : m_deathBuffer[i].OwnerClientId;

            ApplyBlastRagdoll(
                m_deathBuffer[i],
                authorities[i] == serverId ? impulses[i] : Vector3.zero
            );
        }

        BlastDeathsClientRpc(victims, impulses, authorities);
    }

    [ClientRpc]
    private void BlastDeathsClientRpc(
        NetworkObjectReference[] victims,
        Vector3[] impulses,
        ulong[] authorities
    )
    {
        if (IsServer)
            return;

        NetworkManager nm = NetworkManager.Singleton;

        int count = Mathf.Min(victims.Length, impulses.Length);
        count = Mathf.Min(count, authorities.Length);
        for (int i = 0; i < count; i++)
        {
            if (!victims[i].TryGet(out NetworkObject victim))
                continue;

            bool mine = nm != null && nm.LocalClientId == authorities[i];
            ApplyBlastRagdoll(victim, mine ? impulses[i] : Vector3.zero);
        }
    }

    private void ApplyBlastRagdoll(NetworkObject victim, Vector3 impulse)
    {
        if (victim == null || !victim.TryGetComponent(out PlayerRagdoll ragdoll))
            return;

        ragdoll.EnterRagdoll(impulse);
    }

    /// <summary>살아남은 플레이어를 Launched 상태로 두고 임펄스를 오너에게 보내 래그돌로 날린다.</summary>
    private void ServerLaunchSurvivor(PlayerHealth player, Vector3 impulse)
    {
        if (impulse == Vector3.zero)
            return;

        PlayerIncapacitation incap = player.GetComponent<PlayerIncapacitation>();
        incap?.ServerLaunch(m_launchMaxSeconds);

        if (!IsSpawned)
        {
            player.GetComponent<PlayerRagdoll>()?.EnterRagdoll(impulse);
            return;
        }

        LaunchSurvivorRpc(impulse, RpcTarget.Single(player.OwnerClientId, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void LaunchSurvivorRpc(Vector3 impulse, RpcParams rpcParams)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.LocalClient.PlayerObject == null)
            return;

        nm.LocalClient.PlayerObject.GetComponent<PlayerRagdoll>()?.EnterRagdoll(impulse);
    }

    private void ServerBlastNpcs()
    {
        Vector3 origin = transform.position;
        int hitCount = Physics.OverlapSphereNonAlloc(origin, m_profile.Radius, s_blastColliders);

        if (hitCount == s_blastColliders.Length)
            Debug.LogWarning($"[폭탄] 대상 버퍼({s_blastColliders.Length}) 포화 — 일부 NPC가 누락됐을 수 있다", this);

        s_blastNpcs.Clear();
        for (int i = 0; i < hitCount; i++)
        {
            NpcController npc = s_blastColliders[i].GetComponentInParent<NpcController>();
            if (npc == null || !s_blastNpcs.Add(npc))
                continue;

            Vector3 position = npc.transform.position;
            if (IsOccluded(position))
                continue;

            int damage = EvaluateDamage(position);
            if (damage <= 0)
                continue;

            bool wasAlive = !npc.Death.IsDead;

            npc.Health.TakeEnvironmentalDamage(damage, gameObject);

            if (npc.Death.IsDead)
            {
                if (wasAlive && npc.Ragdoll != null)
                    npc.Ragdoll.EnterRagdoll(EvaluateRagdollImpulse(position));
            }
            else
            {
                npc.Knockback.ServerLaunchRagdoll(
                    EvaluateRagdollImpulse(position),
                    m_launchStunSeconds,
                    threat: null
                );
            }
        }
    }
}
