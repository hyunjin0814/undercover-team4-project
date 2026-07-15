using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 돌발 이벤트 프레임워크 — 라운드 진행 중 불규칙하게 <see cref="ISuddenEvent"/>를 서버 권위로 발생시킨다. (GDD 6-4/7-4, #106)
/// 수사와 무관하게 세계관(치안 붕괴)을 반영하는 이벤트(거리 난동자·괴한 습격·전자기기 먹통 등)를 관리한다.
///
/// 서버 권위(#56 패턴):
///  · 발생 타이밍·판정은 서버(또는 오프라인)에서만 돈다 — <see cref="RoundManager.Phase"/>가 InProgress가 되는 곳이 서버뿐이라
///    클라이언트에서는 스케줄러가 아예 돌지 않는다.
///  · 네트워크가 없는 로컬 Play 테스트에서는 스폰 없이 단독으로 동작한다.
///
/// <b>이 매니저는 어떤 이벤트가 있는지 모른다</b> — <see cref="ISuddenEvent"/> 뒤에서 "언제 발생시킬지"만 정하고
/// 발생·틱·정리를 호출할 뿐이다. 이벤트의 효과·상태·전 클라 전파는 전부 각 구현체가 스스로 소유한다
/// (스폰형은 자기 NetworkObject, 전역형은 자기 NetworkVariable). 이벤트를 늘리거나 지워도 이 파일은 그대로다.
///
/// 같은 GameObject에서 이벤트 풀을 자동 수집한다 — 두 경로가 있다:
///  · <see cref="ISuddenEvent"/> 컴포넌트 — 1개 = 1종 (세상에 하나뿐인 이벤트: 괴한 습격·전자기기 먹통).
///  · <see cref="ISuddenEventProvider"/> 컴포넌트 — 1개가 여러 종을 품는다 (스폰형: <see cref="SpawnedNpcEventSet"/>의 리스트 항목들).
/// 어느 쪽이든 매니저가 보는 것은 평평한 <see cref="ISuddenEvent"/> 풀 하나뿐이라 추첨 단위는 이벤트 1종이다.
/// — 각 이벤트가 <c>[RequireComponent(typeof(SuddenEventManager))]</c>로 매니저와 같은 오브젝트를 강제하므로,
///   이벤트는 반드시 이 매니저와 같은 오브젝트에 둔다(자식에 두면 그 자식에 두 번째 매니저가 자동 생성됨).
/// 발생 빈도·이벤트별 수치는 전부 인스펙터 — 밸런싱 보류 항목이라 코드에 못 박지 않는다 (GDD 12장).
/// </summary>
// TODO: 이벤트 발생/종료 HUD 알림(본부 관제 UI, #43 계열)은 OnEventAnnounced/AnnounceEventClientRpc를 구독해 연결한다.
[RequireComponent(typeof(NetworkObject))]
public class SuddenEventManager : NetworkBehaviour
{
    [Header("라운드 매니저 (비우면 씬에서 자동 탐색)")]
    [SerializeField]
    private RoundManager m_round;

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

    // 같은 오브젝트에서 자동 수집한 이벤트 풀 (Awake에서 1회)
    private readonly List<ISuddenEvent> m_events = new List<ISuddenEvent>();

    // 발생 후보 임시 버퍼 — 매 추첨마다의 할당을 피한다 (서버/오프라인에서만 쓰므로 공유 안전)
    private readonly List<ISuddenEvent> m_eligibleBuffer = new List<ISuddenEvent>();

    private float m_nextTriggerTime;
    private bool m_scheduling; // 라운드 InProgress 진입 시 켜진다 — Phase 폴링으로 스케줄 시작/정지를 판정
    private RoundPhase m_lastPhase = RoundPhase.Preparing;

    /// <summary>이벤트 발생 알림 — 본부/현장 HUD 토스트(#43)가 구독할 훅.</summary>
    public event Action<string> OnEventAnnounced;

    // 서버(또는 오프라인)에서만 의미 — 이 피어가 이벤트 권위를 가지는지. 스폰 전(오프라인)이면 항상 권위.
    private bool IsAuthority => !IsSpawned || IsServer;

    private void Awake()
    {
        if (m_round == null)
            m_round = FindFirstObjectByType<RoundManager>();

        // 같은 오브젝트의 이벤트 핸들러를 풀로 수집 — RequireComponent가 이벤트를 같은 오브젝트에 강제하므로
        // 자식까지 훑지 않는다(그래야 자식 배치 오용 시 조용히 수집되는 대신 리그가 잘못됐음이 드러난다)
        GetComponents(m_events); // 컴포넌트형: 1개 = 1종 (괴한 습격·전자기기 먹통)

        // 제공자형: 컴포넌트 1개가 여러 종을 품는다 (스폰형 = SpawnedNpcEventSet의 리스트 항목들).
        // GetComponents가 풀을 비우므로 반드시 그 뒤에 얹는다. (#106)
        List<ISuddenEventProvider> providers = new List<ISuddenEventProvider>();
        GetComponents(providers);
        for (int i = 0; i < providers.Count; i++)
            providers[i].CollectEvents(m_events);
    }

    private void Update()
    {
        // 발생 스케줄·판정은 서버 권위 — 클라이언트에서는 아예 돌지 않는다 (#56)
        if (!IsAuthority)
            return;
        if (m_round == null)
            return;

        // 라운드 페이즈 전이 감지 — InProgress 진입 시 스케줄 시작, 이탈 시 진행 이벤트를 정리하고 멈춘다
        RoundPhase phase = m_round.Phase;
        if (phase != m_lastPhase)
        {
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
            ScheduleNext(m_startDelay); // 시작 직후 유예를 두고 첫 이벤트를 잡는다
        }
        else
        {
            // 라운드가 끝났거나 준비 상태로 되돌아감 — 진행 중이던 이벤트를 강제 정리하고 스케줄을 멈춘다
            m_scheduling = false;
            ResetAllEvents();
        }
    }

    // 활성 이벤트를 매 프레임 서버에서 진행시킨다 — 각자 지속/자동 해제를 관리한다
    private void TickActiveEvents()
    {
        for (int i = 0; i < m_events.Count; i++)
        {
            if (m_events[i].IsActive)
                m_events[i].ServerTick();
        }
    }

    // 예약 시각에 도달하면 발생 가능한 이벤트 중 하나를 무작위로 골라 시작하고, 다음 예약을 잡는다
    private void TrySchedule()
    {
        if (Time.time < m_nextTriggerTime)
            return;

        if (m_enabled)
            TryTriggerRandom();

        ScheduleNext(0f); // 발생 성공 여부와 무관하게 다음 추첨 시각을 새로 잡는다 (막혔으면 다음 기회에 재시도)
    }

    private void TryTriggerRandom()
    {
        m_eligibleBuffer.Clear();
        for (int i = 0; i < m_events.Count; i++)
        {
            ISuddenEvent evt = m_events[i];
            if (!evt.IsActive && evt.CanTrigger())
                m_eligibleBuffer.Add(evt);
        }

        if (m_eligibleBuffer.Count == 0)
            return; // 지금은 발생 가능한 이벤트가 없다 — 다음 예약에서 다시 시도

        ISuddenEvent chosen = m_eligibleBuffer[Random.Range(0, m_eligibleBuffer.Count)];
        chosen.ServerBegin();
        Debug.Log($"[돌발이벤트] 발생 — {chosen.DisplayName}");
        AnnounceEvent(chosen.DisplayName);
    }

    // 다음 발생까지의 대기 시간을 min~max 사이에서 뽑아 예약한다 (extraDelay는 라운드 시작 유예용)
    private void ScheduleNext(float extraDelay)
    {
        m_nextTriggerTime = Time.time + extraDelay + Random.Range(m_minInterval, m_maxInterval);
    }

    // 각 이벤트가 자기 효과를 스스로 되돌린다 — 매니저는 무엇을 정리해야 하는지 알 필요가 없다.
    private void ResetAllEvents()
    {
        for (int i = 0; i < m_events.Count; i++)
            m_events[i].ServerReset();
    }

    // 이벤트 발생을 전 클라이언트에 알린다 — HUD 알림(#43)용. 네트워크 세션에서만 RPC를 쏜다.
    private void AnnounceEvent(string displayName)
    {
        OnEventAnnounced?.Invoke(displayName); // 서버·오프라인 로컬 발행
        if (IsSpawned && IsServer)
            AnnounceEventClientRpc(displayName);
    }

    [ClientRpc]
    private void AnnounceEventClientRpc(string displayName)
    {
        // 서버(호스트)는 위에서 이미 발행했으므로 원격 클라에서만 중계
        if (IsServer)
            return;
        Debug.Log($"[돌발이벤트] 발생 알림 — {displayName}");
        OnEventAnnounced?.Invoke(displayName);
    }
}
