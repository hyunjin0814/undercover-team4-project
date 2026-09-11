using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 홈런 진압봉 — <see cref="Baton"/>의 조준·서버 판정을 그대로 쓰되, 데미지가 아니라 <b>발사</b>가 본체인
/// 근접 무기. (GDD 7-4, #815) 프리팹 <c>m_damage</c>는 0이라 죽이지 않고, 맞은 대상은 래그돌 임펄스로
/// 날아간다 — NPC는 <see cref="NpcKnockback.ServerLaunchRagdoll"/>로, 동료는
/// <see cref="IncapacitationCause.Launched"/>로 <see cref="PlayerRagdoll"/>에 진입해서.
///
/// <b>누르는 순간이 아니라 떼는 순간이 타격이다</b> (#998) — 오래 모을수록 멀리 날린다. 부모는 즉발이므로
/// <see cref="Use"/>는 충전만 시작하고 실제 스윙(<c>base.Use</c>)은 <see cref="CancelUse"/>가 낸다.
/// </summary>
[RequireComponent(typeof(ChannelGauge))]
public class HomeRunBaton : Baton
{
    private ChannelGauge m_gauge;

    // 프리팹 직렬화에 의존하므로 lazy로 잡는다 — RequireComponent는 기존 프리팹 자산을 소급 보정하지 않는다.
    private ChannelGauge Gauge
    {
        get
        {
            if (m_gauge == null)
            {
                m_gauge = GetComponent<ChannelGauge>();
                if (m_gauge == null)
                    Debug.LogError(
                        "HomeRunBaton: ChannelGauge가 프리팹에 없다 — 프리팹을 열어 추가하고 저장할 것",
                        this
                    );
            }
            return m_gauge;
        }
    }

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

    // 비행 시간(≈ 2 × 상승속도 / 중력)보다 넉넉히 길어야 한다 — 짧으면 NpcStun.Tick이 먼저 끝나
    // 아직 공중인 몸을 그 자리에서 세운다.
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

    // 충전 시작 시각(오너 로컬, 음수 = 충전 중 아님).
    private float m_chargeStartTime = -1f;

    // 서버가 아는 '다음 스윙의 충전량'(0~1). 초기값 1은 충전 통보가 없는 경로(테스트 씬 직접 호출) 대비.
    private float m_serverChargeRatio = 1f;

    protected override string WeaponLogName => "홈런 진압봉";

    // ---- 차지 (#998) ----

    /// <summary>
    /// 좌클릭을 누른 순간 — 휘두르지 않고 충전만 시작한다. 뗌 없이 또 눌렸으면(충전 중 버리기 등)
    /// 무기가 굳지 않게 새로 시작한다.
    /// </summary>
    public override void Use(GameObject aimTarget)
    {
        m_chargeStartTime = Time.time;
        // 모으는 본인에게만 들리는 2D 루프 — 게이지와 같은 경로라 시작·종료가 이미 짝지어져 있다 (#998)
        Gauge?.Begin(m_maxChargeSeconds, EAudioClip.HomeRunCharge);
    }

    /// <summary>
    /// 좌클릭을 뗀 순간 — 모인 만큼의 위력으로 지금 휘두른다. 충전량은 스윙 요청 직전에 보낸다:
    /// 신뢰 RPC는 순서가 보장돼 서버에 항상 먼저 도착한다.
    ///
    /// 장착 전환·커서 풀림도 이 경로다(PlayerItemUser) — 놓음과 뗌을 구분할 방법이 ItemBase 계약에
    /// 없어 그때도 힘이 나간다. 데미지 0이라 그대로 둔다. 다만 <b>다운 중이면 버린다</b>(아래).
    /// </summary>
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

        // 모으는 도중 다운되면 모은 것은 버린다 — PlayerItemUser는 누름(HandleUseItem)만 무력화로
        // 막고 뗌은 막지 않으므로, 여기서 보지 않으면 쓰러진 채로 스윙이 나간다. 즉발이던 시절에는
        // 누름 게이트 하나로 충분했지만, 타격이 뗌으로 옮겨 오면서 이 자리가 필요해졌다.
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
        m_serverChargeRatio = Mathf.Clamp01(charge); // 위조 방어 — 상한은 프리팹 값이다
    }

    private float ChargedLaunchSpeed =>
        Mathf.Lerp(m_minLaunchSpeed, m_launchSpeed, m_serverChargeRatio);

    /// <summary>
    /// 타격음은 하나로 통일한다 — 이 무기가 알리는 것은 맞은 쪽이 무엇인지가 아니라 <b>날아갔다</b>는
    /// 사실이다. (<see cref="ToyHammer"/>도 같은 선택)
    /// </summary>
    protected override EFx ImpactFxFor(NpcController npc, PlayerHealth player, bool critical) =>
        EFx.HomeRunHit;

    /// <summary>
    /// 타격음도 충전량을 따라간다 (#998) — 살짝 친 것과 끝까지 모은 것이 같은 소리로 나면
    /// 게이지를 보지 않는 주변 사람에게는 세기가 전혀 드러나지 않는다. 소리 종류는 그대로 하나다.
    /// </summary>
    protected override float ImpactVolumeScale =>
        Mathf.Lerp(m_minHitVolume, 1f, m_serverChargeRatio);

    /// <summary>유효타 확정 뒤 — NPC·동료 모두 래그돌로 발사한다. 데미지·반응보다 뒤에 불린다. (#815)</summary>
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
            // 수직에 가깝게 조준 — 수평 성분이 없으면 소지자가 보는 방향을 폴백으로 쓴다.
            horizontal = new Vector3(holder.forward.x, 0f, holder.forward.z);
        }
        horizontal.Normalize();

        // 충전량은 세기만 바꾼다 — 발사각·기절 시간은 최대 충전 기준 그대로다. 약하게 날린 몸은 비행이
        // 더 짧으므로 "누운 시간 ≥ 비행 시간"이 저절로 유지된다.
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

    // ---- 동료 비행 (#815) ----
    //
    // 상태(Launched)는 서버 권위 동기화값이라 전 피어가 PlayerRagdoll.PollRagdollCause로 알아서 진입한다.
    // RPC가 필요한 것은 임펄스 하나뿐이고, 물리는 오너(피격당한 클라)가 돌리므로 소유권은 옮기지 않는다.
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
