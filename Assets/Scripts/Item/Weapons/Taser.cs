using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 테이저건 — 조준 방향으로 전극을 쏴 맞은 NPC(또는 동료)를 기절시킨다(GDD 8-3).
/// 서버가 원점을 검증하고 레이캐스트로 판정하며, 발사 후 충전은 원형 게이지로 표시한다.
/// </summary>
[RequireComponent(typeof(ChannelGauge))]
[RequireComponent(typeof(OwnerFeedback))]
public class Taser : ItemBase, IAimedWeapon
{
    private OwnerFeedback m_feedback;

    private OwnerFeedback Feedback => this.ResolveCapability(ref m_feedback);

    private ChannelGauge m_gauge;

    private ChannelGauge Gauge => this.ResolveCapability(ref m_gauge);

    [Header("테이저 설정")]
    [Tooltip("전극이 날아가는 최대 사거리(m). 상호작용 레이(PlayerInteractor.Range)와 무관하게 이 값이 기준이다")]
    [SerializeField]
    private float m_range = 8f;

    [Tooltip("클라가 보낸 조준 원점이 서버가 아는 플레이어 위치에서 이만큼(m) 넘게 떨어져 있으면 거부한다 — 카메라 높이(1.6m) + 이동 지연 여유")]
    [SerializeField]
    private float m_originTolerance = 3f;

    [Tooltip("발사 후 다음 발사까지 대기 시간(초). 명중·빗나감 모두 소모한다 — 빗나가도 대가가 있어야 조준이 의미를 갖는다")]
    [SerializeField]
    private float m_cooldownSeconds = 5f;

    [Tooltip("동료를 맞췄을 때 기절 시간(초). 쿨다운(m_cooldownSeconds)보다 짧게 둘 것 — 같거나 길면 일어나는 순간 다시 쏴서 한 명을 영구히 묶을 수 있다")]
    [SerializeField]
    private float m_playerStunSeconds = 5f;

    [Tooltip("조준 보정 반지름(m). 선이 빗나갔을 때 이 굵기의 구로 다시 훑어 사람을 찾는다 — 지연으로 어긋난 만큼을 흡수한다. 0이면 보정 없음")]
    [Min(0f)]
    [SerializeField]
    private float m_aimAssistRadius = 0.25f;

    private const float k_assistProbeRadius = 0.05f;

    private float m_nextFireTime;

    private static readonly RaycastHit[] s_aimBuffer = new RaycastHit[128];

    /// <summary>조준 원점·방향으로 사격을 서버에 요청한다.</summary>
    public override void Use(GameObject aimTarget)
    {
        PlayerInteractor interactor = Holder;
        if (interactor == null)
        {
            Debug.LogWarning("Taser: PlayerInteractor를 찾지 못함 — 조준 기준 없음", this);
            return;
        }

        Transform aim = interactor.AimOrigin;
        Vector3 origin = aim.position;
        Vector3 direction = aim.forward;

        if (!IsSpawned || IsServer)
        {
            ServerFire(origin, direction);
            return;
        }

        if (!IsOwner)
        {
            return;
        }

        RequestFireRpc(origin, direction);
    }

    [Rpc(SendTo.Server)]
    private void RequestFireRpc(Vector3 origin, Vector3 direction)
    {
        ServerFire(origin, direction);
    }

    /// <summary>원점을 검증한 뒤 서버 물리로 조준 사격을 판정한다.</summary>
    private void ServerFire(Vector3 origin, Vector3 direction)
    {
        if (IsSpawned && !IsServer)
        {
            return;
        }

        if (direction.sqrMagnitude < 0.0001f)
        {
            return;
        }

        if (!IsOriginPlausible(origin))
        {
            Debug.LogWarning($"Taser: 조준 원점이 플레이어 위치와 너무 멀다 — 사격 거부 (origin={origin})", this);
            return;
        }

        if (Time.time < m_nextFireTime)
        {
            Feedback?.NotifyOwner($"테이저 충전 중 — {m_nextFireTime - Time.time:F1}초 남음");
            return;
        }

        m_nextFireTime = Time.time + m_cooldownSeconds;

        Gauge?.Begin(m_cooldownSeconds, EAudioClip.None);

        App.Game.Fx?.PlayEverywhere(EFx.TaserFire, origin);

        switch (
            EvaluateAim(
                origin, direction, out NpcController target,
                out PlayerIncapacitation playerTarget, out RaycastHit hit))
        {
            case AimResult.NoHit:
                Feedback?.NotifyOwner("테이저 빗나감 — 허공");
                return;
            case AimResult.HitNonTarget:
                Feedback?.NotifyOwner($"테이저 빗나감 — {hit.collider.name}에 맞음");
                return;
            case AimResult.TargetInvalidState:
                App.Game.Fx?.PlayEverywhere(EFx.TaserHit, hit.point);
                Feedback?.NotifyOwner(
                    playerTarget != null
                        ? $"테이저 무효 — 이미 무력화된 동료 ({playerTarget.name})"
                        : $"테이저 무효 — 이미 기절한 대상 ({target.name})");
                return;
        }

        App.Game.Fx?.PlayEverywhere(EFx.TaserHit, hit.point);

        if (playerTarget != null)
        {
            playerTarget.ServerStun(m_playerStunSeconds);
            Feedback?.NotifyOwner($"테이저 명중 — 동료 오사! {playerTarget.name} ({m_playerStunSeconds}초 기절)");
            return;
        }

        PlayerInteractor shooter = Holder;
        target.Stun.EnterStunned(
            shooter != null ? shooter.transform : null,
            null,
            NpcStunCause.Taser
        );
        Feedback?.NotifyOwner($"테이저 명중: {target.name} ({target.Stun.StunSeconds}초 기절)");
    }

    private enum AimResult { NoHit, HitNonTarget, TargetInvalidState, ValidTarget }

    /// <summary>선 판정 후 빗나가면 보정 판정으로 명중 결과를 분류한다(서버·크로스헤어 공용).</summary>
    private AimResult EvaluateAim(
        Vector3 origin,
        Vector3 direction,
        out NpcController target,
        out PlayerIncapacitation playerTarget,
        out RaycastHit hit)
    {
        AimResult precise = EvaluatePrecise(origin, direction, out target, out playerTarget, out hit);

        if (precise != AimResult.NoHit && precise != AimResult.HitNonTarget)
            return precise;

        if (m_aimAssistRadius <= 0f || direction.sqrMagnitude < 0.0001f)
            return precise;

        AimResult assisted = EvaluateAssist(
            origin,
            direction,
            out NpcController assistTarget,
            out PlayerIncapacitation assistPlayer,
            out RaycastHit assistHit);
        if (assisted == AimResult.NoHit)
            return precise;

        target = assistTarget;
        playerTarget = assistPlayer;
        hit = assistHit;
        return assisted;
    }

    /// <summary>선이 빗나갔을 때 굵은 구로 다시 훑어 사람을 찾는다(엄폐는 뚫지 않는다).</summary>
    private AimResult EvaluateAssist(
        Vector3 origin,
        Vector3 direction,
        out NpcController target,
        out PlayerIncapacitation playerTarget,
        out RaycastHit hit)
    {
        target = null;
        playerTarget = null;
        hit = default;

        int count = Physics.SphereCastNonAlloc(
            origin, m_aimAssistRadius, direction.normalized, s_aimBuffer, m_range, ~0,
            QueryTriggerInteraction.Ignore);

        PlayerInteractor holder = Holder;
        Transform holderRoot = holder != null ? holder.transform : null;
        int bestIndex = -1;
        float bestDistance = float.PositiveInfinity;

        int limit = Mathf.Min(count, s_aimBuffer.Length);
        for (int i = 0; i < limit; i++)
        {
            Collider collider = s_aimBuffer[i].collider;
            if (collider == null)
                continue;

            float distance = s_aimBuffer[i].distance;
            if (distance <= 0f || distance >= bestDistance)
                continue;

            if (holderRoot != null && collider.transform.IsChildOf(holderRoot))
                continue;

            if (collider.GetComponentInParent<NpcController>() == null
                && collider.GetComponentInParent<PlayerIncapacitation>() == null)
                continue;

            bestDistance = distance;
            bestIndex = i;
        }

        if (bestIndex < 0)
            return AimResult.NoHit;

        hit = s_aimBuffer[bestIndex];

        if (AimOcclusion.IsEnvironmentBlocked(
                origin, hit.point, ~0, k_assistProbeRadius, holderRoot))
        {
            hit = default;
            return AimResult.NoHit;
        }

        NpcController npc = hit.collider.GetComponentInParent<NpcController>();
        if (npc == null)
            return EvaluatePlayerAim(hit, out playerTarget);

        target = npc;
        return npc.Stun.IsStunned ? AimResult.TargetInvalidState : AimResult.ValidTarget;
    }

    /// <summary>보정 없이 레이캐스트로 명중 결과를 분류한다.</summary>
    private AimResult EvaluatePrecise(
        Vector3 origin,
        Vector3 direction,
        out NpcController target,
        out PlayerIncapacitation playerTarget,
        out RaycastHit hit)
    {
        target = null;
        playerTarget = null;
        hit = default;

        if (direction.sqrMagnitude < 0.0001f)
            return AimResult.NoHit;

        int count = Physics.RaycastNonAlloc(
            origin, direction.normalized, s_aimBuffer, m_range, ~0, QueryTriggerInteraction.Ignore);

        PlayerInteractor holder = Holder;
        int index = AimOcclusion.FindNearest(
            origin, s_aimBuffer, count, holder != null ? holder.transform : null);
        if (index < 0)
            return AimResult.NoHit;

        hit = s_aimBuffer[index];

        NpcController npc = hit.collider.GetComponentInParent<NpcController>();
        if (npc == null)
            return EvaluatePlayerAim(hit, out playerTarget);

        target = npc;

        if (npc.Stun.IsStunned)
            return AimResult.TargetInvalidState;

        return AimResult.ValidTarget;
    }

    private AimResult EvaluatePlayerAim(RaycastHit hit, out PlayerIncapacitation playerTarget)
    {
        playerTarget = hit.collider.GetComponentInParent<PlayerIncapacitation>();
        if (playerTarget == null)
            return AimResult.HitNonTarget;

        return playerTarget.IsIncapacitated ? AimResult.TargetInvalidState : AimResult.ValidTarget;
    }

    /// <summary>조준선이 스턴 가능한 대상에 닿는지 확인한다(크로스헤어 색 예측용).</summary>
    public bool HasValidAimTarget(Vector3 origin, Vector3 direction)
        => EvaluateAim(origin, direction, out _, out _, out _) == AimResult.ValidTarget;

    /// <summary>클라가 보낸 조준 원점이 소지자 위치 근처인지 확인한다(원점 위조 방어).</summary>
    private bool IsOriginPlausible(Vector3 origin)
    {
        PlayerInteractor holder = Holder;
        if (holder == null)
        {
            return false;
        }

        return (origin - holder.transform.position).sqrMagnitude
            <= m_originTolerance * m_originTolerance;
    }

    /// <summary>소유권 이전 시 서버에서 충전 게이지를 내린다.</summary>
    public override void ServerCancelActiveUse() => Gauge?.End();

    /// <summary>재장착 시 아직 충전 중이면 남은 만큼 게이지를 이어 띄운다.</summary>
    public override void OnEquipped()
    {
        if (!IsSpawned || IsServer)
        {
            ServerReportCharge();
            return;
        }

        RequestChargeGaugeRpc();
    }

    [Rpc(SendTo.Server)]
    private void RequestChargeGaugeRpc() => ServerReportCharge();

    private void ServerReportCharge()
    {
        float remaining = m_nextFireTime - Time.time;
        if (remaining <= 0f)
            return;

        Gauge?.Begin(m_cooldownSeconds, m_cooldownSeconds - remaining, EAudioClip.None);
    }
}
