using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 무력화의 원인 — 무력화는 하나가 아니라 성질이 다른 넷이다. (#252, #364)
/// 행동을 막는 것은 같지만 <b>어떻게 풀리는가</b>가 갈리므로, 구조·전멸 판정·애니메이션이 이 값으로 분기한다.
/// </summary>
public enum IncapacitationCause
{
    None, // 무력화 아님
    Down, // HP 0 다운 (#105) — 동료가 구조해야 일어난다. 전멸(게임오버) 판정 대상
    Penalty, // 오검거 광장 매달기 (#101) — 30초 뒤 자동 복귀
    Stun, // 테이저 피격 기절 (#252) — 시간이 지나면 스스로 일어난다
    Die, // 다운 방치 → 기능 정지 (#364) — 현장 구조로도 못 일어난다. 본부 이송 부활(#365)만이 복구 경로
    // (값은 반드시 끝에 추가한다 — NetworkVariable로 동기화되는 enum이라 순서가 곧 와이어 포맷이다)
}

/// <summary>
/// 플레이어 행동불능(무력화) 공통 기반. (#105)
/// HP 0 다운(#105)·오검거 매달기(#101)·테이저 피격 기절(#252)·다운 방치 기능 정지(#364)는
/// 트리거만 다르고 결과=무력화로 같으므로, 무력화 상태 자체를 이 한 곳에서 서버 권위로 관리한다.
/// 이동·아이템·상호작용 컴포넌트가 <see cref="IsIncapacitated"/>를 읽어 각자 행동을 막는다.
///
/// 넷의 차이는 <b>풀리는 방식</b>이고, 그 구분이 <see cref="Cause"/>다:
///  · 다운 — 동료 구조(PlayerReviver)로만 일어난다. 전원 다운이면 전멸(게임오버).
///  · 매달기·기절 — 시간이 지나면 스스로 복귀하므로 구조 대상도, 전멸 판정 대상도 아니다.
///  · Die — 다운을 <see cref="m_dieAfterDownSeconds"/>초 방치하면 넘어간다(#364). 현장 구조가 막히고
///    본부 이송 부활(#365)만 남으므로, 구조 히트박스는 켜되(운반 조준용) 구조 채널링은 거부된다.
/// 조준 히트박스는 다운·Die에서 켠다.
/// </summary>
public class PlayerIncapacitation : NetworkBehaviour
{
    // 다운·Die 중에만 활성화되는 조준 히트박스(Interactable 레이어). 평소 비활성. (#105, #364)
    // 플레이어 몸(CharacterController)은 Default 레이어라 PlayerInteractor의 Interactable 마스크에 안 잡히므로,
    // 쓰러진 동안 이 트리거 콜라이더를 켜서 구조자(다운)·운반자(Die, #365)가 조준할 수 있게 한다.
    // 필드명은 구조 전용이던 시절 그대로다 — 프리팹 직렬화가 이름으로 묶여 있어 바꾸면 인스펙터 참조가 끊긴다.
    [SerializeField]
    private GameObject m_reviveHitbox;

    [Header("다운 방치 → Die (#364)")]
    [Tooltip(
        "다운 상태로 이 시간(초)이 지나면 Die(기능 정지)로 전환된다 — 동료가 구조할 수 있는 제한시간"
    )]
    [SerializeField]
    private float m_dieAfterDownSeconds = 60f;

    // 서버 권위 무력화 원인 — 서버만 쓰고 모든 클라가 읽는다. (PlayerData.m_syncedHp와 동일 패턴)
    // 예전에는 bool 두 개(무력화 여부 + 구조 가능 여부)였는데, 기절이 들어오며 '구조 불가'가 둘로
    // 갈려(매달기·기절) 조합으로는 구분할 수 없게 됐다 — 원인 하나로 합쳤다. (#252)
    private readonly NetworkVariable<IncapacitationCause> m_causeSynced =
        new NetworkVariable<IncapacitationCause>();

    private IncapacitationCause m_cause; // 서버·오프라인의 진실값 (비네트워크 Play 테스트 폴백)

    // 기절 회차 — 지연 복구가 '자기가 건 기절'만 풀게 하는 토큰. 기절이 풀린 뒤 다시 걸리거나 그 사이
    // 다운·매달기가 들어오면 회차가 어긋나, 낡은 타이머는 무동작으로 끝난다. (#252)
    private int m_stunEpisode;

    // 무력화 회차 — Die 전환 타이머가 '자기가 본 다운'만 죽이게 하는 토큰. 기절 회차와 같은 장치지만
    // 원인이 바뀔 때마다(구조·매달기·재다운) 올라가므로, 구조된 뒤 다시 다운되어도 낡은 타이머가
    // 옛 카운트다운으로 Die를 만들지 않는다. (#364)
    private int m_causeEpisode;

    // Die 전환 예정 시각 — HUD 카운트다운 표시용. 다운이 아닐 때는 0. 시간 기준은 CurrentTime과 같다.
    // 서버 권위 값이라 원격 오너도 같은 남은 시간을 본다(늦게 접속해도 즉시 맞는다).
    private readonly NetworkVariable<double> m_dieDeadlineSynced = new NetworkVariable<double>();
    private double m_dieDeadline; // 서버·오프라인의 진실값 (m_cause와 동일 이중 구조)

    /// <summary>무력화 원인. 서버·오프라인은 실참조, 원격 피어는 동기화값으로 판정. (PlayerEscorter.IsEscorting 관례)</summary>
    public IncapacitationCause Cause => IsSpawned && !IsServer ? m_causeSynced.Value : m_cause;

    /// <summary>무력화(행동불능) 여부 — 이동·아이템·상호작용 게이트가 읽는다. 원인을 가리지 않는다.</summary>
    public bool IsIncapacitated => Cause != IncapacitationCause.None;

    /// <summary>HP 0 다운인지 — <b>구조(리바이브) 대상은 이것만</b>이다. 매달기·기절은 스스로 풀리고,
    /// Die는 현장 구조로 못 일어난다(본부 이송 부활만, #364/#365).</summary>
    public bool IsDowned => Cause == IncapacitationCause.Down;

    /// <summary>다운 방치로 기능 정지(Die)됐는지 — 운반·본부 부활(#365)의 대상 판정용. (#364)</summary>
    public bool IsDead => Cause == IncapacitationCause.Die;

    /// <summary>
    /// 스스로도 남의 손으로도 곧 일어나지 못하는 상태 — 다운 또는 Die. (#364)
    /// <b>전멸(게임오버) 판정과 조준 히트박스</b>가 이걸 본다: 다운만 세면 전원이 Die로 넘어간 순간
    /// 판정이 통과하지 못해 게임오버가 영영 뜨지 않는다(RoundManager.AreAllPlayersOutOfAction).
    /// 매달기·기절은 시간이 지나면 스스로 풀리므로 포함하지 않는다. (#252)
    /// </summary>
    public bool IsOutOfAction => IsDowned || IsDead;

    /// <summary>테이저 피격 기절인지. 모션은 다운과 같으므로(#252) 표시·집계처럼 원인을 구분할 때만 쓴다.</summary>
    public bool IsStunned => Cause == IncapacitationCause.Stun;

    /// <summary>
    /// Die 전환까지 남은 시간(초) — 다운이 아니면 0. 구조 압박을 보여주는 HUD용. (#364)
    /// 다운 중 갑자기 Die로 떨어지면 무슨 일이 일어난 건지 알 수 없으므로 남은 시간을 노출한다.
    /// </summary>
    public float RemainingUntilDie
    {
        get
        {
            if (!IsDowned)
                return 0f;

            double deadline = IsSpawned && !IsServer ? m_dieDeadlineSynced.Value : m_dieDeadline;
            return Mathf.Max(0f, (float)(deadline - CurrentTime));
        }
    }

    // 카운트다운의 시간 기준 — 온라인은 서버 시각(모든 피어가 같은 값을 읽는다), 오프라인은 로컬 시각.
    // Time.time을 그대로 동기화하면 피어마다 기점이 달라 남은 시간이 어긋난다.
    private double CurrentTime =>
        IsSpawned && NetworkManager != null ? NetworkManager.ServerTime.Time : Time.timeAsDouble;

    // 살아 있는 인스턴스 목록 — 매 프레임 플레이어를 훑어야 하는 쪽(본부 부활 구역 #365)이
    // FindObjectsByType으로 씬 전체를 뒤지지 않게 한다. 그쪽은 상시 도는 검사라 조회 비용이 그대로 상시 비용이 된다.
    // 활성/비활성 시점에 스스로 등록·해제하므로 스폰 여부·오프라인 테스트와 무관하게 정확하다.
    private static readonly List<PlayerIncapacitation> s_instances = new();

    /// <summary>현재 씬에 존재하는 모든 플레이어의 무력화 컴포넌트. 매 프레임 순회해도 되는 무할당 목록. (#365)</summary>
    public static IReadOnlyList<PlayerIncapacitation> All => s_instances;

    private void OnEnable() => s_instances.Add(this);

    private void OnDisable() => s_instances.Remove(this);

    /// <summary>무력화 상태가 바뀔 때 발행 — 애니메이션·UI 훅용. 원인만 바뀌면 울리지 않는다.</summary>
    public event Action<bool> OnIncapacitatedChanged;

    /// <summary>
    /// 서버·오프라인에서 '아무' 플레이어의 무력화 상태가 바뀔 때 발행 — 전원 다운(전멸) 판정 등 전역 로직용. (#105)
    /// 서버 권위 경로(<see cref="SetCause"/>)에서만 발행되므로 클라이언트에서는 울리지 않는다.
    /// </summary>
    public static event Action OnAnyIncapacitatedChanged;

    public override void OnNetworkSpawn()
    {
        // 원격 피어는 서버 Set 경로를 타지 않으므로, 동기화값 변화를 이벤트로 중계한다.
        m_causeSynced.OnValueChanged += HandleSyncedChanged;

        // 늦게 접속한 클라: 이미 쓰러진 플레이어의 현재 상태를 즉시 반영한다.
        // (OnValueChanged는 '변화' 시에만 발생하므로 스폰 시 한 번 맞춰줘야 한다)
        RefreshAimHitbox();
    }

    public override void OnNetworkDespawn()
    {
        m_causeSynced.OnValueChanged -= HandleSyncedChanged;
    }

    // 서버(호스트 포함)는 SetCause에서 이벤트를 직접 발행하므로 여기선 원격 클라만 중계 (이중 발행 방지)
    private void HandleSyncedChanged(IncapacitationCause previous, IncapacitationCause current)
    {
        if (IsServer)
            return;

        RefreshAimHitbox();

        // 원인만 바뀌고 무력화 여부는 그대로면 알리지 않는다 — 구독자는 bool만 본다
        bool was = previous != IncapacitationCause.None;
        bool now = current != IncapacitationCause.None;
        if (was != now)
            OnIncapacitatedChanged?.Invoke(now);
    }

    // 조준 히트박스 = 쓰러져 있는 동안(다운·Die)만 켠다. 서버·원격·오프라인 모든 인스턴스에서 실행된다.
    // Die도 켜는 이유: 구조는 막히지만 운반(#365)하려면 조준은 잡혀야 한다. 구조 가능 여부는 히트박스가
    // 아니라 PlayerReviver가 IsDowned로 가른다. (#364)
    private void RefreshAimHitbox()
    {
        if (m_reviveHitbox != null)
            m_reviveHitbox.SetActive(IsOutOfAction);
    }

    /// <summary>
    /// 무력화 진입 — 서버(또는 오프라인)에서만. HP0 다운(#105)·매달기(#101)가 호출한다.
    /// 기절은 스스로 풀려야 하므로 이 경로가 아니라 <see cref="ServerStun"/>을 쓴다.
    /// Die는 다운 방치 타이머로만 들어가므로 여기로는 지정할 수 없다. (#364)
    /// </summary>
    public void Incapacitate(IncapacitationCause cause = IncapacitationCause.Down)
    {
        if (IsSpawned && !IsServer)
            return; // 서버 권위 방어 (PlayerEscorter 관례)

        if (cause == IncapacitationCause.None)
        {
            Debug.LogWarning(
                "PlayerIncapacitation: 원인 None으로는 무력화할 수 없다 — 해제는 Recover()",
                this
            );
            return;
        }

        if (cause == IncapacitationCause.Die)
        {
            Debug.LogWarning(
                "PlayerIncapacitation: Die는 밖에서 걸 수 없다 — 다운 방치 타이머로만 전환된다 (#364)",
                this
            );
            return;
        }

        // Die는 다른 무력화로 덮이지 않는다 — 덮으면 그 원인의 자동 해제에 딸려 공짜로 살아난다.
        // 실제 경로가 있다: 오검거 매달기 폴백(WrongfulArrestPenalty.HangAsync)은 대상이 이미
        // 무력화됐는지 보지 않고 Penalty를 걸어, 30초 뒤 Recover()로 Die까지 함께 풀어 버린다. (#364)
        if (Cause == IncapacitationCause.Die)
        {
            Debug.Log(
                $"[Die] 무력화 덮어쓰기 무시 — {name}은 기능 정지 상태다 (요청 원인: {cause})",
                this
            );
            return;
        }

        SetCause(cause);
    }

    /// <summary>
    /// 기절 진입 — seconds 뒤 <b>스스로</b> 회복한다. 테이저 아군 오사(#252)가 호출. 서버 전용.
    /// 이미 무력화된 대상은 무시한다 — 다운·매달기를 기절로 덮어쓰면 그 무력화가 기절 타이머에 일찍 풀린다.
    /// </summary>
    public void ServerStun(float seconds)
    {
        if (IsSpawned && !IsServer)
            return;
        if (IsIncapacitated)
            return;

        SetCause(IncapacitationCause.Stun);
        ServerStunTimerAsync(seconds, ++m_stunEpisode).Forget();
    }

    /// <summary>무력화 해제(구조·복구) — 서버(또는 오프라인)에서만.</summary>
    public void Recover()
    {
        if (IsSpawned && !IsServer)
            return;
        SetCause(IncapacitationCause.None);
    }

    // 기절 자동 회복 타이머. 씬 전환·파괴는 토큰으로 안전 중단한다 (WrongfulArrestPenalty.HangAsync 관례).
    private async UniTaskVoid ServerStunTimerAsync(float seconds, int episode)
    {
        try
        {
            await UniTask.Delay(
                TimeSpan.FromSeconds(seconds),
                cancellationToken: destroyCancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            return; // 파괴·퇴장 — 복구할 대상이 이미 없다
        }

        // 내가 건 기절이 그대로일 때만 푼다 — 그 사이 구조·다운·매달기가 들어왔으면 남의 상태다
        if (m_stunEpisode != episode || m_cause != IncapacitationCause.Stun)
            return;

        Recover();
    }

    // 다운 방치 → Die 전환 타이머. 기절 타이머와 같은 구조지만 방향이 반대다 — 시간이 지나면
    // 풀리는 게 아니라 더 나쁜 상태로 떨어진다. (#364)
    private async UniTaskVoid ServerDieTimerAsync(int episode)
    {
        try
        {
            await UniTask.Delay(
                TimeSpan.FromSeconds(m_dieAfterDownSeconds),
                cancellationToken: destroyCancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            return; // 파괴·퇴장 — 전환할 대상이 이미 없다
        }

        // 내가 본 다운이 그대로일 때만 떨어뜨린다 — 구조됐거나 다른 무력화로 바뀌었으면 남의 상태다
        if (m_causeEpisode != episode || m_cause != IncapacitationCause.Down)
            return;

        Debug.Log($"[Die] 구조 제한시간 경과 — 기능 정지: {name} (본부 이송 부활만 남는다)", this);
        SetCause(IncapacitationCause.Die);
    }

    // 서버 권위 값 변경 + 로컬 이벤트 발행을 함께 처리 — 서버(또는 오프라인)에서만 호출된다.
    private void SetCause(IncapacitationCause cause)
    {
        if (m_cause == cause)
            return; // 중복 트리거 무시

        bool was = IsIncapacitated;
        m_cause = cause;
        if (IsSpawned && IsServer)
            m_causeSynced.Value = cause;

        // 원인이 바뀌었으니 이전 회차의 Die 타이머는 무효다 — 구조 후 재다운도 새 회차로 다시 센다. (#364)
        m_causeEpisode++;
        if (cause == IncapacitationCause.Down)
        {
            SetDieDeadline(CurrentTime + m_dieAfterDownSeconds);
            ServerDieTimerAsync(m_causeEpisode).Forget();
        }
        else
        {
            SetDieDeadline(0d);
        }

        RefreshAimHitbox();
        if (was != IsIncapacitated)
            OnIncapacitatedChanged?.Invoke(IsIncapacitated);
        OnAnyIncapacitatedChanged?.Invoke(); // 전역 훅 — RoundManager가 전원 행동불능(전멸) 여부를 재검사
    }

    // Die 전환 예정 시각 갱신 — 실참조와 동기화값을 함께 쓴다(m_cause/m_causeSynced와 동일 관례).
    private void SetDieDeadline(double deadline)
    {
        m_dieDeadline = deadline;
        if (IsSpawned && IsServer)
            m_dieDeadlineSynced.Value = deadline;
    }
}
