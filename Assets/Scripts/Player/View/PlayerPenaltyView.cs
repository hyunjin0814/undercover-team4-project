using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 오검거 페널티의 플레이어 측 표현 — 추격 대상 본인에게 경고 토스트를 띄우고, 호송 시 끌기 NPC 추종을 중계한다.
/// </summary>
public class PlayerPenaltyView : NetworkBehaviour
{
    private PlayerTowedMotion m_towed;

    [Header("추격 경고")]
    [Tooltip("추격대 출동 시 띄울 문구 — HudTable/Hud.Penalty.ChaseWarning")]
    [SerializeField]
    private LocalizedString m_chaseWarning;

    private readonly NetworkVariable<NetworkObjectReference> m_carrierASynced = new();
    private readonly NetworkVariable<NetworkObjectReference> m_carrierBSynced = new();

    private NpcController m_carrierALocal;
    private NpcController m_carrierBLocal;

    public NpcController CarrierA =>
        IsSpawned ? ResolveCarrier(m_carrierASynced.Value) : m_carrierALocal;

    public NpcController CarrierB =>
        IsSpawned ? ResolveCarrier(m_carrierBSynced.Value) : m_carrierBLocal;

    private static NpcController ResolveCarrier(NetworkObjectReference reference) =>
        reference.TryGet(out NetworkObject obj) ? obj.GetComponent<NpcController>() : null;

    private void Awake()
    {
        m_towed = GetComponent<PlayerTowedMotion>();
    }

    /// <summary>서버 전용 — 대상 오너 클라에 경고를 띄운다. seconds = 표시 시간(지나면 자동 소멸).</summary>
    public void ShowWarning(float seconds)
    {
        if (IsSpawned)
            ShowWarningRpc(seconds);
        else
            ShowLocal(seconds);
    }

    /// <summary>서버 전용 — 포획 확정·타임아웃 등으로 경고를 지운다.</summary>
    public void HideWarning()
    {
        if (IsSpawned)
            HideWarningRpc();
        else
            HideLocal();
    }

    [Rpc(SendTo.Owner)]
    private void ShowWarningRpc(float seconds) => ShowLocal(seconds);

    [Rpc(SendTo.Owner)]
    private void HideWarningRpc() => HideLocal();

    private void ShowLocal(float seconds) => App.UI.Toast?.Show(m_chaseWarning, seconds);

    private void HideLocal() => App.UI.Toast?.Hide(m_chaseWarning);

    /// <summary>관전 오빗 중심을 지정 지점으로 고정한다(관전 진입 신호 겸용). 서버 전용.</summary>
    public void SetSpectatePivot(Vector3 worldPosition, bool cycleToTeammate = false)
    {
        if (IsSpawned)
            SetSpectatePivotRpc(worldPosition, cycleToTeammate);
        else
            ApplySpectatePivot(worldPosition, cycleToTeammate);
    }

    [Rpc(SendTo.Owner)]
    private void SetSpectatePivotRpc(Vector3 worldPosition, bool cycleToTeammate) =>
        ApplySpectatePivot(worldPosition, cycleToTeammate);

    private void ApplySpectatePivot(Vector3 worldPosition, bool cycleToTeammate)
    {
        PlayerSpectateCamera spectate = GetComponent<PlayerSpectateCamera>();
        if (spectate == null)
            return;

        spectate.SetPivotOverride(worldPosition);
        if (cycleToTeammate)
            spectate.SpectateTeammateIfAny();
    }

    /// <summary>오너 클라가 끌기 NPC 둘 사이를 추종하도록 호송을 시작한다. 서버 전용.</summary>
    public void StartCarried(NpcController carrierA, NpcController carrierB, bool collide = false)
    {
        if (carrierA == null || carrierB == null)
            return;

        if (IsSpawned)
        {
            m_carrierASynced.Value = carrierA.NetworkObject;
            m_carrierBSynced.Value = carrierB.NetworkObject;
            StartCarriedRpc(carrierA.NetworkObject, carrierB.NetworkObject, 0f, collide);
        }
        else if (m_towed != null)
        {
            m_carrierALocal = carrierA;
            m_carrierBLocal = carrierB;
            m_towed.BeginEscortFollow(carrierA.transform, carrierB.transform, 0f, collide);
        }
    }

    /// <summary>앵커 하나를 정해진 속도로 따라가게 한다(UFO 흡입). 서버 전용.</summary>
    public void StartTowedBy(NetworkObject anchor, float maxSpeed)
    {
        if (anchor == null)
            return;

        if (IsSpawned)
        {
            m_carrierASynced.Value = default;
            m_carrierBSynced.Value = default;
            StartCarriedRpc(anchor, anchor, maxSpeed, false);
        }
        else if (m_towed != null)
        {
            m_carrierALocal = null;
            m_carrierBLocal = null;
            m_towed.BeginEscortFollow(anchor.transform, anchor.transform, maxSpeed, false);
        }
    }

    /// <summary>서버 전용 — 호송 종료(광장 도착·중단): 추종을 풀어 준다. 직후 서버가 광장 스냅 텔레포트로 보정한다.</summary>
    public void StopCarried()
    {
        if (IsSpawned)
        {
            m_carrierASynced.Value = default;
            m_carrierBSynced.Value = default;
            StopCarriedRpc();
        }
        else if (m_towed != null)
        {
            m_carrierALocal = null;
            m_carrierBLocal = null;
            m_towed.EndEscortFollow();
        }
    }

    [Rpc(SendTo.Owner)]
    private void StartCarriedRpc(
        NetworkObjectReference carrierA,
        NetworkObjectReference carrierB,
        float maxSpeed,
        bool collide
    )
    {
        if (m_towed == null)
            return;
        if (!carrierA.TryGet(out NetworkObject a) || !carrierB.TryGet(out NetworkObject b))
            return;

        m_towed.BeginEscortFollow(a.transform, b.transform, maxSpeed, collide);
    }

    [Rpc(SendTo.Owner)]
    private void StopCarriedRpc()
    {
        if (m_towed != null)
            m_towed.EndEscortFollow();
    }
}
