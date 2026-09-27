using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 구역 스캔 아이템 (#490) — 사용 지점 기준 반경 <see cref="m_scanRadius"/> 안에 진범이 있으면
/// 초록, 없으면 빨강으로 서서히 퍼지는 링을 띄운다. 근처 플레이어 전원이 같은 링을 본다.
///
/// <b>즉발 + 쿨타임</b>이다 — 채널링이 없고, 판정 즉시 결과가 나온다. 사용해도 소모되지 않고
/// <see cref="m_cooldownSeconds"/> 뒤 다시 쓸 수 있다(<see cref="ItemBase.ServerConsume"/> 미사용).
/// 이슈 원안(사이렌/무음, 일회용 소모)에서 두 축이 바뀐 결과다(2026-08-23 결정) — 자세한 배경은
/// <see cref="ItemBase.ServerConsume"/> 문서 참고.
///
/// <b>쿨다운은 Taser처럼 서버 로컬 float이 아니라 <see cref="JailSirenButton"/>처럼 종료 시각을
/// NetworkVariable로 동기화한다.</b> 60초는 오너가 남은 시간을 알아야 하는 길이라(장착 아이콘
/// 힌트가 맞으려면 클라에서도 <see cref="CanUse"/>가 정확해야 한다) 서버 전용 상태로는 부족하다.
///
/// <b>진범 기준은 <see cref="CitizenIdentity.IsCriminal"/>뿐이다</b> — 제보 전화로 공개되기 전
/// 예비 용의자는 반경에 있어도 빨강이다. 이 판정은 서버 전용 값이라 클라에서는 항상 false이므로,
/// 반경 수집·판정은 전부 서버에서만 하고 결과(found)만 전 피어에 회신한다.
///
/// 조준 대상이 없다 — 대상 윤곽선(<see cref="CanTarget"/>)·조준 안내(<see cref="TargetPromptLabel"/>)는
/// 기본값(false/null)을 그대로 쓴다. Taser와 같은 이유다: 상호작용 레이(3m)와 실제 반경(20m)이
/// 어긋난 표시가 되기 때문이다.
/// </summary>
[RequireComponent(typeof(OwnerFeedback))]
public class AreaScanner : ItemBase
{
    private OwnerFeedback m_feedback;

    private OwnerFeedback Feedback => this.ResolveCapability(ref m_feedback);

    private const double k_noCooldown = -1d;

    [Header("구역 스캔 설정")]
    [Tooltip("판정 반경(m) — 링 표시 반경과 같은 값을 쓴다(단일 출처)")]
    [Min(0.1f)]
    [SerializeField]
    private float m_scanRadius = 20f;

    [Tooltip("재사용까지 대기 시간(초). 서버가 강제한다")]
    [Min(0f)]
    [SerializeField]
    private float m_cooldownSeconds = 60f;

    // 클라가 보낸 사용 원점이 서버가 아는 소지자 위치에서 이만큼(m) 넘게 떨어져 있으면 거부한다.
    // Taser.m_originTolerance와 같은 근거(카메라 높이 + 이동 지연 여유) — 다만 여기 원점은 카메라가
    // 아니라 소지자 루트 위치라 오차 여지가 더 작다.
    [Tooltip("클라가 보낸 사용 원점이 서버가 아는 소지자 위치에서 이만큼(m) 넘게 떨어져 있으면 거부한다")]
    [SerializeField]
    private float m_originTolerance = 2f;

    [Header("링 연출")]
    [Tooltip("결과 링 프리팹(AreaScanRingView) — 비우면 소리만 나고 링은 뜨지 않는다")]
    [SerializeField]
    private GameObject m_ringPrefab;

    [SerializeField]
    private Color m_hitColor = Color.green;

    [SerializeField]
    private Color m_missColor = Color.red;

    // 쿨다운 종료 시각(ServerTime 기준) — 시각을 동기화하는 이유는 JailSirenButton과 같다:
    // 해제 타이머가 필요 없고, 중간 접속·재장착에서도 남은 시간을 그대로 이어 계산할 수 있다.
    // 아이템 인스턴스에 붙으므로 버렸다 다시 주워도 쿨다운이 따라간다.
    private readonly NetworkVariable<double> m_cooldownEndSynced = new NetworkVariable<double>(k_noCooldown);
    private double m_cooldownEnd = k_noCooldown; // 오프라인(비네트워크 Play) 폴백

    private double Now =>
        IsSpawned && NetworkManager != null ? NetworkManager.ServerTime.Time : Time.timeAsDouble;

    /// <summary>쿨다운 남은 시간(초) — 0이면 지금 쓸 수 있다. 모든 피어에서 같은 값이 나온다.</summary>
    public float CooldownRemaining =>
        (float)Math.Max(0d, (IsSpawned ? m_cooldownEndSynced.Value : m_cooldownEnd) - Now);

    /// <summary>쿨다운 중인가 — 시각 비교라 별도 타이머 없이 스스로 풀린다.</summary>
    public bool IsOnCooldown => CooldownRemaining > 0f;

    // 스캐너(#372)와 판정 로직이 같아 공용 게이트를 쓴다.
    private readonly DeviceBlackoutGate m_blackout = new();

    /// <summary>전자기기 먹통(#106) 중인지 — 구역 스캔은 먹통 동안 사용할 수 없다(개인 스캐너와 동일 게이트).</summary>
    private bool IsBlackout => m_blackout.IsActive;

    /// <summary>쿨다운 중 사용을 시도했다 — 인자는 남은 시간(초). 오너 로컬 전용.</summary>
    public event Action<float> OnCooldownUseAttempt;

    /// <summary>먹통 중 사용을 시도했다 — 오너 로컬 전용.</summary>
    public event Action OnBlackoutUseAttempt;

    // 사유 안내는 전부 위 두 이벤트로 나간다 — ToastOwner/RaiseOwnerToast는 쓰지 않는다.
    // 아래 ServerScan의 NotifyOwner 호출은 정상 흐름이 아니라 위조 RPC로 클라 게이트를
    // 우회했을 때의 서버 로그용이라 토스트로 띄울 대상이 없다.

    // ---- 반경 수집 버퍼 ----
    // 서버(또는 오프라인)에서만 쓰므로 정적으로 공유해도 안전하다 (BombBlast.s_blastColliders 관례).
    // 1인당 래그돌 본 콜라이더가 여럿이라 20m 반경이면 256칸도 금방 찬다 — 512로 넉넉히 잡는다.
    private static readonly Collider[] s_scanColliders = new Collider[512];

    private static int s_hitLayers; // 0 = 아직 조회 전

    /// <summary>
    /// 판정용 레이어 마스크 — 래그돌 본을 뺀 전 레이어. (NpcResistState.HitLayers와 같은 패턴)
    /// ⚠ LayerMask.NameToLayer는 필드 초기화에서 호출 금지라 첫 사용 시점에 늦게 조회한다.
    /// </summary>
    private static int HitLayers
    {
        get
        {
            if (s_hitLayers == 0)
            {
                int ragdoll = LayerMask.NameToLayer("Ragdoll");
                s_hitLayers = ragdoll >= 0 ? ~(1 << ragdoll) : ~0; // 레이어가 없으면 전 레이어로 폴백
            }
            return s_hitLayers;
        }
    }

    // ---- ItemBase — 사용 요청 진입점 ----

    /// <summary>쿨다운 중이 아니고 먹통이 아닐 때만 사용 가능. (UI 힌트용 — 최종 판정은 서버가 재검증)</summary>
    public override bool CanUse() => !IsOnCooldown && !IsBlackout;

    /// <summary>대상을 쓰지 않는 아이템 — 오너의 사용 의도만 서버로 전달한다 (Taser.Use와 같은 구조, #55).</summary>
    public override void Use(GameObject target)
    {
        if (!CanUse())
        {
            if (IsBlackout)
                OnBlackoutUseAttempt?.Invoke();
            else
                OnCooldownUseAttempt?.Invoke(CooldownRemaining);
            return;
        }

        PlayerInteractor holder = Holder;
        if (holder == null)
        {
            Debug.LogWarning("AreaScanner: PlayerInteractor를 찾지 못함 — 사용 원점 없음", this);
            return;
        }

        Vector3 origin = holder.transform.position;

        if (this.HasServerAuthority())
        {
            ServerScan(origin);
            return;
        }

        if (!IsOwner)
            return;

        RequestScanRpc(origin);
    }

    [Rpc(SendTo.Server)]
    private void RequestScanRpc(Vector3 origin) => ServerScan(origin);

    // ---- 서버 판정 ----

    /// <summary>
    /// 서버에서 반경 판정을 수행한다. 클라 CanUse는 신뢰할 수 없으므로 원점·먹통·쿨다운을 전부 재검증한다.
    /// 검증 순서(원점 → 먹통 → 쿨다운)는 Taser.ServerFire와 같다 — 거부된 요청이 쿨다운을 깎으면 안 된다.
    /// </summary>
    private void ServerScan(Vector3 origin)
    {
        if (IsSpawned && !IsServer)
            return; // 서버 권위 방어 (Taser.ServerFire 관례)

        if (!IsOriginPlausible(origin))
        {
            Debug.LogWarning($"AreaScanner: 사용 원점이 소지자 위치와 너무 멀다 — 스캔 거부 (origin={origin})", this);
            return;
        }

        if (IsBlackout)
        {
            // 정상 흐름은 클라 CanUse()가 이미 막는다 — 여기 닿는다면 위조 RPC다 (Scanner.ServerBeginScan 관례)
            Feedback?.NotifyOwner("구역 스캔 실패 — 전자기기 먹통");
            return;
        }

        if (IsOnCooldown)
        {
            // 위조 RPC로 쿨다운을 무시하고 온 요청 — 정상 UI라면 애초에 CanUse에서 막힌다.
            return;
        }

        bool found = ServerFindCriminalInRange(origin);

        StartCooldown();

        Debug.Log($"[구역 스캔] {(found ? "진범 발견" : "진범 없음")} — 반경 {m_scanRadius}m, 위치 {origin}");

        if (!IsSpawned)
        {
            PlayResultLocal(origin, found); // 오프라인 Play 테스트 폴백
            return;
        }

        ScanResultRpc(origin, found);
    }

    // 반경 내 NPC를 모아 진범이 있는지만 본다 — 수는 세지 않는다(이진 신호).
    // 기절·사망한 진범은 그대로 포함하지만, 유치장에 수감(Jailed)된 진범은 뺀다 — 이미 처리가
    // 끝난 표적까지 "발견"으로 뜨면 다 잡고도 계속 찾아야 하는 것처럼 보인다(#938).
    private bool ServerFindCriminalInRange(Vector3 origin)
    {
        int hitCount = Physics.OverlapSphereNonAlloc(
            origin, m_scanRadius, s_scanColliders, HitLayers, QueryTriggerInteraction.Ignore);

        // 포화는 조용히 틀린다 — 넘친 대상 중에 진범이 있어도 놓칠 수 있다 (BombBlast 관례)
        if (hitCount == s_scanColliders.Length)
            Debug.LogWarning($"[구역 스캔] 대상 버퍼({s_scanColliders.Length}) 포화 — 일부 NPC가 판정에서 누락됐을 수 있다", this);

        for (int i = 0; i < hitCount; i++)
        {
            CitizenIdentity identity = s_scanColliders[i].GetComponentInParent<CitizenIdentity>();
            if (identity == null || !identity.IsCriminal)
                continue;

            NpcController npc = identity.GetComponent<NpcController>();
            if (npc != null && npc.CurrentState == NpcState.Jailed)
                continue;

            return true;
        }

        return false;
    }

    private void StartCooldown()
    {
        m_cooldownEnd = Now + m_cooldownSeconds;

        if (IsSpawned && IsServer)
            m_cooldownEndSynced.Value = m_cooldownEnd;
    }

    /// <summary>
    /// 클라가 보낸 사용 원점이 서버가 아는 이 아이템 소지자 위치 근처인지 — 원점 위조 방어.
    /// 원점을 위조하면 맵 어디서든 원격으로 스캔할 수 있으므로 Taser보다 더 중요한 검증이다.
    /// </summary>
    private bool IsOriginPlausible(Vector3 origin)
    {
        PlayerInteractor holder = Holder;
        if (holder == null)
            return false; // 아무에게도 안 들린 아이템으로 스캔 요청이 올 수는 없다

        return (origin - holder.transform.position).sqrMagnitude <= m_originTolerance * m_originTolerance;
    }

    // ---- 결과 전파 ----

    // 이미 전 피어에서 도는 경로이므로 FxManager.PlayHere를 쓴다 — PlayEverywhere를 쓰면
    // 피어마다 재전파돼 소리가 겹친다(FxManager 문서 관례).
    [Rpc(SendTo.Everyone)]
    private void ScanResultRpc(Vector3 origin, bool found) => PlayResultLocal(origin, found);

    /// <summary>
    /// 판독 결과 — 전 피어에서 발행된다. 인자는 (진범 있었나, 판정 반경). (#915)
    /// 이 아이템을 장착한 사람의 <c>AreaScanPresenter</c>만 바인딩돼 있어 그 사람 화면에만 문구가 뜬다.
    /// </summary>
    public event Action<bool, float> OnScanResult;

    private void PlayResultLocal(Vector3 origin, bool found)
    {
        OnScanResult?.Invoke(found, m_scanRadius);

        // 링이 이미 근처 전원에게 결과를 보여주는 공개 연출이라, 소리도 공개 3D로 맞춘다 —
        // 판독음 하나 때문에 나만 아는 정보가 새로 생기지 않는다 (JailSirenButton과 같은 방침).
        // FxManager를 거치는 이유는 "무슨 일이 일어났는지"만 여기서 고르고 조합(파티클·소리·전파)은
        // 인스펙터 표에 맡기기 위해서다 — 이미 전 피어에서 도는 경로라 PlayHere를 쓴다
        // (PlayEverywhere를 쓰면 피어마다 재전파돼 소리가 겹친다).
        App.Game.Fx?.PlayHere(found ? EFx.AreaScanHit : EFx.AreaScanMiss, origin);

        if (m_ringPrefab == null)
            return;

        GameObject ring = Instantiate(m_ringPrefab, origin, Quaternion.identity);
        if (ring.TryGetComponent(out AreaScanRingView view))
            view.Play(origin, m_scanRadius, found ? m_hitColor : m_missColor);
        else
            Debug.LogWarning("AreaScanner: m_ringPrefab에 AreaScanRingView가 없다 — 링이 표시되지 않는다", this);
    }
}
