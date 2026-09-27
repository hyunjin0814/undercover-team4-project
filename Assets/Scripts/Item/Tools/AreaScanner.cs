using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 구역 스캔 아이템 — 사용 지점 반경 안에 진범이 있으면 초록, 없으면 빨강 링을 근처 전원에게 띄운다.
/// 즉발 + 쿨타임형이며, 판정은 서버에서 하고 쿨다운 종료 시각은 NetworkVariable로 동기화한다.
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

    private readonly NetworkVariable<double> m_cooldownEndSynced = new NetworkVariable<double>(k_noCooldown);
    private double m_cooldownEnd = k_noCooldown;

    private double Now =>
        IsSpawned && NetworkManager != null ? NetworkManager.ServerTime.Time : Time.timeAsDouble;

    public float CooldownRemaining =>
        (float)Math.Max(0d, (IsSpawned ? m_cooldownEndSynced.Value : m_cooldownEnd) - Now);

    public bool IsOnCooldown => CooldownRemaining > 0f;

    private readonly DeviceBlackoutGate m_blackout = new();

    private bool IsBlackout => m_blackout.IsActive;

    public event Action<float> OnCooldownUseAttempt;

    public event Action OnBlackoutUseAttempt;

    private static readonly Collider[] s_scanColliders = new Collider[512];

    private static int s_hitLayers;

    private static int HitLayers
    {
        get
        {
            if (s_hitLayers == 0)
            {
                int ragdoll = LayerMask.NameToLayer("Ragdoll");
                s_hitLayers = ragdoll >= 0 ? ~(1 << ragdoll) : ~0;
            }
            return s_hitLayers;
        }
    }

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

    /// <summary>원점·먹통·쿨다운을 재검증한 뒤 서버에서 반경 판정을 수행한다.</summary>
    private void ServerScan(Vector3 origin)
    {
        if (IsSpawned && !IsServer)
            return;

        if (!IsOriginPlausible(origin))
        {
            Debug.LogWarning($"AreaScanner: 사용 원점이 소지자 위치와 너무 멀다 — 스캔 거부 (origin={origin})", this);
            return;
        }

        if (IsBlackout)
        {
            Feedback?.NotifyOwner("구역 스캔 실패 — 전자기기 먹통");
            return;
        }

        if (IsOnCooldown)
        {
            return;
        }

        bool found = ServerFindCriminalInRange(origin);

        StartCooldown();

        Debug.Log($"[구역 스캔] {(found ? "진범 발견" : "진범 없음")} — 반경 {m_scanRadius}m, 위치 {origin}");

        if (!IsSpawned)
        {
            PlayResultLocal(origin, found);
            return;
        }

        ScanResultRpc(origin, found);
    }

    private bool ServerFindCriminalInRange(Vector3 origin)
    {
        int hitCount = Physics.OverlapSphereNonAlloc(
            origin, m_scanRadius, s_scanColliders, HitLayers, QueryTriggerInteraction.Ignore);

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

    /// <summary>클라가 보낸 사용 원점이 소지자 위치 근처인지 확인한다(원점 위조 방어).</summary>
    private bool IsOriginPlausible(Vector3 origin)
    {
        PlayerInteractor holder = Holder;
        if (holder == null)
            return false;

        return (origin - holder.transform.position).sqrMagnitude <= m_originTolerance * m_originTolerance;
    }

    [Rpc(SendTo.Everyone)]
    private void ScanResultRpc(Vector3 origin, bool found) => PlayResultLocal(origin, found);

    public event Action<bool, float> OnScanResult;

    private void PlayResultLocal(Vector3 origin, bool found)
    {
        OnScanResult?.Invoke(found, m_scanRadius);

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
