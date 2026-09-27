using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 진압봉 — 조준 방향 부채꼴을 근접 타격해 NPC 체력을 깎는다(GDD 7-4). 동료를 맞히면 아군 오사다.
/// 스윙 모션을 먼저 재생하고 임팩트 프레임에 서버가 판정하며, 클라가 보낸 원점은 서버가 검증한다.
/// </summary>
[RequireComponent(typeof(OwnerFeedback))]
public class Baton : ItemBase, IAimedWeapon
{
    private OwnerFeedback m_feedback;

    private OwnerFeedback Feedback => this.ResolveCapability(ref m_feedback);

    [Header("진압봉 설정")]
    [Tooltip(
        "타격이 닿는 최대 사거리(m). 상호작용 레이(PlayerInteractor.Range)와 무관하게 이 값이 기준이다"
    )]
    [SerializeField]
    private float m_range = 2f;

    [Tooltip("타격 판정 구체의 반경(m). 근접 조준을 관대하게 만드는 값 — 키울수록 빗맞아도 맞는다")]
    [SerializeField]
    private float m_hitRadius = 0.35f;

    [Tooltip(
        "스윙 호의 반각(도). 0이면 정면 한 줄 — 키우면 도주 대상 명중률과 크로스헤어가 켜지는 범위가 함께 넓어진다"
    )]
    [Range(0f, 60f)]
    [SerializeField]
    private float m_arcHalfAngle = 25f;

    [Tooltip(
        "호를 훑는 캐스트 수. 짝수를 넣으면 +1 해서 쓴다 — 가운데 한 줄이 비면 정지 대상이 빠진다"
    )]
    [Range(1, 11)]
    [SerializeField]
    private int m_arcSampleCount = 5;

    [Tooltip(
        "1회 타격이 깎는 NPC 체력. 서버가 자기 프리팹 값을 쓴다 — 클라이언트가 수치를 보내지 않는다"
    )]
    [SerializeField]
    private int m_damage = 34;

    [Tooltip(
        "타격 후 다음 타격까지 대기 시간(초). 명중·빗나감 모두 소모한다 — 빗나가도 대가가 있어야 조준이 의미를 갖는다"
    )]
    [SerializeField]
    private float m_cooldownSeconds = 0.9f;

    [Tooltip(
        "클라가 보낸 조준 원점이 서버가 아는 플레이어 위치에서 이만큼(m) 넘게 떨어져 있으면 거부한다"
    )]
    [SerializeField]
    private float m_originTolerance = 3f;

    private static readonly RaycastHit[] s_hitBuffer = new RaycastHit[64];

    private float m_nextSwingTime;

    private readonly ServerChannel m_swingImpact = new();

    protected readonly struct SwingPower
    {
        public readonly int Damage;
        public readonly bool IsCritical;

        public SwingPower(int damage, bool isCritical)
        {
            Damage = damage;
            IsCritical = isCritical;
        }
    }

    /// <summary>이번 타격의 위력을 정한다. 기본은 프리팹 데미지 고정이다. 서버 전용.</summary>
    protected virtual SwingPower RollSwingPower() => new SwingPower(m_damage, false);

    protected virtual string WeaponLogName => "진압봉";

    protected virtual float ImpactVolumeScale => 1f;

    /// <summary>유효타 적용 후 호출되는 훅. 기본은 무동작. 서버 전용.</summary>
    protected virtual void ServerOnHitLanded(
        NpcController npc,
        PlayerHealth player,
        Vector3 swingDirection,
        Transform holder
    ) { }

    /// <summary>지금 휘두르면 맞을 대상에 조준선이 닿는지 확인한다(크로스헤어 색 예측용).</summary>
    public bool HasValidAimTarget(Vector3 origin, Vector3 direction)
    {
        PlayerInteractor holder = Holder;
        if (holder == null)
        {
            return false;
        }

        return EvaluateSwing(
                origin, direction, holder.transform, holder.LosBlockMask, out _, out _, out _, out _)
            == SwingResult.ValidTarget;
    }

    /// <summary>조준 원점·방향으로 스윙을 서버에 요청한다.</summary>
    public override void Use(GameObject aimTarget)
    {
        PlayerInteractor interactor = Holder;
        if (interactor == null)
        {
            Debug.LogWarning("Baton: PlayerInteractor를 찾지 못함 — 조준 기준 없음", this);
            return;
        }

        Transform aim = interactor.AimOrigin;
        Vector3 origin = aim.position;
        Vector3 direction = aim.forward;

        if (!IsSpawned || IsServer)
        {
            ServerSwing(origin, direction);
            return;
        }

        if (!IsOwner)
        {
            return;
        }

        RequestSwingRpc(origin, direction);
    }

    [Rpc(SendTo.Server)]
    private void RequestSwingRpc(Vector3 origin, Vector3 direction)
    {
        ServerSwing(origin, direction);
    }

    /// <summary>원점을 검증한 뒤 서버 물리로 근접 타격을 판정한다.</summary>
    private void ServerSwing(Vector3 origin, Vector3 direction)
    {
        if (IsSpawned && !IsServer)
        {
            return;
        }

        if (direction.sqrMagnitude < 0.0001f)
        {
            return;
        }

        PlayerInteractor holder = Holder;
        if (holder == null)
        {
            return;
        }

        if (
            (origin - holder.transform.position).sqrMagnitude
            > m_originTolerance * m_originTolerance
        )
        {
            Debug.LogWarning(
                $"Baton: 조준 원점이 플레이어 위치와 너무 멀다 — 타격 거부 (origin={origin})",
                this
            );
            return;
        }

        if (Time.time < m_nextSwingTime || m_swingImpact.IsActive)
        {
            return;
        }

        m_nextSwingTime = Time.time + m_cooldownSeconds;

        PlaySwing();

        Transform holderTransform = holder.transform;
        ServerResolveHitAtImpactAsync(
                holderTransform.InverseTransformPoint(origin),
                holderTransform.InverseTransformDirection(direction),
                holder
            )
            .Forget();
    }

    /// <summary>임팩트 프레임까지 기다린 뒤 상황을 재확인하고 캐스트해 데미지를 넣는다. 서버 전용.</summary>
    private async UniTaskVoid ServerResolveHitAtImpactAsync(
        Vector3 localOrigin,
        Vector3 localDirection,
        PlayerInteractor holder
    )
    {
        ServerChannel.Result result = await m_swingImpact.RunAsync(
            PlayerAnimationDriver.k_swingImpactSeconds
        );

        if (result != ServerChannel.Result.Completed)
        {
            return;
        }

        if (this == null || (IsSpawned && !IsServer))
        {
            return;
        }

        if (holder == null || Holder != holder)
        {
            return;
        }

        Transform holderTransform = holder.transform;
        Vector3 origin = holderTransform.TransformPoint(localOrigin);
        Vector3 direction = holderTransform.TransformDirection(localDirection);

        switch (
            EvaluateSwing(
                origin,
                direction,
                holderTransform,
                holder.LosBlockMask,
                out NpcController target,
                out PlayerHealth playerTarget,
                out BombDevice bombTarget,
                out RaycastHit hit
            )
        )
        {
            case SwingResult.NoHit:
                Feedback?.NotifyOwner($"{WeaponLogName} 빗나감 — 허공");
                return;
            case SwingResult.HitNonTarget:
                App.Game.Fx?.PlayEverywhere(EFx.BatonHitWorld, hit.point, hit.normal);
                Feedback?.NotifyOwner($"{WeaponLogName} 빗나감 — {hit.collider.name}에 맞음");
                return;
            case SwingResult.TargetInvalidState:
                Feedback?.NotifyOwner(
                    playerTarget != null
                        ? $"{WeaponLogName} 무효 — 이미 무력화된 동료 ({playerTarget.name})"
                        : $"{WeaponLogName} 무효 — {(target.CurrentState == NpcState.Dead ? "이미 죽은" : "이미 제압됐거나 페널티 진행 중인")} 대상 ({target.CurrentState})"
                );
                return;
        }

        if (bombTarget != null)
        {
            App.Game.Fx?.PlayEverywhere(EFx.BatonHitMetal, hit.point, hit.normal);
            NotifyHit(false);
            bombTarget.ServerDetonate();
            Feedback?.NotifyOwner($"{WeaponLogName} 명중 — 폭탄이 그 자리에서 터졌다");
            return;
        }

        SwingPower power = RollSwingPower();

        App.Game.Fx?.PlayEverywhere(
            ImpactFxFor(target, playerTarget, power.IsCritical),
            hit.point,
            hit.normal,
            ImpactVolumeScale
        );
        NotifyHit(playerTarget != null);

        if (playerTarget != null)
        {
            playerTarget.TakeDamage(power.Damage, holder.gameObject);
            Feedback?.NotifyOwner(
                $"{WeaponLogName} 명중 — 동료 오사! {playerTarget.name} "
                    + $"(-{power.Damage} → {playerTarget.CurrentHp}/{playerTarget.MaxHp})"
            );
            ServerOnHitLanded(null, playerTarget, direction, holderTransform);
            return;
        }

        target.Health.TakeDamage(power.Damage, holder.gameObject);
        target.Reaction.ServerReactTo(ReactionTrigger.Damage, holderTransform);
        Feedback?.NotifyOwner(
            $"{WeaponLogName} 명중: {target.name} (-{power.Damage} → {target.Health.CurrentHp}/{target.Health.MaxHp})"
        );
        ServerOnHitLanded(target, null, direction, holderTransform);
    }

    /// <summary>버리기 등으로 사용이 끊길 때 대기 중인 임팩트를 취소한다.</summary>
    public override void ServerCancelActiveUse() => m_swingImpact.Cancel();

    public override void OnNetworkDespawn()
    {
        m_swingImpact.Cancel();
        base.OnNetworkDespawn();
    }

    public override void OnDestroy()
    {
        m_swingImpact.Dispose();
        base.OnDestroy();
    }

    /// <summary>스윙 모션을 전 피어에서 재생시킨다.</summary>
    private void PlaySwing()
    {
        if (!IsSpawned)
        {
            ApplySwingFeedback(Holder);
            return;
        }

        PlaySwingRpc();
    }

    [Rpc(SendTo.Everyone)]
    private void PlaySwingRpc()
    {
        ApplySwingFeedback(Holder);
    }

    private static void ApplySwingFeedback(PlayerInteractor holder)
    {
        if (holder == null)
        {
            return;
        }

        PlayerAnimationDriver driver = holder.GetComponentInChildren<PlayerAnimationDriver>();
        if (driver != null)
        {
            driver.TriggerAttack();
        }

        App.Game.Fx?.PlayHere(EFx.BatonSwing, holder.transform.position);
    }

    /// <summary>맞은 대상에 따른 타격 연출 종류를 고른다(로봇/사람).</summary>
    protected virtual EFx ImpactFxFor(NpcController npc, PlayerHealth player, bool critical)
    {
        if (player != null)
        {
            return EFx.BatonHitMetal;
        }

        if (npc == null)
        {
            return EFx.BatonHitWorld;
        }

        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();

        return identity != null && identity.IsAndroidBody ? EFx.BatonHitMetal : EFx.BatonHitFlesh;
    }

    /// <summary>때린 사람에게만 히트마커를 띄운다(아군 오사는 다른 색).</summary>
    private void NotifyHit(bool friendlyFire)
    {
        if (!IsSpawned)
        {
            ApplyHitMarker(friendlyFire);
            return;
        }

        NotifyHitRpc(friendlyFire);
    }

    [Rpc(SendTo.Owner)]
    private void NotifyHitRpc(bool friendlyFire) => ApplyHitMarker(friendlyFire);

    private static void ApplyHitMarker(bool friendlyFire) =>
        App.UI.Crosshair?.ShowHit(friendlyFire);

    private enum SwingResult
    {
        NoHit,
        HitNonTarget,
        TargetInvalidState,
        ValidTarget,
    }

    /// <summary>부채꼴을 훑어 명중 결과를 분류하는 부수효과 없는 순수 판정.</summary>
    private SwingResult EvaluateSwing(
        Vector3 origin,
        Vector3 direction,
        Transform holderRoot,
        LayerMask wallMask,
        out NpcController target,
        out PlayerHealth playerTarget,
        out BombDevice bombTarget,
        out RaycastHit hit
    )
    {
        target = null;
        playerTarget = null;
        bombTarget = null;
        hit = default;

        int samples = m_arcHalfAngle <= 0f ? 1 : Mathf.Max(1, m_arcSampleCount);
        if (samples % 2 == 0)
        {
            samples++;
        }

        Vector3 axis = holderRoot != null ? holderRoot.up : Vector3.up;
        Vector3 forward = direction.normalized;
        int half = samples / 2;

        SwingResult best = SwingResult.NoHit;

        for (int i = 0; i < samples; i++)
        {
            int step = (i + 1) / 2;
            float angle = half == 0 ? 0f : m_arcHalfAngle * step / half * (i % 2 == 0 ? 1f : -1f);

            SwingResult result = EvaluateSwingRay(
                origin,
                Quaternion.AngleAxis(angle, axis) * forward,
                holderRoot,
                wallMask,
                out NpcController rayTarget,
                out PlayerHealth rayPlayerTarget,
                out BombDevice rayBombTarget,
                out RaycastHit rayHit
            );

            if (
                result == SwingResult.NoHit
                || (result != SwingResult.ValidTarget && best != SwingResult.NoHit)
            )
            {
                continue;
            }

            best = result;
            target = rayTarget;
            playerTarget = rayPlayerTarget;
            bombTarget = rayBombTarget;
            hit = rayHit;

            if (best == SwingResult.ValidTarget)
            {
                break;
            }
        }

        return best;
    }

    /// <summary>부채꼴 한 줄을 구체로 캐스트해 NPC·동료를 찾는다.</summary>
    private SwingResult EvaluateSwingRay(
        Vector3 origin,
        Vector3 direction,
        Transform holderRoot,
        LayerMask wallMask,
        out NpcController target,
        out PlayerHealth playerTarget,
        out BombDevice bombTarget,
        out RaycastHit hit
    )
    {
        target = null;
        playerTarget = null;
        bombTarget = null;
        hit = default;

        int count = Physics.SphereCastNonAlloc(
            origin,
            m_hitRadius,
            direction.normalized,
            s_hitBuffer,
            m_range,
            ~0,
            QueryTriggerInteraction.Ignore
        );

        int index = AimOcclusion.FindNearest(origin, s_hitBuffer, count, holderRoot);
        if (index < 0)
        {
            return SwingResult.NoHit;
        }

        hit = s_hitBuffer[index];

        if (hit.distance > 0f && AimOcclusion.IsBlocked(origin, hit.point, hit.collider.transform, wallMask))
        {
            return SwingResult.NoHit;
        }

        NpcController npc = hit.collider.GetComponentInParent<NpcController>();
        if (npc == null)
        {
            return EvaluateNonNpcSwing(hit, out playerTarget, out bombTarget);
        }

        target = npc;
        if (!NpcStateRules.CanBeDamaged(npc))
        {
            return SwingResult.TargetInvalidState;
        }

        return SwingResult.ValidTarget;
    }

    /// <summary>NPC가 아닌 명중을 분류한다 — 동료면 오사, 추격 폭탄이면 즉발, 그 외는 빗나감.</summary>
    private static SwingResult EvaluateNonNpcSwing(
        RaycastHit hit,
        out PlayerHealth playerTarget,
        out BombDevice bombTarget
    )
    {
        bombTarget = null;

        playerTarget = hit.collider.GetComponentInParent<PlayerHealth>();
        if (playerTarget != null)
        {
            PlayerIncapacitation targetIncap = playerTarget.GetComponent<PlayerIncapacitation>();
            bool isDownException = targetIncap != null && targetIncap.IsDowned;
            return playerTarget.IsDamageable || isDownException
                ? SwingResult.ValidTarget
                : SwingResult.TargetInvalidState;
        }

        bombTarget = hit.collider.GetComponentInParent<BombDevice>();
        if (bombTarget != null && bombTarget.CanBeStruck)
        {
            return SwingResult.ValidTarget;
        }

        bombTarget = null;
        return SwingResult.HitNonTarget;
    }
}
