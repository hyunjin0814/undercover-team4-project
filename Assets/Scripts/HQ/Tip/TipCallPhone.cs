using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 본부 제보 전화 — 간헐적으로 울리고, 벨이 끝나면(또는 받으면) 예비 용의자 1명을 수배 리스트에 공개한다.
/// 타이머·승격은 서버 권위이며 울림 여부만 동기화한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class TipCallPhone : NetworkBehaviour, IInteractable
{
    private CriminalAssigner Assigner => App.Game.CriminalAssigner;
    private RoundManager Round => App.Game.Round;

    [Header("수신 간격 (#102)")]
    [Tooltip("다음 수신까지 최소 대기(초)")]
    [Min(0f)]
    [SerializeField] private float m_minInterval = 40f;

    [Tooltip("다음 수신까지 최대 대기(초)")]
    [Min(0f)]
    [SerializeField] private float m_maxInterval = 90f;

    [Tooltip("라운드 시작 후 첫 전화까지 유예(초)")]
    [Min(0f)]
    [SerializeField] private float m_startDelay = 20f;

    [Header("수신 횟수 (#102)")]
    [Tooltip("한 라운드 전화 횟수 하한")]
    [Min(0)]
    [SerializeField] private int m_minCallCount = 2;

    [Tooltip("한 라운드 전화 횟수 상한. CriminalAssigner의 예비 풀 크기가 이보다 작으면 풀이 먼저 마른다")]
    [Min(0)]
    [SerializeField] private int m_maxCallCount = 4;

    [Header("울림")]
    [Tooltip("벨이 울리다 자동으로 받히기까지의 시간(초) — 상호작용으로 그 전에 받을 수도 있다")]
    [Min(1f)]
    [SerializeField] private float m_ringDuration = 3f;

    [Tooltip("외부 요청 벨(#485)이 끝난 뒤 제보 전화까지 비워 두는 간격(초). 짧게 유지 — 늘리면 라운드 막판 제보 전화가 시간에 밀려 못 울릴 수 있다")]
    [Min(0f)]
    [SerializeField] private float m_externalRingGrace = 8f;

    private readonly NetworkVariable<bool> m_isRingingSynced = new(false);

    private bool m_isRinging;

    private float m_nextRingTime;
    private float m_ringEndTime;
    private bool m_scheduling;
    private int m_remainingCalls;
    private RoundPhase m_lastPhase = RoundPhase.Preparing;
    private bool m_warnedNoRound;

    public bool IsRinging => IsSpawned && !IsServer ? m_isRingingSynced.Value : m_isRinging;

    public event Action OnRingingChanged;

    private bool IsAuthority => !IsSpawned || IsServer;

    private Action<ulong> m_externalAnswered;

    /// <summary>외부 요청으로 벨을 울리고 받은 클라이언트 id를 onAnswered로 전달한다. 서버(또는 오프라인) 전용.</summary>
    public bool TryRingExternal(Action<ulong> onAnswered)
    {
        if (!IsAuthority || onAnswered == null) return false;
        if (m_isRinging) return false;
        if (Round == null || Round.Phase != RoundPhase.InProgress) return false;

        m_externalAnswered = onAnswered;
        SetRinging(true);
        m_ringEndTime = Time.time + m_ringDuration;
        return true;
    }

    public override void OnNetworkSpawn()
    {
        m_isRingingSynced.OnValueChanged += HandleRingingSyncedChanged;
    }

    public override void OnNetworkDespawn()
    {
        m_isRingingSynced.OnValueChanged -= HandleRingingSyncedChanged;
    }

    private void HandleRingingSyncedChanged(bool previous, bool current) => OnRingingChanged?.Invoke();

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor)) return;

        if (!IsSpawned)
        {
            Answer(NetworkManager.ServerClientId);
            return;
        }

        RequestAnswerRpc();
    }

    /// <summary>울리는 중에만 받을 수 있다 — 조준 윤곽선도 그때만 켜진다.</summary>
    public bool CanInteract(GameObject interactor) => IsRinging;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.TipCall;

    [Rpc(SendTo.Server)]
    private void RequestAnswerRpc(RpcParams rpcParams = default)
    {
        if (!m_isRinging) return;

        Answer(rpcParams.Receive.SenderClientId);
    }

    private void Update()
    {
        if (!IsAuthority)
            return;

        if (Round == null)
        {
            if (!m_warnedNoRound)
            {
                m_warnedNoRound = true;
                Debug.LogWarning("TipCallPhone: RoundManager를 찾지 못해 수신 타이머가 돌지 않는다", this);
            }
            return;
        }

        RoundPhase phase = Round.Phase;
        if (phase != m_lastPhase)
        {
            HandlePhaseChanged(phase);
            m_lastPhase = phase;
        }

        if (m_isRinging)
        {
            if (Time.time >= m_ringEndTime)
                HandleRingTimeout();
            return;
        }

        if (!m_scheduling)
            return;

        if (Time.time < m_nextRingTime)
            return;

        if (m_remainingCalls <= 0)
        {
            m_scheduling = false;
            Debug.Log("[제보 전화] 이번 라운드 수신 횟수를 모두 소진해 더 이상 전화가 오지 않는다");
            return;
        }

        if (Assigner == null || !Assigner.HasPendingSuspect)
        {
            m_scheduling = false;
            Debug.Log("[제보 전화] 예비 용의자가 모두 공개되어 더 이상 전화가 오지 않는다");
            return;
        }

        m_remainingCalls--;
        SetRinging(true);
        m_ringEndTime = Time.time + m_ringDuration;
        Debug.Log($"[제보 전화] 수신 — {m_ringDuration:0}초 안에 받아야 한다 (남은 수신 {m_remainingCalls}회)");
    }

    private void HandleRingTimeout()
    {
        bool wasExternal = m_externalAnswered != null;

        if (wasExternal)
        {
            m_externalAnswered = null;
            SetRinging(false);
            DelayNextRing();
            Debug.Log("[전화] 외부 요청 벨을 받지 않아 끊겼다 — 제보 전화 횟수는 소모되지 않았다");
            return;
        }

        Answer(NetworkManager.ServerClientId);
        Debug.Log("[제보 전화] 자동 응답 — 수배 목록이 갱신됐다");
    }

    private void HandlePhaseChanged(RoundPhase phase)
    {
        if (phase == RoundPhase.InProgress)
        {
            m_scheduling = true;
            int maxCalls = Mathf.Max(m_minCallCount, m_maxCallCount);
            m_remainingCalls = UnityEngine.Random.Range(m_minCallCount, maxCalls + 1);
            ScheduleNext(m_startDelay);
            Debug.Log($"[제보 전화] 이번 라운드 수신 횟수: {m_remainingCalls}회");
        }
        else
        {
            m_scheduling = false;
            m_externalAnswered = null;
            SetRinging(false);
        }
    }

    private void Answer(ulong answeredBy)
    {
        SetRinging(false);

        Action<ulong> external = m_externalAnswered;
        m_externalAnswered = null;
        if (external != null)
        {
            external(answeredBy);
            DelayNextRing();
            return;
        }

        if (Assigner == null)
        {
            Debug.LogWarning("TipCallPhone: CriminalAssigner를 찾지 못해 수배를 공개할 수 없다", this);
        }
        else if (Assigner.PromoteNext())
        {
        }
        else
        {
            Debug.Log("[제보 전화] 받았지만 지금 공개할 수 있는 용의자가 없다 — 다음 전화를 기다린다");
        }

        ScheduleNext();
    }

    private void ScheduleNext(float extraDelay = 0f)
    {
        m_nextRingTime = Time.time + extraDelay + UnityEngine.Random.Range(m_minInterval, m_maxInterval);
        Debug.Log($"[제보 전화] 다음 수신 예약 — {m_nextRingTime - Time.time:0}초 뒤");
    }

    private void DelayNextRing()
    {
        m_nextRingTime = Mathf.Max(m_nextRingTime, Time.time + m_externalRingGrace);
    }

    private void SetRinging(bool value)
    {
        if (m_isRinging == value)
            return;

        m_isRinging = value;

        if (IsSpawned && IsServer)
            m_isRingingSynced.Value = value;

        OnRingingChanged?.Invoke();
    }
}
