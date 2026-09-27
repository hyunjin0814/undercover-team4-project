using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 홈런 진압봉 — 데미지 없이 맞은 대상을 래그돌로 날려 보내는 근접 무기(GDD 7-4).
/// 좌클릭을 누르면 충전하고, 떼는 순간 모인 위력으로 휘두른다.
/// </summary>
[RequireComponent(typeof(ChannelGauge))]
public class HomeRunBaton : Baton
{
    private ChannelGauge m_gauge;

    private ChannelGauge Gauge => this.ResolveCapability(ref m_gauge);

    [Header("홈런 진압봉 (#815)")]
    [Tooltip("최대 충전 시 발사 수평 속도(m/s)")]
    [SerializeField]
    private float m_launchSpeed = 14f;

    [Header("차지 (#998)")]
    [Tooltip("최대 충전까지 좌클릭을 누르고 있어야 하는 시간(초). 더 눌러도 세지지 않는다")]
    [Min(0.05f)]
    [SerializeField]
    private float m_maxChargeSeconds = 1.2f;

    [Tooltip("무충전(툭 치기) 발사 수평 속도(m/s). 충전량에 따라 m_launchSpeed까지 선형으로 오른다")]
    [Min(0f)]
    [SerializeField]
    private float m_minLaunchSpeed = 4f;

    [Tooltip("무충전 타격음 볼륨 배율. 최대 충전이 1이고 여기까지 선형으로 내려간다 — 0으로 두면 툭 치기가 무음이다")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_minHitVolume = 0.35f;

    [Tooltip("상승 속도 = 수평 속도 × 이 값. 1.0이 45°로 사거리 최대 (BombBlastProfile과 같은 노브)")]
    [Range(0f, 3f)]
    [SerializeField]
    private float m_liftRatio = 1f;

    [Tooltip("착지 후까지 누워 있는 시간(초). 비행 시간보다 넉넉히 길게 잡을 것")]
    [SerializeField]
    private float m_stunSeconds = 6f;

    [Tooltip("동료에게 적용할 래그돌 임펄스 배율 — 1.0에서 출발해 플레이 테스트로 조정할 것")]
    [Range(0f, 2f)]
    [SerializeField]
    private float m_playerLaunchScale = 1f;

    [Tooltip("비행 상태의 서버 최대시간(초) — 오너의 정착 통보가 안 올 때의 안전장치")]
    [SerializeField]
    private float m_launchMaxSeconds = 6f;

    private float m_chargeStartTime = -1f;

    private float m_serverChargeRatio = 1f;

    protected override string WeaponLogName => "홈런 진압봉";

    /// <summary>좌클릭을 누른 순간 휘두르지 않고 충전을 시작한다.</summary>
    public override void Use(GameObject aimTarget)
    {
        m_chargeStartTime = Time.time;
        Gauge?.Begin(m_maxChargeSeconds, EAudioClip.HomeRunCharge);
    }

    /// <summary>좌클릭을 뗀 순간 충전량을 보내고 스윙한다. 다운 중이면 버린다.</summary>
    public override void CancelUse()
    {
        if (m_chargeStartTime < 0f)
        {
            base.CancelUse();
            return;
        }

        float charge = Mathf.Clamp01((Time.time - m_chargeStartTime) / m_maxChargeSeconds);
        m_chargeStartTime = -1f;
        Gauge?.End();

        PlayerInteractor holder = Holder;
        if (holder != null)
        {
            PlayerIncapacitation incap = holder.GetComponent<PlayerIncapacitation>();
            if (incap != null && incap.IsIncapacitated)
            {
                return;
            }
        }

        if (!IsSpawned || IsServer)
        {
            m_serverChargeRatio = charge;
        }
        else if (IsOwner)
        {
            SubmitChargeRpc(charge);
        }

        base.Use(null);
    }

    [Rpc(SendTo.Server)]
    private void SubmitChargeRpc(float charge)
    {
        m_serverChargeRatio = Mathf.Clamp01(charge);
    }

    private float ChargedLaunchSpeed =>
        Mathf.Lerp(m_minLaunchSpeed, m_launchSpeed, m_serverChargeRatio);

    /// <summary>대상과 무관하게 홈런 전용 타격 연출을 돌려준다.</summary>
    protected override EFx ImpactFxFor(NpcController npc, PlayerHealth player, bool critical) =>
        EFx.HomeRunHit;

    protected override float ImpactVolumeScale =>
        Mathf.Lerp(m_minHitVolume, 1f, m_serverChargeRatio);

    /// <summary>유효타 확정 뒤 — NPC·동료 모두 래그돌로 발사한다. 데미지·반응보다 뒤에 불린다.</summary>
    protected override void ServerOnHitLanded(
        NpcController npc,
        PlayerHealth player,
        Vector3 swingDirection,
        Transform holder
    )
    {
        Vector3 horizontal = new Vector3(swingDirection.x, 0f, swingDirection.z);
        if (horizontal.sqrMagnitude < 0.0001f)
        {
            horizontal = new Vector3(holder.forward.x, 0f, holder.forward.z);
        }
        horizontal.Normalize();

        float launchSpeed = ChargedLaunchSpeed;

        if (npc != null)
        {
            Vector3 impulse = horizontal * launchSpeed + Vector3.up * (launchSpeed * m_liftRatio);
            npc.Knockback.ServerLaunchRagdoll(impulse, m_stunSeconds, holder);
            return;
        }

        if (player != null)
        {
            Vector3 impulse =
                horizontal * (launchSpeed * m_playerLaunchScale)
                + Vector3.up * (launchSpeed * m_liftRatio * m_playerLaunchScale);
            ServerLaunchPlayer(player, impulse);
        }
    }

    private void ServerLaunchPlayer(PlayerHealth player, Vector3 impulse)
    {
        PlayerIncapacitation incap = player.GetComponent<PlayerIncapacitation>();
        incap?.ServerLaunch(m_launchMaxSeconds);

        if (!IsSpawned)
        {
            player.GetComponent<PlayerRagdoll>()?.EnterRagdoll(impulse);
            return;
        }

        LaunchPlayerRpc(impulse, RpcTarget.Single(player.OwnerClientId, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void LaunchPlayerRpc(Vector3 impulse, RpcParams rpcParams)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.LocalClient.PlayerObject == null)
        {
            return;
        }

        nm.LocalClient.PlayerObject.GetComponent<PlayerRagdoll>()?.EnterRagdoll(impulse);
    }
}
