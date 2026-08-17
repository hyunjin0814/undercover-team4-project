using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 유치장 사이렌 버튼 (#488) — 본부에 설치되는 빨간 버튼. 누르면 유치장에 경보음이 울리고 진행 중인 탈옥(#231)이 저지된다.
/// 이름은 버튼이지만 얇은 전달자가 아니라 설치형 설비 본체다. (CCTVPowerButton과 달리 쿨다운·먹통 판정을 직접 들고 있다)
/// 판정은 전부 서버 — 클라 게이트는 조준 피드백용이라 신뢰하지 않는다.
/// </summary>
public class JailSirenButton : InstallableItem
{
    // 쿨다운이 걸려 있지 않음을 나타내는 값 — ServerTime은 0에서 시작하므로 음수를 쓴다 (RoundTimerSync와 동일).
    private const double k_noCooldown = -1d;

    // 경보음이 울릴 자리 — 버튼이 있는 본부가 아니라 유치장이다. 갇힌 본인에게 들려야 억제력이 생긴다.
    // 인스펙터에 배선하지 않는 이유는 맵마다 씬이 다르기 때문이다 — 배선을 두면 맵을 한 장 늘릴
    // 때마다 사람이 기억해서 이어야 하고, 실제로 #488 이후 지금까지 어느 씬에서도 이어지지 않아
    // 사이렌은 내내 무음이었다. App 등록(#592)이라 맵이 늘어도 배선할 것이 없다.
    private JailZone m_jailZone;

    // 연타로 감시를 대체하지 못하게 하는 값 — 자물쇠 해제 창(JailbreakEvent.m_unlockSeconds, 기본 10초)보다
    // 넉넉히 길어야 한다. 추첨 주기 하한이 20초라 이보다 크게 올리면 다음 기회를 잡아먹는다.
    [Tooltip("연타 방지 쿨다운(초). 서버가 강제한다")]
    [Min(0f)]
    [SerializeField]
    private float m_cooldownSeconds = 22f;

    // 쿨다운 종료 시각(ServerTime 기준) — 남은 시간을 어느 피어에서든 계산할 수 있어야 하므로
    // "쿨다운 중인가" bool이 아니라 시각을 동기화한다. 늦게 접속한 피어도 스폰 페이로드로 받아
    // 진행 중인 쿨다운을 중간부터 이어 표시한다. (RoundTimerSync.m_endServerTime과 같은 방식)
    private readonly NetworkVariable<double> m_cooldownEndSynced = new NetworkVariable<double>(k_noCooldown);
    private double m_cooldownEnd = k_noCooldown; // 오프라인(비네트워크 Play) 폴백

    // 동기화 시계 — 세션 밖에서는 로컬 시간으로 떨어진다 (PlayerIncapacitation과 동일).
    private double Now => IsSpawned && NetworkManager != null ? NetworkManager.ServerTime.Time : Time.timeAsDouble;

    /// <summary>쿨다운 남은 시간(초) — 0이면 지금 쓸 수 있다. 모든 피어에서 같은 값이 나온다.</summary>
    public float CooldownRemaining =>
        (float)Math.Max(0d, (IsSpawned ? m_cooldownEndSynced.Value : m_cooldownEnd) - Now);

    /// <summary>쿨다운 중인가 — 시각 비교라 별도 타이머 없이 스스로 풀린다.</summary>
    public bool IsOnCooldown => CooldownRemaining > 0f;

    /// <summary>쿨다운 전체 길이(초) — 표시(JailSirenButtonView)가 진행률을 채울 때 쓴다.</summary>
    public float CooldownSeconds => m_cooldownSeconds;

    /// <summary>쿨다운이 걸렸다 — 인자는 남은 시간(초). 전 피어에서 발생하고 표시가 구독한다.</summary>
    public event Action<float> OnCooldownStarted;

    [Header("먹통 연동 (#434)")]
    // 기본은 막지 않는다 — 먹통이면 CCTV가 차단돼 유치장이 안 보이는데 경보까지 못 울리면
    // 할 수 있는 게 아무것도 없다. 대응 수단을 통째로 뺏는 대신, 화면 없이 무전에 기대
    // 타이밍을 재서 누르는 쪽으로 남긴다. 밸런싱으로 되돌릴 수 있게 스위치는 둔다.
    [Tooltip("켜면 먹통 중 사이렌이 막힌다 — 화면도 경보도 없어 손쓸 방법이 사라진다")]
    [SerializeField]
    private bool m_blockedByBlackout;

    // 매니저는 캐싱하지 않고 App 경유로 매번 읽는다 (R1/R8).
    private bool IsJammed =>
        m_blockedByBlackout && (App.Game.SuddenEvent?.GetEvent<DeviceBlackoutEvent>()?.IsCommsBlackout ?? false);

    protected override void OnInstallableSpawn()
    {
        m_cooldownEndSynced.OnValueChanged += HandleCooldownEndChanged;

        // 쿨다운 도중에 들어온 피어 — 시작 통지를 놓쳤으므로 여기서 남은 만큼 이어 붙인다.
        if (IsOnCooldown)
            OnCooldownStarted?.Invoke(CooldownRemaining);
    }

    protected override void OnInstallableDespawn() => m_cooldownEndSynced.OnValueChanged -= HandleCooldownEndChanged;

    private void HandleCooldownEndChanged(double previous, double current) => RaiseCooldownStarted();

    /// <summary>미설치·쿨다운·먹통이면 윤곽선이 뜨지 않는다 — ServerFire가 거르는 조건과 같은 기준. (#184)</summary>
    public override bool CanInteract(GameObject interactor) =>
        base.CanInteract(interactor) && !IsOnCooldown && !IsJammed;

    protected override void OnInteract(GameObject interactor)
    {
        if (!IsSpawned || IsServer)
        {
            ServerFire();
            return;
        }

        RequestFireRpc();
    }

    // Everyone 권한 — 씬에 놓인 서버 소유 오브젝트라 오너가 없다 (SignalDecoder와 동일).
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestFireRpc() => ServerFire();

    // 클라 게이트는 신뢰 불가 — 세 조건을 서버가 다시 본다.
    private void ServerFire()
    {
        if (IsSpawned && !IsServer)
            return;
        if (!IsInstalled || IsOnCooldown || IsJammed)
            return;

        StartCooldown();

        bool repelled = App.Game.SuddenEvent?.GetEvent<JailbreakEvent>()?.ServerRepelIntruder() ?? false;
        Debug.Log(repelled ? "[사이렌] 침입자 저지" : "[사이렌] 울렸지만 제지할 침입 없음");

        if (!IsSpawned)
        {
            PlayLocal(); // 오프라인 Play 테스트
            return;
        }

        PlaySirenRpc();
    }

    // 종료 시각만 찍으면 끝 — 해제 타이머가 필요 없다. 동기화 콜백은 값을 쓴 서버에서도 돌므로
    // 이벤트는 그쪽에서 한 번만 나간다. 오프라인은 콜백이 없어 직접 발행한다.
    private void StartCooldown()
    {
        m_cooldownEnd = Now + m_cooldownSeconds;

        if (IsSpawned && IsServer)
            m_cooldownEndSynced.Value = m_cooldownEnd;
        else if (!IsSpawned)
            RaiseCooldownStarted();
    }

    private void RaiseCooldownStarted()
    {
        float remaining = CooldownRemaining;
        if (remaining > 0f)
            OnCooldownStarted?.Invoke(remaining);
    }

    [Rpc(SendTo.Everyone)]
    private void PlaySirenRpc() => PlayLocal();

    private void PlayLocal()
    {
        if (m_jailZone == null)
            m_jailZone = App.Game.Jail;

        if (m_jailZone == null)
        {
            Debug.LogWarning("JailSirenButton: 유치장을 찾지 못해 경보음을 낼 자리가 없다", this);
            return;
        }

        // 방 안 지점이면 어디를 잡아도 결과가 같다 — 방(7.2m)이 통째로 감쇠 없는 거리 안에 들어가게
        // AudioLibrary의 MinDistance를 잡아 두었으므로, 서 있는 자리에 따라 크기가 달라지지 않는다.
        App.Sound?.PlaySfxAt(EAudioClip.JailSiren, m_jailZone.PlayerEntryPoint.position);
    }
}
