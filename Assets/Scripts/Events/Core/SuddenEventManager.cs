using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 돌발 이벤트 프레임워크 — 이벤트 풀을 서버 권위로 추첨·틱·정리한다(GDD 6-4).
/// 라운드 날씨는 준비 단계에 따로 뽑으며, 이벤트 컴포넌트는 반드시 이 오브젝트에 둔다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SuddenEventManager : NetworkedManagerBase
{
    private RoundManager Round => App.Game.Round;
    private RoundProgress Progress => App.Game.RoundProgress;

    [Header("발생 스케줄 (초)")]
    [Tooltip(
        "라운드 시작(또는 직전 이벤트 종료) 후 다음 이벤트까지 대기하는 최소/최대 시간 — 이 사이에서 랜덤"
    )]
    [SerializeField]
    private float m_minInterval = 20f;

    [SerializeField]
    private float m_maxInterval = 45f;

    [Tooltip("라운드 시작 직후 첫 이벤트까지의 추가 유예(초) — 준비 없이 곧바로 터지는 것 방지")]
    [SerializeField]
    private float m_startDelay = 15f;

    [Header("이벤트 발생 on/off")]
    [Tooltip(
        "끄면 스케줄러가 새 이벤트를 발생시키지 않는다 (디버그·튜토리얼용). 이미 진행 중인 이벤트는 계속된다"
    )]
    [SerializeField]
    private bool m_enabled = true;

    [System.Serializable]
    private class SuddenEventEntry
    {
        [Tooltip(
            "ISuddenEvent 또는 ISuddenEventProvider를 구현한 컴포넌트 (예: DeviceBlackoutEvent, StreetThugEvent, JailbreakEvent)"
        )]
        public MonoBehaviour component;

        [Tooltip("끄면 이 항목은 이벤트 풀에서 제외된다 — 특정 이벤트만 켜서 테스트할 때 쓴다")]
        public bool enabled = true;
    }

    [Header("이벤트 풀 (명시 리스트)")]
    [Tooltip(
        "발생 후보 이벤트를 여기 등록한다. 자동수집은 쓰지 않는다 — 항목의 enabled로 개별 토글 (#291)"
    )]
    [SerializeField]
    private List<SuddenEventEntry> m_eventEntries = new List<SuddenEventEntry>();

    [Header("라운드 날씨 (#700)")]
    [Tooltip(
        "라운드별 맑음 확률·날씨별 가중치 표. 이 맵의 표를 꽂는다 — 안 꽂으면 아래 폴백 확률 하나로 돈다"
    )]
    [SerializeField]
    private RoundWeatherTable m_weatherTable;

    [Tooltip("표를 안 꽂았을 때 쓸 맑음 확률(%) — 표 미장착 씬이 깨지지 않게 하는 값이다")]
    [Range(0, 100)]
    [SerializeField]
    private int m_clearPercentFallback = 50;

    private readonly List<ISuddenEvent> m_events = new List<ISuddenEvent>();

    private readonly List<ISuddenEvent> m_eligibleBuffer = new List<ISuddenEvent>();

    private readonly List<IRoundWeather> m_weatherBuffer = new List<IRoundWeather>();

    private float m_nextTriggerTime;
    private bool m_scheduling;
    private RoundPhase m_lastPhase = RoundPhase.Preparing;

    private bool m_phaseSeen;

    public event Action<string, string> OnEventAnnounced;

    private bool m_wasEverSpawned;

    public override void OnNetworkSpawn() => m_wasEverSpawned = true;

    private bool IsAuthority
    {
        get
        {
            if (IsSpawned)
                return IsServer;

            if (m_wasEverSpawned)
                return false;

            NetworkManager net = NetworkManager.Singleton;
            return net == null || !net.IsListening;
        }
    }

    protected override void Awake()
    {
        base.Awake();

        m_events.Clear();
        for (int i = 0; i < m_eventEntries.Count; i++)
        {
            SuddenEventEntry entry = m_eventEntries[i];
            if (!entry.enabled || entry.component == null)
                continue;

            if (entry.component is ISuddenEventProvider provider)
                provider.CollectEvents(m_events);
            else if (entry.component is ISuddenEvent evt)
                m_events.Add(evt);
            else
                Debug.LogWarning(
                    $"SuddenEventManager: '{entry.component.name}'은(는) ISuddenEvent/ISuddenEventProvider가 아니다 — 무시",
                    entry.component
                );
        }
    }

    /// <summary>풀에서 T 타입 이벤트를 찾아 돌려준다. 없으면 null.</summary>
    public T GetEvent<T>()
        where T : class, ISuddenEvent => m_events.Find(e => e is T) as T;

    public int EventCount => m_events.Count;

    /// <summary>index번 이벤트의 표시 이름을 돌려준다. 범위 밖이면 null(디버그용).</summary>
    public string EventNameAt(int index) =>
        index >= 0 && index < m_events.Count ? m_events[index].DisplayName : null;

    private void Update()
    {
        if (!IsAuthority)
            return;
        if (Round == null)
            return;

        RoundPhase phase = Round.Phase;
        if (!m_phaseSeen || phase != m_lastPhase)
        {
            m_phaseSeen = true;
            HandlePhaseChanged(phase);
            m_lastPhase = phase;
        }

        if (!m_scheduling)
            return;

        TickActiveEvents();
        TrySchedule();
    }

    private void HandlePhaseChanged(RoundPhase phase)
    {
        if (phase == RoundPhase.InProgress)
        {
            m_scheduling = true;
            ScheduleNext(m_startDelay);
            return;
        }

        m_scheduling = false;
        ResetAllEvents();

        if (phase == RoundPhase.Preparing)
            TryBeginRoundWeather();
    }

    private void TickActiveEvents()
    {
        for (int i = 0; i < m_events.Count; i++)
        {
            if (m_events[i].IsActive)
                m_events[i].ServerTick();
        }
    }

    private void TrySchedule()
    {
        if (Time.time < m_nextTriggerTime)
            return;

        if (m_enabled)
            TryTriggerRandom();

        ScheduleNext(0f);
    }

    private void TryTriggerRandom()
    {
        m_eligibleBuffer.Clear();
        for (int i = 0; i < m_events.Count; i++)
        {
            ISuddenEvent evt = m_events[i];

            if (evt is IRoundWeather)
                continue;

            if (!evt.IsActive && evt.CanTrigger())
                m_eligibleBuffer.Add(evt);
        }

        if (m_eligibleBuffer.Count == 0)
            return;

        ISuddenEvent chosen = m_eligibleBuffer[Random.Range(0, m_eligibleBuffer.Count)];
        chosen.ServerBegin();

        if (!chosen.IsActive)
        {
            Debug.Log($"[돌발이벤트] 발동 불발 — {chosen.DisplayName}");
            return;
        }

        Debug.Log($"[돌발이벤트] 발생 — {chosen.DisplayName}");

        if (chosen.AnnounceOnBegin)
            Announce(chosen.DisplayName, chosen.NoticeKey);
    }

    private void TryBeginRoundWeather()
    {
        if (!m_enabled)
            return;

        m_weatherBuffer.Clear();
        for (int i = 0; i < m_events.Count; i++)
        {
            if (m_events[i] is IRoundWeather weather && !weather.IsActive && weather.CanTrigger())
                m_weatherBuffer.Add(weather);
        }

        if (m_weatherBuffer.Count == 0)
            return;

        int round = Progress != null ? Progress.Current : RoundProgress.k_firstRound;
        int clearPercent =
            m_weatherTable != null
                ? m_weatherTable.GetClearPercent(round, m_clearPercentFallback)
                : m_clearPercentFallback;

        if (Random.Range(0, 100) < clearPercent)
        {
            Debug.Log($"[날씨] {round}라운드 — 맑음 (맑음 확률 {clearPercent}%)");
            return;
        }

        IRoundWeather chosen = PickWeighted();
        if (chosen == null)
        {
            Debug.Log($"[날씨] {round}라운드 — 후보 가중치가 전부 0이라 맑음으로 간다");
            return;
        }

        chosen.ServerBegin();

        if (!chosen.IsActive)
        {
            Debug.Log($"[날씨] 발동 불발 — {chosen.DisplayName}");
            return;
        }

        Debug.Log($"[날씨] {round}라운드 — {chosen.DisplayName} (맑음 확률 {clearPercent}%)");

        if (chosen.AnnounceOnBegin)
            Announce(chosen.DisplayName, chosen.NoticeKey);
    }

    private IRoundWeather PickWeighted()
    {
        float total = 0f;
        for (int i = 0; i < m_weatherBuffer.Count; i++)
            total += WeightOf(m_weatherBuffer[i]);

        if (total <= 0f)
            return null;

        float roll = Random.value * total;
        for (int i = 0; i < m_weatherBuffer.Count; i++)
        {
            roll -= WeightOf(m_weatherBuffer[i]);
            if (roll <= 0f)
                return m_weatherBuffer[i];
        }

        return m_weatherBuffer[m_weatherBuffer.Count - 1];
    }

    private float WeightOf(IRoundWeather weather) =>
        m_weatherTable != null ? m_weatherTable.WeightOf(weather.Kind) : 1f;

    /// <summary>[디버그] 풀의 index번 이벤트를 즉시 발동한다. 이미 활성이거나 발생 불가면 무시한다.</summary>
    public void ForceTrigger(int index)
    {
        if (!IsAuthority)
            return;
        if (index < 0 || index >= m_events.Count)
        {
            Debug.LogWarning(
                $"SuddenEventManager.ForceTrigger: 잘못된 index {index} (풀 크기 {m_events.Count})",
                this
            );
            return;
        }

        ISuddenEvent evt = m_events[index];
        if (evt.IsActive)
        {
            Debug.Log($"[돌발이벤트] 강제발동 불가 — {evt.DisplayName} (이미 진행 중)");
            return;
        }

        if (!evt.CanTrigger() && !evt.ServerPrepareForceTrigger())
        {
            Debug.Log($"[돌발이벤트] 강제발동 불가 — {evt.DisplayName} (조건 미충족)");
            return;
        }

        if (evt is IRoundWeather)
            ResetOtherWeather(evt);

        evt.ServerBegin();

        if (!evt.IsActive)
        {
            Debug.LogWarning($"[돌발이벤트] 강제발동했지만 시작되지 않았다 — {evt.DisplayName} (위 로그 참고)", this);
            return;
        }

        if (evt.AnnounceOnBegin)
            Announce(evt.DisplayName, evt.NoticeKey);
        Debug.Log($"[돌발이벤트] 강제발동 — {evt.DisplayName}");
    }

    private void ResetOtherWeather(ISuddenEvent keep)
    {
        for (int i = 0; i < m_events.Count; i++)
        {
            ISuddenEvent evt = m_events[i];
            if (evt == keep || !(evt is IRoundWeather) || !evt.IsActive)
                continue;

            evt.ServerReset();
            Debug.Log($"[날씨] 강제 교체로 걷힘 — {evt.DisplayName}");
        }
    }

    [ContextMenu("Debug/Force Trigger First Event")]
    private void ForceTriggerFirst() => ForceTrigger(0);

    private void ScheduleNext(float extraDelay)
    {
        m_nextTriggerTime = Time.time + extraDelay + Random.Range(m_minInterval, m_maxInterval);
    }

    private void ResetAllEvents()
    {
        for (int i = 0; i < m_events.Count; i++)
            m_events[i].ServerReset();
    }

    /// <summary>이벤트 알림을 전 클라이언트 HUD에 발행한다. 서버(또는 오프라인) 전용.</summary>
    public void Announce(string displayName, string noticeKey = null)
    {
        OnEventAnnounced?.Invoke(displayName, noticeKey);
        if (IsSpawned && IsServer)
            AnnounceEventClientRpc(displayName, noticeKey ?? string.Empty);
    }

    [ClientRpc]
    private void AnnounceEventClientRpc(string displayName, string noticeKey)
    {
        if (IsServer)
            return;
        Debug.Log($"[돌발이벤트] 발생 알림 — {displayName}");
        OnEventAnnounced?.Invoke(displayName, string.IsNullOrEmpty(noticeKey) ? null : noticeKey);
    }
}
