using System;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 번개 라운드 날씨(GDD 6-4) — 진행 중 주기적으로 예고 후 낙뢰를 떨어뜨려 반경 내 실외 플레이어에게 피해 또는 속도 버프를 준다.
/// 서버 권위로 판정하고 표현은 LightningView가 맡는다.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class LightningEvent : NetworkBehaviour, IRoundWeather
{
    [Header("Strike Interval (Seconds)")]
    [SerializeField]
    private float m_strikeIntervalMin = 2f;

    [SerializeField]
    private float m_strikeIntervalMax = 5f;

    [Header("예고 낙하 (#647)")]
    [Tooltip("지점을 알린 뒤 벼락이 떨어지기까지의 시간(초) — 0이면 예고 없이 즉발이다")]
    [Min(0f)]
    [SerializeField]
    private float m_warningSeconds = 0.6f;

    [Tooltip(
        "낙뢰 지점에서 이 거리(m) 안에 있으면 맞는다 — 수평 거리다.\n\n"
            + "예고 시간 × 걷기 속도(5m/s)보다 작아야 움직여서 빠져나갈 수 있다"
    )]
    [Min(0.1f)]
    [SerializeField]
    private float m_strikeRadius = 2.5f;

    [Header("Strike Effects")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_damageChance = 0.5f;

    [SerializeField]
    private int m_damageAmount = 1;

    [SerializeField]
    private float m_buffMultiplier = 1.5f;

    [SerializeField]
    private float m_buffDuration = 5f;

    [Header("실내 차단")]
    [Tooltip(
        "머리 위로 이 거리(m) 안에 지붕이 있는 플레이어에게는 낙뢰가 떨어지지 않는다 — 0이면 실내에도 떨어진다.\n\n"
            + "건물 높이보다 넉넉히 잡을 것. 비·눈이 그치는 판정과 같은 규칙을 쓴다(WeatherShelter)"
    )]
    [Min(0f)]
    [SerializeField]
    private float m_shelterProbeHeight = 25f;

    [Tooltip("하늘을 막는 것으로 칠 레이어 — 건물은 Default다")]
    [SerializeField]
    private LayerMask m_shelterMask = 1;

    private NetworkVariable<bool> m_lightningSynced = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private bool m_lightning = false;
    private float m_nextStrikeTime;
    private bool m_strikeScheduled;

    private bool m_hasPendingStrike;
    private Vector3 m_pendingStrikePosition;
    private float m_pendingStrikeTime;

    public event Action<bool> OnLightningChanged;

    public event Action<Vector3> OnStrike;

    public event Action<Vector3> OnStrikeWarning;

    public string DisplayName => "번개";
    public bool IsActive => m_lightning;
    public WeatherKind Kind => WeatherKind.Lightning;

    public bool AnnounceOnBegin => false;

    public bool IsLightningActive =>
        (!IsSpawned || IsServer) ? m_lightning : m_lightningSynced.Value;

    public override void OnNetworkSpawn()
    {
        m_lightningSynced.OnValueChanged += OnSyncValueChanged;

        if (m_lightningSynced.Value)
        {
            OnLightningChanged?.Invoke(true);
        }
    }

    public override void OnNetworkDespawn()
    {
        m_lightningSynced.OnValueChanged -= OnSyncValueChanged;
    }

    private void OnSyncValueChanged(bool previousValue, bool newValue)
    {
        OnLightningChanged?.Invoke(newValue);
    }

    public bool CanTrigger() => true;

    public void ServerBegin()
    {
        SetLightning(true);

        m_strikeScheduled = false;
    }

    public void ServerTick()
    {
        if (!m_lightning)
            return;

        if (!m_strikeScheduled)
        {
            ScheduleNextStrike();
            m_strikeScheduled = true;
            return;
        }

        if (m_hasPendingStrike && Time.time >= m_pendingStrikeTime)
            ResolvePendingStrike();

        if (Time.time >= m_nextStrikeTime)
        {
            BeginStrikeWarning();
            ScheduleNextStrike();
        }
    }

    public void ServerReset()
    {
        SetLightning(false);
    }

    private void SetLightning(bool value)
    {
        if (m_lightning == value)
            return;

        m_lightning = value;

        if (!value)
            m_hasPendingStrike = false;

        if (IsServer)
        {
            m_lightningSynced.Value = value;
        }

        OnLightningChanged?.Invoke(value);
    }

    private void ScheduleNextStrike()
    {
        m_nextStrikeTime = Time.time + Random.Range(m_strikeIntervalMin, m_strikeIntervalMax);
    }

    private void BeginStrikeWarning()
    {
        if (!IsServer || m_hasPendingStrike)
            return;

        PlayerHealth aim = PickExposedPlayer();
        if (aim == null)
            return;

        m_pendingStrikePosition = aim.transform.position;
        m_pendingStrikeTime = Time.time + m_warningSeconds;
        m_hasPendingStrike = true;

        PlayStrikeWarningClientRpc(m_pendingStrikePosition);
    }

    private void ResolvePendingStrike()
    {
        m_hasPendingStrike = false;

        Vector3 strikePosition = m_pendingStrikePosition;

        bool isDamage = Random.value < m_damageChance;

        CollectExposedPlayers(strikePosition, m_strikeRadius);
        for (int i = 0; i < s_exposed.Count; i++)
        {
            if (isDamage)
                ApplyDamage(s_exposed[i]);
            else
                ApplySpeedBuff(s_exposed[i]);
        }

        Debug.Log(
            $"[LightningEvent] Strike {(isDamage ? "DAMAGE" : "BUFF")} at {strikePosition} — hit {s_exposed.Count}"
        );

        PlayStrikeVFXClientRpc(strikePosition);
    }

    private PlayerHealth PickExposedPlayer()
    {
        CollectExposedPlayers(Vector3.zero, -1f);
        return s_exposed.Count == 0 ? null : s_exposed[Random.Range(0, s_exposed.Count)];
    }

    private void CollectExposedPlayers(Vector3 center, float radius)
    {
        s_exposed.Clear();

        System.Collections.Generic.IReadOnlyList<PlayerHealth> players = PlayerHealth.All;

        float sqrRadius = radius * radius;
        for (int i = 0; i < players.Count; i++)
        {
            PlayerHealth player = players[i];
            if (player == null)
                continue;

            Vector3 position = player.transform.position;

            if (radius > 0f)
            {
                Vector2 flat = new Vector2(position.x - center.x, position.z - center.z);
                if (flat.sqrMagnitude > sqrRadius)
                    continue;
            }

            Vector3 origin = position + Vector3.up * WeatherShelter.k_bodyProbeHeight;
            if (WeatherShelter.IsSheltered(origin, m_shelterMask, m_shelterProbeHeight))
                continue;

            s_exposed.Add(player);
        }
    }

    private static readonly System.Collections.Generic.List<PlayerHealth> s_exposed =
        new System.Collections.Generic.List<PlayerHealth>();

    private void ApplyDamage(PlayerHealth healthComponent)
    {
        if (healthComponent == null)
            return;

        healthComponent.TakeDamage(m_damageAmount, null);
    }

    private void ApplySpeedBuff(PlayerHealth healthComponent)
    {
        PlayerMovement movement = healthComponent.GetComponent<PlayerMovement>();
        if (movement == null)
            return;

        movement.ServerApplySpeedBuff(m_buffMultiplier, m_buffDuration);
    }

    [ClientRpc]
    private void PlayStrikeWarningClientRpc(Vector3 position)
    {
        if (IsServer && !IsHost)
            return;

        OnStrikeWarning?.Invoke(position);
    }

    [ClientRpc]
    private void PlayStrikeVFXClientRpc(Vector3 position)
    {
        if (IsServer && !IsHost)
            return;

        OnStrike?.Invoke(position);
    }
}
