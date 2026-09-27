using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// UFO 빔 흡입 판정(서버 권위) — 빔 안에 일정 시간 머문 현장 플레이어를 기체로 빨아올려 라운드에서 제외한다.
/// 라운드 진행 중에만 판정하며, 끌어올리는 이동은 오너가 앵커를 따라가는 방식이다.
/// </summary>
[RequireComponent(typeof(UfoCraft))]
public class UfoAbductor : MonoBehaviour
{
    [Header("빔 판정")]
    [Tooltip("빔 안에 이만큼(초) 머무르면 빨려 올라간다")]
    [Min(0.1f)]
    [SerializeField] private float m_captureSeconds = 1.5f;

    [Tooltip("빔에서 벗어났을 때 누적이 식는 속도 배율 — 1이면 머문 만큼 그대로 되돌아간다. 클수록 도망이 쉽다")]
    [Min(0f)]
    [SerializeField] private float m_dwellDecayScale = 1.5f;

    [Header("흡입")]
    [Tooltip("빨려 올라가는 속도(m/s) — 오너의 추종 이동에 걸리는 상한이다")]
    [Min(0.5f)]
    [SerializeField] private float m_liftSpeed = 4f;

    [Tooltip("기체까지 이 거리(m) 안으로 들어오면 흡입 완료로 본다")]
    [Min(0.5f)]
    [SerializeField] private float m_swallowDistance = 2.5f;

    [Tooltip("빨려 올라가다 이 시간(초)이 지나도 못 닿으면 완료로 친다 — 앵커를 놓쳐 영영 떠 있지 않게")]
    [Min(1f)]
    [SerializeField] private float m_liftTimeoutSeconds = 15f;

    private const float k_shelterProbeRadius = 0.3f;

    private const float k_beamFootSlack = 1.5f;

    private UfoCraft m_craft;
    private NetworkObject m_anchor;
    private Transform m_victim;
    private float m_liftDeadline;

    private readonly Dictionary<Transform, float> m_dwell = new Dictionary<Transform, float>();
    private readonly List<Transform> m_inBeam = new List<Transform>();
    private readonly List<Transform> m_cooled = new List<Transform>();

    private RoundManager Round => App.Game.Round;

    private static bool HasServerAuthority =>
        NetworkManager.Singleton == null
        || !NetworkManager.Singleton.IsListening
        || NetworkManager.Singleton.IsServer;

    private void Awake()
    {
        m_craft = GetComponent<UfoCraft>();
        m_anchor = GetComponent<NetworkObject>();
    }

    private void Update()
    {
        if (!HasServerAuthority)
            return;

        if (Round != null && Round.Phase != RoundPhase.InProgress)
        {
            ReleaseVictim();
            m_dwell.Clear();
            m_craft.ServerSetHold(false);
            return;
        }

        Transform caught = TickDwell(Time.deltaTime);

        if (m_victim != null)
            TickLifting();
        else if (caught != null)
            BeginLifting(caught);

        m_craft.ServerSetHold(m_victim != null);
    }

    private void OnDisable()
    {
        if (HasServerAuthority)
            ReleaseVictim();
    }

    /// <summary>빔 안 체류 시간을 갱신(벗어나면 식힘)하고 임계를 넘은 사람을 돌려준다. 없으면 null.</summary>
    private Transform TickDwell(float deltaTime)
    {
        Vector3 ground = m_craft.BeamGroundPoint();
        float span = transform.position.y - ground.y + m_craft.BeamRadius + k_beamFootSlack;
        SuddenEventUtil.CollectDamageablePlayers(transform.position, span, m_inBeam);

        for (int i = m_inBeam.Count - 1; i >= 0; i--)
        {
            if (!IsInsideBeamColumn(m_inBeam[i].position, ground) || IsShelteredFromBeam(m_inBeam[i]))
                m_inBeam.RemoveAt(i);
        }

        Transform caught = null;
        for (int i = 0; i < m_inBeam.Count; i++)
        {
            Transform player = m_inBeam[i];
            m_dwell.TryGetValue(player, out float held);
            held += deltaTime;
            m_dwell[player] = held;

            if (held >= m_captureSeconds && caught == null)
                caught = player;
        }

        m_cooled.Clear();
        foreach (KeyValuePair<Transform, float> entry in m_dwell)
        {
            if (entry.Key == null || !m_inBeam.Contains(entry.Key))
                m_cooled.Add(entry.Key);
        }

        for (int i = 0; i < m_cooled.Count; i++)
        {
            Transform player = m_cooled[i];
            if (player == null)
            {
                m_dwell.Remove(player);
                continue;
            }

            float cooled = m_dwell[player] - deltaTime * m_dwellDecayScale;
            if (cooled <= 0f)
                m_dwell.Remove(player);
            else
                m_dwell[player] = cooled;
        }

        return caught;
    }

    private bool IsInsideBeamColumn(Vector3 position, Vector3 groundPoint)
    {
        Vector3 axis = transform.position;

        float dx = position.x - axis.x;
        float dz = position.z - axis.z;
        if (dx * dx + dz * dz > m_craft.BeamRadius * m_craft.BeamRadius)
            return false;

        return position.y >= groundPoint.y - k_beamFootSlack && position.y <= axis.y;
    }

    private bool IsShelteredFromBeam(Transform player)
    {
        Vector3 body = player.position + Vector3.up * WeatherShelter.k_bodyProbeHeight;
        float toCraft = transform.position.y - body.y;

        return toCraft > 0f
            && WeatherShelter.IsSheltered(body, m_craft.GroundMask, toCraft, k_shelterProbeRadius);
    }

    private void BeginLifting(Transform victim)
    {
        PlayerIncapacitation incap = victim.GetComponent<PlayerIncapacitation>();

        if (incap != null && incap.IsIncapacitated && !incap.IsLaunched)
        {
            m_dwell.Remove(victim);
            return;
        }

        m_victim = victim;
        m_liftDeadline = Time.time + m_liftTimeoutSeconds;
        m_dwell.Remove(victim);

        if (incap != null)
            incap.Incapacitate(IncapacitationCause.Beamed);

        victim.GetComponent<PlayerEscortCommands>()?.ServerUnropeEverything();

        PlayerPenaltyView view = victim.GetComponent<PlayerPenaltyView>();
        if (view != null && m_anchor != null)
            view.StartTowedBy(m_anchor, m_liftSpeed);

        Debug.Log($"[UFO] 흡입 시작 — {victim.name}");
    }

    private void TickLifting()
    {
        if (m_victim == null)
            return;

        PlayerIncapacitation incap = m_victim.GetComponent<PlayerIncapacitation>();
        if (incap != null && incap.Cause != IncapacitationCause.Beamed)
        {
            Debug.Log($"[UFO] 흡입 중단 — {m_victim.name}이 흡입 밖의 사유로 쓰러졌다 ({incap.Cause})");
            StopTow(m_victim);
            m_victim = null;
            return;
        }

        bool swallowed =
            (m_victim.position - transform.position).sqrMagnitude <= m_swallowDistance * m_swallowDistance;

        if (swallowed)
        {
            Swallow(m_victim);
            m_victim = null;
            return;
        }

        if (Time.time < m_liftDeadline)
            return;

        Debug.Log($"[UFO] 흡입 타임아웃 — {m_victim.name}에 닿지 못해 풀어준다");
        ReleaseVictim();
    }

    /// <summary>흡입을 완료하고 대상의 라운드 아웃(몸 회수 불가)을 확정한다.</summary>
    private void Swallow(Transform victim)
    {
        PlayerPenaltyView view = victim.GetComponent<PlayerPenaltyView>();
        if (view != null)
            view.SetSpectatePivot(transform.position, cycleToTeammate: true);

        PlayerIncapacitation incap = victim.GetComponent<PlayerIncapacitation>();
        if (incap != null)
        {
            incap.ServerKillByBodyLost();

            PlayerHealth health = victim.GetComponent<PlayerHealth>();
            if (health != null && health.CurrentHp > 0)
                health.ModifyHp(-health.CurrentHp);
        }

        StopTow(victim);
        Debug.Log($"[UFO] 흡입 완료 — {victim.name}이 실려 갔다");
    }

    private void ReleaseVictim()
    {
        if (m_victim == null)
            return;

        Transform victim = m_victim;
        m_victim = null;

        StopTow(victim);

        PlayerIncapacitation incap = victim.GetComponent<PlayerIncapacitation>();
        if (incap != null && incap.Cause == IncapacitationCause.Beamed)
            incap.Recover();
    }

    private static void StopTow(Transform victim)
    {
        PlayerPenaltyView view = victim != null ? victim.GetComponent<PlayerPenaltyView>() : null;
        if (view != null)
            view.StopCarried();
    }
}
