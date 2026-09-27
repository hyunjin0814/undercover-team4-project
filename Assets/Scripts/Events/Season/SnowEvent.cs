using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 눈 라운드 날씨 — 눈을 내리고, 오래 내리면 빙판 비율(IceRatio)이 쌓였다가 그친 뒤 서서히 녹는다.
/// 빙판 비율은 서버가 정해 동기화하며, 누적은 라운드 진행 중에만 된다.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class SnowEvent : NetworkBehaviour, IRoundWeather
{
    private const float k_ratioEpsilon = 0.02f;

    [Header("빙판 (초안 — 팀 검토 필요)")]
    [Tooltip("눈이 이만큼(초) 누적되면 빙판이 생기기 시작한다 — 이 전에는 미끄럽지 않다")]
    [Min(0f)]
    [SerializeField] private float m_iceOnsetSeconds = 20f;

    [Tooltip("누적이 이만큼(초)이면 빙판이 상한(아래 Ice Max Ratio)에 닿는다")]
    [Min(1f)]
    [SerializeField] private float m_iceFullSeconds = 50f;

    [Tooltip("빙판이 도달할 수 있는 최대치 — 1이면 완전 빙판. 눈이 라운드 내내 내리므로(#700) 여기서 민다")]
    [Range(0f, 1f)]
    [SerializeField] private float m_iceMaxRatio = 0.6f;

    [Tooltip("눈이 그친 뒤 빙판이 완전히 녹는 데 걸리는 시간(초) — 그쳐도 길은 한동안 미끄럽다")]
    [Min(1f)]
    [SerializeField] private float m_thawSeconds = 45f;

    [Header("실내 차단")]
    [Tooltip(
        "머리 위로 이 거리(m) 안에 지붕이 있으면 그 자리에는 빙판이 없다 — 0이면 실내에서도 미끄럽다.\n\n"
            + "눈 표현(SnowView)의 같은 이름 값과 맞춰 둘 것. 건물 높이보다 넉넉히"
    )]
    [Min(0f)]
    [SerializeField] private float m_shelterProbeHeight = 25f;

    [Tooltip("하늘을 막는 것으로 칠 레이어 — 건물은 Default다")]
    [SerializeField] private LayerMask m_shelterMask = 1;

    private readonly NetworkVariable<bool> m_snowSynced = new NetworkVariable<bool>(false);
    private bool m_snow;

    private readonly NetworkVariable<float> m_iceRatioSynced = new NetworkVariable<float>(0f);
    private float m_snowSeconds;
    private float m_iceRatioLocal;

    public string DisplayName => "눈";

    public WeatherKind Kind => WeatherKind.Snow;

    public bool IsActive => m_snow;

    public bool AnnounceOnBegin => false;

    public bool IsSnow => (!IsSpawned || IsServer) ? m_snow : m_snowSynced.Value;

    public float IceRatio => (!IsSpawned || IsServer) ? m_iceRatioLocal : m_iceRatioSynced.Value;

    /// <summary>그 위치의 빙판 정도를 돌려준다. 지붕 아래면 0이다.</summary>
    public float IceRatioAt(Vector3 position)
    {
        float ratio = IceRatio;
        if (ratio <= 0f)
            return 0f;

        return WeatherShelter.IsSheltered(position, m_shelterMask, m_shelterProbeHeight) ? 0f : ratio;
    }

    public event Action<bool> OnSnowChanged;

    public event Action<float> OnIceRatioChanged;

    public override void OnNetworkSpawn()
    {
        m_snowSynced.OnValueChanged += HandleSnowSynced;
        m_iceRatioSynced.OnValueChanged += HandleIceSynced;

        if (m_snowSynced.Value)
            OnSnowChanged?.Invoke(true);
        if (m_iceRatioSynced.Value > 0f)
            OnIceRatioChanged?.Invoke(m_iceRatioSynced.Value);
    }

    public override void OnNetworkDespawn()
    {
        m_snowSynced.OnValueChanged -= HandleSnowSynced;
        m_iceRatioSynced.OnValueChanged -= HandleIceSynced;
    }

    private void HandleSnowSynced(bool previous, bool current) => OnSnowChanged?.Invoke(current);

    private void HandleIceSynced(float previous, float current) => OnIceRatioChanged?.Invoke(current);

    private void Update()
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_snow)
            return;

        ServerTickThaw();
    }

    public bool CanTrigger() => true;

    public void ServerBegin() => SetSnow(true);

    public void ServerTick()
    {
        m_snowSeconds = Mathf.Min(m_snowSeconds + Time.deltaTime, m_iceFullSeconds);
        RefreshIceRatio();
    }

    public void ServerReset()
    {
        SetSnow(false);

        m_snowSeconds = 0f;
        RefreshIceRatio();
    }

    private void ServerTickThaw()
    {
        if (m_snowSeconds <= 0f)
            return;

        m_snowSeconds = Mathf.Max(0f, m_snowSeconds - Time.deltaTime * (m_iceFullSeconds / m_thawSeconds));
        RefreshIceRatio();
    }

    private void RefreshIceRatio()
    {
        float ratio = m_iceFullSeconds > m_iceOnsetSeconds
            ? Mathf.Clamp01((m_snowSeconds - m_iceOnsetSeconds) / (m_iceFullSeconds - m_iceOnsetSeconds))
            : (m_snowSeconds >= m_iceOnsetSeconds ? 1f : 0f);

        ratio = Mathf.Min(ratio, m_iceMaxRatio);

        bool boundary =
            (ratio <= 0f || ratio >= m_iceMaxRatio) && !Mathf.Approximately(ratio, m_iceRatioLocal);
        if (!boundary && Mathf.Abs(ratio - m_iceRatioLocal) < k_ratioEpsilon)
            return;

        m_iceRatioLocal = ratio;
        if (IsSpawned && IsServer)
            m_iceRatioSynced.Value = ratio;
        else if (!IsSpawned)
            OnIceRatioChanged?.Invoke(ratio);
    }

    private void SetSnow(bool value)
    {
        if (m_snow == value)
            return;

        m_snow = value;
        if (IsSpawned && IsServer)
            m_snowSynced.Value = value;
        else if (!IsSpawned)
            OnSnowChanged?.Invoke(value);
    }
}
