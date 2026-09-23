using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 진압봉 아이템 — 조준 방향을 근접 타격해 맞은 NPC의 체력을 깎는다. (GDD 7-4, #217)
/// 체력이 0에 도달하면 기절(Stunned)하는데, 그 판정은 <see cref="NpcHealth.TakeDamage"/>의
/// 엣지 트리거가 담당하므로 여기서는 데미지만 넣는다 (#366).
///
/// <b>조준 타격</b> — 테이저와 같은 방식이다. PlayerInteractor가 잡아준 대상을 쓰지 않고
/// 조준 방향으로 직접 캐스트해 <b>맞으면 명중, 빗나가면 실패</b>다. 다만 근접 사거리라
/// 점 레이캐스트는 조준이 과하게 빡빡해서 <see cref="Physics.SphereCastNonAlloc"/>로 두께를 준다.
/// 벽·소품이 먼저 맞으면 그대로 빗나간다(가장 가까운 것만 판정 — 엄폐가 성립).
/// <b>동료를 맞추면 아군 오사다</b> — NPC와 같은 데미지가 그대로 HP에 들어간다 (GDD 7-5, #461).
/// 3대째에 다운(유예)이 걸리고, 60초 안에 못 일어나면 기능 정지다(#725). 유예 중인 동료를
/// 한 대 더 때리면 유예를 건너뛰고 곧장 기능 정지가 된다 — 진압봉도 확인사살 수단이다.
/// 때린 쪽에 페널티는 없다 — 쿨다운이 이미 대가다.
///
/// 서버 권위 — 오너가 조준 원점·방향을 보내면 서버가 자기 물리로 캐스트해 판정한다 (#55).
/// 클라가 보낸 원점은 서버가 아는 플레이어 위치와 대조해 검증한다 (원점 위조 = 벽 너머 타격 방지).
///
/// <b>판정은 즉발이 아니라 임팩트 프레임에 일어난다</b> — 좌클릭 순간에 데미지를 넣으면 봉이
/// 아직 뒤로 젖혀져 있는데 NPC가 먼저 맞는 그림이 된다. 그래서 스윙 애니메이션만 먼저 전 피어에
/// 재생시키고, <see cref="PlayerAnimationDriver.k_swingImpactSeconds"/>만큼 기다렸다가 캐스트한다.
/// 1인칭·3인칭 애니메이션이 모두 같은 상수로 임팩트를 맞추므로, 세 곳(내 화면·남의 화면·데미지)이
/// 한 순간에 일어난다.
///
/// 지연 동안의 표적 이동은 보정하지 않는다 — 그래서 판정은 한 줄이 아니라
/// <see cref="m_arcHalfAngle"/>만큼 벌린 부채꼴이다 (#779).
///
/// 홀드 채널링은 아니다 — 좌클릭을 떼도 이미 시작된 스윙은 그대로 들어간다(CancelUse 기본 구현 유지).
/// 취소되는 경우는 스윙 도중 아이템이 손을 떠났을 때뿐이다.
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

    // 5줄이면 최대 사거리에서 줄 간격이 0.51m라 반경 0인 표적에도 틈이 없다
    [Tooltip(
        "호를 훑는 캐스트 수. 짝수를 넣으면 +1 해서 쓴다 — 가운데 한 줄이 비면 정지 대상이 빠진다"
    )]
    [Range(1, 11)]
    [SerializeField]
    private int m_arcSampleCount = 5;

    // 기본값 34는 구 E 제압 타격 1회와 같은 값 — MaxHp 100 기준 3대에 기절한다.
    // E 제압이 제거되면서(#438) 이 값이 NPC 체력을 깎는 유일한 플레이어 타격 수치가 됐다.
    // 같은 수치를 공유하지만 참조하지는 않는다: 저건 'NPC가 제압당할 때 깎이는 양'이고 이건 '이 무기의 위력'이라,
    // 무기 밸런스를 만지려고 NPC 공용 config를 건드리면 제압 홀드까지 함께 움직인다.
    [Tooltip(
        "1회 타격이 깎는 NPC 체력. 서버가 자기 프리팹 값을 쓴다 — 클라이언트가 수치를 보내지 않는다"
    )]
    [SerializeField]
    private int m_damage = 34;

    // 스윙 연출 길이(PlayerAnimationDriver.k_swingSeconds, 0.64초)보다 길게 유지할 것. 짧게 잡으면
    // 애니메이션이 끝나기 전에 다음 스윙이 들어와 계속 처음부터 잘린다 — 데미지는 들어가는데
    // 화면에서는 휘두르다 마는 그림이 된다. 남는 0.26초는 다음 타격 전의 뜸이다(연출 아님).
    [Tooltip(
        "타격 후 다음 타격까지 대기 시간(초). 명중·빗나감 모두 소모한다 — 빗나가도 대가가 있어야 조준이 의미를 갖는다"
    )]
    [SerializeField]
    private float m_cooldownSeconds = 0.9f;

    // 근거는 Taser.m_originTolerance와 동일: 카메라 높이(1.6m) + 스프린트 8m/s × 지연 150ms(1.2m) ≈ 2.8 → 3.0.
    // 줄이면 핑 높은 플레이어의 정상 타격이 조용히 거부된다.
    [Tooltip(
        "클라가 보낸 조준 원점이 서버가 아는 플레이어 위치에서 이만큼(m) 넘게 떨어져 있으면 거부한다"
    )]
    [SerializeField]
    private float m_originTolerance = 3f;

    // 캐스트 결과 버퍼 — 서버 판정과 오너 크로스헤어(HasValidAimTarget)가 함께 쓰지만 공유해도 안전하다.
    // 둘 다 메인 스레드에서 동기적으로 돌고, 결과를 호출 안에서 즉시 꺼내 쓴 뒤 버퍼를 붙들지 않는다.
    // (호스트에서는 두 경로가 같은 프레임에 돌 수 있지만 겹쳐 실행되지는 않는다)
    // 16칸은 모자랐다 (#779) — 래그돌 본까지 세면 플레이어 13 + NPC 12개고, 넘치면 정렬 없이 잘린다.
    // 포화 경고는 못 넣는다 — 크로스헤어와 공유되는 순수 판정이라 매 프레임 찍힌다.
    private static readonly RaycastHit[] s_hitBuffer = new RaycastHit[64];

    // 다음 타격이 가능해지는 시각. 판정자가 서버 하나뿐이라 동기화하지 않는다 (서버 전용 상태).
    // 아이템 인스턴스에 붙어 있으므로 버리고 다시 주워도 쿨다운이 따라간다.
    private float m_nextSwingTime;

    // 스윙 시작 → 임팩트 대기. 홀드 채널링은 아니지만 필요한 건 같다(서버 전용 타이머, CTS 소유,
    // 재진입 가드, 디스폰 시 정리) — 그 골격이 이미 ServerChannel에 있어 그대로 재사용한다 (#109).
    private readonly ServerChannel m_swingImpact = new();

    // ---- 하위 클래스 확장 시임 (#815/#816) ----

    /// <summary>
    /// 이 타격의 위력 — 데미지와 '대박' 여부를 함께 정한다. 굴림 한 번이 데미지와 연출(<see cref="ImpactFxFor"/>)
    /// 둘 다를 결정해야 하므로 하나로 묶는다 (거대 뿅망치, #816) — 따로 굴리면 두 번째 굴림에서 결과가
    /// 어긋나 '터졌는데 평타 소리'가 나는 경합이 생긴다.
    /// </summary>
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

    /// <summary>
    /// 이번 타격의 위력을 정한다 — 서버 전용, 유효타가 확정된 뒤 <b>타격당 한 번만</b> 불린다.
    /// 기본 구현은 프리팹 데미지 고정(대박 없음). 확률로 위력이 갈리는 하위 클래스가 이걸 재정의한다.
    /// </summary>
    protected virtual SwingPower RollSwingPower() => new SwingPower(m_damage, false);

    /// <summary>
    /// 서버 판정 로그·토스트에 실을 무기 이름 — 콘솔·토스트 전용 문자열이라 지역화 대상이 아니다.
    /// </summary>
    protected virtual string WeaponLogName => "진압봉";

    /// <summary>
    /// 임팩트 연출의 소리 볼륨 배율(0~1) — 서버 전용, 유효타 연출을 전파할 때 한 번 읽는다.
    /// 기본은 1(카탈로그 볼륨 그대로). 타격 세기가 매번 다른 하위만 재정의한다 (홈런 진압봉 차지, #998).
    /// </summary>
    protected virtual float ImpactVolumeScale => 1f;

    /// <summary>
    /// 유효타가 실제로 적용된 뒤 — 서버 전용, 데미지·반응(ServerReactTo)보다 <b>뒤</b>에 불린다.
    /// 기본 구현은 무동작. 데미지 외의 추가 효과(발사 등)를 얹는 하위 클래스가 재정의한다 (홈런 진압봉, #815).
    /// </summary>
    /// <param name="npc">맞은 NPC. 동료를 맞췄으면 null.</param>
    /// <param name="player">맞은 동료. NPC를 맞췄으면 null.</param>
    /// <param name="swingDirection">타격 시점의 조준 방향(월드) — 발사 벡터 계산 등에 쓴다.</param>
    /// <param name="holder">때린 사람.</param>
    protected virtual void ServerOnHitLanded(
        NpcController npc,
        PlayerHealth player,
        Vector3 swingDirection,
        Transform holder
    ) { }

    // ---- ItemBase ----

    // CanTarget은 재정의하지 않는다(기본 false) — 조준 대상 윤곽선(#184)의 기준인 상호작용 레이(3m)와
    // 실제 사거리(2m)가 어긋나 그대로 쓰면 2~3m에서 "윤곽선은 떴는데 안 맞는" 상태가 된다.
    // 대신 IAimedWeapon으로 크로스헤어 색만 구동한다 (테이저와 같은 방식, 아래 HasValidAimTarget).
    /// <summary>
    /// 조준선이 지금 휘두르면 맞을 대상(NPC·동료)에 닿는지 확인한다 — 크로스헤어 색 예측용.
    /// 서버 타격 판정(EvaluateSwing)과 같은 함수를 써서 결과가 어긋나지 않는다.
    /// 동료를 겨눠도 켜진다 — 오사 위험을 미리 보여주기 위해서다.
    /// </summary>
    public bool HasValidAimTarget(Vector3 origin, Vector3 direction)
    {
        // 소지자 계층은 자기 몸을 캐스트에서 걸러내는 데 필요하다 — 원점이 카메라(캡슐 안)라
        // 걸러내지 않으면 자기 콜라이더가 distance 0으로 먼저 잡힌다. (EvaluateSwing 주석 참고)
        PlayerInteractor holder = Holder;
        if (holder == null)
        {
            return false;
        }

        return EvaluateSwing(
                origin, direction, holder.transform, holder.LosBlockMask, out _, out _, out _, out _)
            == SwingResult.ValidTarget;
    }

    /// <summary>
    /// 아이템 사용 진입점. 겨냥 대상(aimTarget)은 쓰지 않는다 — 조준 방향으로 직접 휘두르기 때문이다.
    /// 오너는 조준 원점·방향만 넘기고, 명중 판정은 전적으로 서버가 수행한다.
    /// </summary>
    public override void Use(GameObject aimTarget)
    {
        // 조준 기준은 든 플레이어의 AimOrigin(카메라) — 아이템은 줍기/버리기로 부모가 바뀌므로
        PlayerInteractor interactor = Holder;
        if (interactor == null)
        {
            Debug.LogWarning("Baton: PlayerInteractor를 찾지 못함 — 조준 기준 없음", this);
            return;
        }

        Transform aim = interactor.AimOrigin;
        Vector3 origin = aim.position;
        Vector3 direction = aim.forward;

        // 서버(호스트)·오프라인은 로컬 참조로 즉시 실행
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

    // ---- 서버 판정 ----

    /// <summary>
    /// 서버에서 근접 타격을 판정한다. 클라가 보낸 원점·방향은 신뢰할 수 없으므로,
    /// 원점이 서버가 아는 플레이어 위치 근처인지 확인한 뒤 서버 물리로 캐스트한다.
    /// (원점 검증이 없으면 위조 RPC로 맵 어디서든, 벽 너머로도 때릴 수 있다)
    /// </summary>
    private void ServerSwing(Vector3 origin, Vector3 direction)
    {
        // 스폰 전(오프라인)엔 IsServer 캐시가 아직 갱신되지 않아 false일 수 있으므로,
        // "스폰된 상태에서 서버가 아닐 때"만 차단한다. (Taser.ServerFire 관례)
        if (IsSpawned && !IsServer)
        {
            return;
        }

        if (direction.sqrMagnitude < 0.0001f)
        {
            return; // 방향이 0벡터면 캐스트를 만들 수 없다 (위조·직렬화 사고 방어)
        }

        PlayerInteractor holder = Holder;
        if (holder == null)
        {
            return; // 아무에게도 안 들린 아이템이 휘둘러질 수는 없다
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

        // 쿨다운은 유효성 검사를 전부 통과한 뒤에 본다 — 위조·사고로 거부된 요청이 쿨다운을 소모하면
        // 정상 타격이 엉뚱하게 막힌다. 여기부터는 "실제로 휘둘렀다"로 취급한다.
        // 임팩트 대기 중인 스윙이 있으면 함께 막는다 — 쿨다운(0.9초)이 임팩트 지연(0.3초)보다 길어
        // 정상 경로에선 걸릴 일이 없지만, 쿨다운을 줄이면 대기가 겹쳐 앞 타격이 유실된다.
        if (Time.time < m_nextSwingTime || m_swingImpact.IsActive)
        {
            return; // 연타 입력이라 로그를 남기지 않는다 — 남기면 좌클릭 연타마다 콘솔이 도배된다
        }

        // 명중 여부와 무관하게 소모한다 — 빗나감에 대가가 없으면 조준할 이유가 사라진다.
        m_nextSwingTime = Time.time + m_cooldownSeconds;

        // 애니메이션 먼저, 판정은 임팩트 프레임에. 빗나가도 휘두르는 모습은 보여야 한다. (#217)
        PlaySwing();

        // 조준을 소지자 로컬로 환산해 들고 간다 — 대기하는 0.3초 동안 플레이어가 걷거나 돌면
        // 월드 좌표로 굳혀 둔 조준선은 몸에서 떨어져 나가, 화면에서는 정면을 후려치는데 판정은
        // 0.3초 전 허공에서 나가는 일이 생긴다. 몸에 붙여 두면 스윙이 캐릭터를 따라간다
        // (= 애니메이션이 보여주는 것과 같다). 이 기준은 PlayerLook이 마우스 yaw로 돌리는 그
        // transform이라 스윙 도중에도 좌우로는 따라간다 — 굳는 것은 pitch뿐이다.
        Transform holderTransform = holder.transform;
        ServerResolveHitAtImpactAsync(
                holderTransform.InverseTransformPoint(origin),
                holderTransform.InverseTransformDirection(direction),
                holder
            )
            .Forget();
    }

    /// <summary>
    /// 임팩트 프레임까지 기다렸다가 캐스트해 데미지를 넣는다 — 서버 전용. (#217)
    /// 대기 동안 상황이 바뀔 수 있으므로(디스폰·버리기·소지자 소멸) 판정 직전에 다시 확인한다.
    /// </summary>
    private async UniTaskVoid ServerResolveHitAtImpactAsync(
        Vector3 localOrigin,
        Vector3 localDirection,
        PlayerInteractor holder
    )
    {
        ServerChannel.Result result = await m_swingImpact.RunAsync(
            PlayerAnimationDriver.k_swingImpactSeconds
        );

        // 취소 = 스윙 도중 아이템이 손을 떠났거나 디스폰됐다. 없던 일이므로 조용히 끝낸다.
        if (result != ServerChannel.Result.Completed)
        {
            return;
        }

        // 대기 동안 파괴·디스폰됐을 수 있다 (Unity의 == null 오버로드가 파괴된 객체를 잡아준다).
        if (this == null || (IsSpawned && !IsServer))
        {
            return;
        }

        // 소지자가 사라졌거나, 스윙 도중 아이템을 버렸거나 남에게 넘어갔으면 이 타격은 무효다 —
        // 손을 떠난 봉이 때리는 그림은 없다.
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
                // 허공은 연출이 없다 — 이미 나간 스윙음이 '휘두르긴 했다'를 말해 주고 있다.
                Feedback?.NotifyOwner($"{WeaponLogName} 빗나감 — 허공");
                return;
            case SwingResult.HitNonTarget:
                App.Game.Fx?.PlayEverywhere(EFx.BatonHitWorld, hit.point, hit.normal);
                Feedback?.NotifyOwner($"{WeaponLogName} 빗나감 — {hit.collider.name}에 맞음");
                return;
            case SwingResult.TargetInvalidState:
                // 아무 연출도 내지 않는다 — 때릴 수 없는 대상이므로 허공(NoHit)과 같은 취급이다.
                // 먼지든 타격음이든 내면 그만큼은 '때려졌다'로 읽히는데, 여기서 일어나는 일은 없다.
                // 휘두른 것 자체는 이미 나간 스윙 모션·스윙음이 말해 준다.
                // NPC 쪽 사유는 이제 <b>상태 하나</b>다 — "이미 쓰러진" 갈래는 그 게이트를 걷으면서
                // 함께 사라졌다(#571, EvaluateSwing 주석). 쓰러진 대상은 이제 유효타다.
                Feedback?.NotifyOwner(
                    playerTarget != null
                        ? $"{WeaponLogName} 무효 — 이미 무력화된 동료 ({playerTarget.name})"
                        : $"{WeaponLogName} 무효 — {(target.CurrentState == NpcState.Dead ? "이미 죽은" : "이미 제압됐거나 페널티 진행 중인")} 대상 ({target.CurrentState})"
                );
                return;
        }

        // 추격 폭탄을 맞췄다 — <b>그 자리에서 즉발한다</b> (#399). 밀어내기는 없어졌다: 굴러오는
        // 폭탄을 봉으로 쳐서 옮긴다는 그림 자체가 읽히지 않았다. 때린 사람은 폭심 바로 옆이라
        // 피해를 온전히 받는다 — 오조작의 대가를 그 자리에서 치른다.
        // 연출은 금속 타격이다 — 폭탄은 로봇이고, 맞은 것 자체는 유효타라 히트마커가 떠야 한다 (#478).
        if (bombTarget != null)
        {
            App.Game.Fx?.PlayEverywhere(EFx.BatonHitMetal, hit.point, hit.normal);
            NotifyHit(false);
            bombTarget.ServerDetonate();
            Feedback?.NotifyOwner($"{WeaponLogName} 명중 — 폭탄이 그 자리에서 터졌다");
            return;
        }

        // 위력 굴림은 여기서 <b>단 한 번</b> — 데미지와 연출이 같은 결과를 보게 한다 (#816).
        // 허공·비대상·무효타는 위에서 이미 return했으므로 굴림이 낭비되지 않는다.
        SwingPower power = RollSwingPower();

        // 유효타 — 임팩트 연출은 전 피어, 히트마커는 때린 사람에게만.
        App.Game.Fx?.PlayEverywhere(
            ImpactFxFor(target, playerTarget, power.IsCritical),
            hit.point,
            hit.normal,
            ImpactVolumeScale
        );
        NotifyHit(playerTarget != null);

        // 동료를 맞췄다 — 아군 오사 (#461). NPC와 같은 데미지를 그대로 넣고, HP 0이 되면
        // PlayerHealth.SetHp가 다운 유예(IncapacitationCause.Down)까지 이어준다 — 여기서 따로 할 일이 없다.
        // NPC 경로의 ServerReactTo(반격·도주 전환)는 플레이어에게 해당 없다.
        // 대박(#816)도 예외 없이 이 경로를 그대로 탄다 — 특별 취급하지 않는다는 뜻이지 즉사를
        // 보장한다는 뜻은 아니다. 건강한 동료가 맞으면 PlayerHealth.SetHp가 다운(60초 유예,
        // IncapacitationCause.Down)에 먼저 들어간다 — 9999든 1이든 HP를 0으로 떨어뜨리는 첫 타격의
        // 결과는 같다. 이미 유예 중인 동료를 맞히면(확인사살, ServerFinishOff)에서만 그 자리에서
        // 완전 사망한다 — 팀 결정(2026-08-24)은 '아군도 봐주지 않는다'이지 '즉시 죽는다'가 아니다.
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

        // 때린 사람을 가해자로 넘긴다 — 맞은 즉시 이 사람에게 반격·도주하고(#400),
        // 이 타격으로 기절하면 깨어난 뒤에도 이 사람에게서 도망친다 (#269).
        target.Health.TakeDamage(power.Damage, holder.gameObject);
        target.Reaction.ServerReactTo(ReactionTrigger.Damage, holderTransform); // 맞은 즉시 반응 (#400)
        Feedback?.NotifyOwner(
            $"{WeaponLogName} 명중: {target.name} (-{power.Damage} → {target.Health.CurrentHp}/{target.Health.MaxHp})"
        );
        ServerOnHitLanded(target, null, direction, holderTransform);
    }

    // ---- 정리 ----

    /// <summary>
    /// 서버가 소유권 회수를 동반해 사용을 끊는 경로(버리기) — 대기 중인 임팩트를 취소한다.
    /// 손을 떠난 봉이 뒤늦게 때리는 것을 막는다.
    /// (좌클릭 뗌은 CancelUse라 여기로 오지 않는다 — 시작된 스윙은 끝까지 간다. 의도된 것)
    /// </summary>
    public override void ServerCancelActiveUse() => m_swingImpact.Cancel();

    public override void OnNetworkDespawn()
    {
        m_swingImpact.Cancel(); // 디스폰된 뒤에 판정이 남아 돌지 않게
        base.OnNetworkDespawn();
    }

    public override void OnDestroy()
    {
        m_swingImpact.Dispose();
        base.OnDestroy();
    }

    // ---- 스윙 애니메이션 (전 피어) ----

    /// <summary>
    /// 스윙 모션을 전 피어에서 재생시킨다 — 서버 판정 지점에서 한 번만 호출된다. (#217)
    /// Down/Crouch/Airborne처럼 동기화값을 폴링할 수 없는 일회성 이벤트라 브로드캐스트로 처리한다.
    /// (드라이버 주석의 "트리거 대신 bool" 원칙은 지속 상태 얘기다 — 서버가 명시적으로 한 번 쏘는
    ///  RPC는 유실·중복이 없으므로 트리거가 맞다)
    /// </summary>
    private void PlaySwing()
    {
        if (!IsSpawned)
        {
            ApplySwingFeedback(Holder); // 오프라인 — RPC 경로가 없다
            return;
        }

        PlaySwingRpc();
    }

    [Rpc(SendTo.Everyone)]
    private void PlaySwingRpc()
    {
        // 소지자를 인자로 싣지 않는 이유: 아이템의 부착 부모는 NetworkObject 부모 동기화로 전 피어가
        // 동일하므로, 각 피어가 자기 계층에서 찾는 편이 참조 직렬화보다 싸고 어긋날 여지가 없다.
        ApplySwingFeedback(Holder);
    }

    // 모션 + 스윙음. 둘을 같은 함수에 두는 이유는 같은 순간에 일어나야 하기 때문이다 —
    // 소리를 임팩트 시점으로 미루면 휘두르는 동작과 어긋난다.
    // 드라이버는 Animator가 붙은 모델 쪽에 있을 수도, 루트에 있을 수도 있다 — 소지자 루트에서 아래로 찾는다
    // (GetComponentInChildren은 자기 자신도 포함하므로 두 배치 모두 걸린다).
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

        // 소지자 위치에서 낸다 — 봉 끝이 아니라 몸 기준이면 충분하고(둘의 거리가 1m 안쪽이다),
        // 아이템이 손에 붙는 시점과 무관하게 항상 유효한 좌표다.
        // 이 함수는 이미 전 피어에서 도는 스윙 RPC 안이라 전파(PlayEverywhere)가 아니라 로컬 재생이다.
        App.Game.Fx?.PlayHere(EFx.BatonSwing, holder.transform.position);
    }

    // ---- 타격 연출 (#478) ----

    /// <summary>
    /// 맞은 대상에 따른 타격 연출 — 로봇은 깡, 사람은 퍽. (#478)
    /// 먼지·소리 조합과 전 피어 전파는 <see cref="FxManager"/>가 가져갔다 (#532) — 여기서는 무엇을 맞혔는지만 고른다.
    /// <paramref name="critical"/>은 대박 여부(#816) — 기본 구현은 무시한다. 로봇/사람 구분이 아니라
    /// '무엇이 일어났는가'가 다른 소리를 원하는 하위 클래스(뿅망치)가 재정의해서 쓴다.
    /// </summary>
    /// <remarks>
    /// <b>클라이언트가 스스로 판단하지 않고 서버가 정해 실어 보낸다.</b> <see cref="OfficialRecords.CitizenType"/>은
    /// 전 피어에 동기화되므로(<see cref="CitizenData"/>) 각 피어가 다시 조회해도 같은 답이 나오지만,
    /// 그러면 프로필 미배정 같은 예외 처리가 피어 수만큼 흩어진다. 판정이 이미 서버 단독이라
    /// 결과만 얹어 보내는 편이 갈래가 한 곳에 남는다.
    ///
    /// 종족을 소리로 드러내도 정보가 새지 않는다 — 위조(#223)는 표시 이름·문양만 오염시키고
    /// 표시 타입(<c>m_typeView</c>)은 건드리지 않으므로, 소리와 스캔 결과가 어긋나는 일이 없다.
    /// </remarks>
    protected virtual EFx ImpactFxFor(NpcController npc, PlayerHealth player, bool critical)
    {
        if (player != null)
        {
            return EFx.BatonHitMetal; // 동료는 전원 로봇 경찰이다 (GDD 세계관)
        }

        if (npc == null)
        {
            return EFx.BatonHitWorld;
        }

        // 판정은 CitizenIdentity가 갖는다 — 피격 신음(NpcHurtVoice)이 같은 기준을 봐야 하기 때문이다.
        // 프로필 미배정 구간을 사람으로 보는 것도 그쪽 규칙이다.
        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();

        return identity != null && identity.IsAndroidBody ? EFx.BatonHitMetal : EFx.BatonHitFlesh;
    }

    /// <summary>
    /// 명중을 때린 사람에게만 알린다 — 크로스헤어 히트마커. 아군 오사는 색이 다르다. (#478/#461)
    /// 소리로는 동료(로봇)와 안드로이드 NPC가 둘 다 깡이라 구분되지 않으므로, 이것이 오사를 드러내는 유일한 수단이다.
    /// </summary>
    private void NotifyHit(bool friendlyFire)
    {
        if (!IsSpawned)
        {
            ApplyHitMarker(friendlyFire); // 오프라인 — RPC 경로가 없다
            return;
        }

        NotifyHitRpc(friendlyFire);
    }

    [Rpc(SendTo.Owner)]
    private void NotifyHitRpc(bool friendlyFire) => ApplyHitMarker(friendlyFire);

    // 로컬 HUD라 오너 스폰 전이거나 HUD 없는 구성에서는 null이다 (App.UI.Crosshair 주석).
    private static void ApplyHitMarker(bool friendlyFire) =>
        App.UI.Crosshair?.ShowHit(friendlyFire);

    // ---- 조준 판정 ----

    private enum SwingResult
    {
        NoHit,
        HitNonTarget,
        TargetInvalidState,
        ValidTarget,
    }

    /// <summary>
    /// 부채꼴을 훑어 명중 결과를 분류한다. 유효타가 있으면 그 줄이 이기고,
    /// 없으면 조준축에 가장 가까운 줄이 남는다. 부수효과 없는 순수 판정으로
    /// 유지할 것 — 크로스헤어 예측(HasValidAimTarget)이 매 프레임 공유한다.
    /// </summary>
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

        // 반각이 0이면 줄을 늘려도 같은 캐스트가 반복될 뿐이다. 그 외에는 홀수로 올린다 —
        // 가운데 한 줄이 비면 정지 대상이 빠진다.
        int samples = m_arcHalfAngle <= 0f ? 1 : Mathf.Max(1, m_arcSampleCount);
        if (samples % 2 == 0)
        {
            samples++;
        }

        // 소지자 up을 축으로 돌린다 — 지금은 루트가 기울지 않아 월드 up과 같지만, 몸 기준이 이 판정의
        // 의미(스윙이 캐릭터를 따라간다)에 맞다.
        Vector3 axis = holderRoot != null ? holderRoot.up : Vector3.up;
        Vector3 forward = direction.normalized;
        int half = samples / 2;

        SwingResult best = SwingResult.NoHit;

        for (int i = 0; i < samples; i++)
        {
            // 가운데(0) → 좌 → 우 → 더 좌 → 더 우 순서로 번져 나간다
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

            // 허공은 알려줄 것이 없다. 그 외에는 유효타만 앞의 결과를 밀어낼 수 있다 —
            // 밀어내지 못하면 먼저 온(= 조준축에 더 가까운) 줄이 그대로 남는다.
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
                break; // 가운데부터 훑었으니 더 잘 조준된 유효타는 남아 있지 않다
            }
        }

        return best;
    }

    /// <summary>
    /// 호를 이루는 한 줄의 판정 — 반경 m_hitRadius 구체로 NPC·동료를 찾는다.
    /// 후보 선정은 AimOcclusion.FindNearest, 가림 재확인은 IsBlocked가 맡는다
    /// (SphereCast는 벽에 붙으면 겹침 히트가 섞이기 때문).
    /// </summary>
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

        // 휘두른 본인(과 그가 들고 있는 것들)은 제외 계층으로 넘긴다 — 위 주석의 자기 겹침 문제.
        int index = AimOcclusion.FindNearest(origin, s_hitBuffer, count, holderRoot);
        if (index < 0)
        {
            return SwingResult.NoHit;
        }

        hit = s_hitBuffer[index];

        // 후보가 실제로 벽 뒤에 있는지 다시 확인한다 — SphereCast가 벽에 바짝 붙으면
        // 벽을 distance 0으로 잡아 뒤로 밀려날 수 있어서다. hit.distance가 0 이하면
        // hit.point가 신뢰할 수 없는 값(0,0,0)이라 이 재확인을 건너뛴다.
        if (hit.distance > 0f && AimOcclusion.IsBlocked(origin, hit.point, hit.collider.transform, wallMask))
        {
            return SwingResult.NoHit;
        }

        // 콜라이더가 NPC 루트의 자식일 수 있으므로 부모까지 탐색한다 (Taser.EvaluateAim과 동일 관례).
        // 벽·소품을 맞췄으면 그대로 빗나감이고, 동료를 맞췄으면 아군 오사다 (#461).
        NpcController npc = hit.collider.GetComponentInParent<NpcController>();
        if (npc == null)
        {
            return EvaluateNonNpcSwing(hit, out playerTarget, out bombTarget);
        }

        // 피해 게이트를 데미지 전에 본다 — TakeDamage도 같은 규칙으로 피해를 무시하지만(#366/#292),
        // 여기서 먼저 걸러야 오너에게 "무효" 사유를 알려줄 수 있다. 두 곳의 기준은 반드시 같아야 한다.
        //
        // 스턴 게이트가 아니라 <b>타격 게이트</b>다 — #292에서 스턴이 오버레이가 되며 전 상태에 걸리게
        // 되면서 둘이 갈라졌다(구 CanBeStunned → CanBeDamaged). 타격까지 함께 열면 연행 중인 NPC를
        // 때려 기절시켜 신병에서 빼내는 우회가 생기므로, 진압봉은 좁은 쪽(타격)을 따른다.
        // 상태 enum이 아니라 NpcController를 넘긴다 — 납치범 예외(#371)가 거기 들어 있고,
        // 크로스헤어와 실제 타격이 같은 함수를 봐야 "떴는데 안 맞음"이 생기지 않는다.
        target = npc;
        if (!NpcStateRules.CanBeDamaged(npc))
        {
            return SwingResult.TargetInvalidState;
        }

        // <b>쓰러진 대상도 때릴 수 있다</b> (#571) — 여기 있던 "이미 쓰러졌으면 무효타" 게이트를 걷었다.
        //
        // 그 게이트의 근거는 둘이었고 <b>둘 다 더는 성립하지 않는다</b>:
        //  · "쓰러진 대상은 HP가 이미 0이라 피해가 0인데 명중이 뜬다" — 그 한 대가 곧 확인사살이다 (#916).
        //  · "테이저로 기절한 대상은 만피라 누워 있는 채 계속 깎인다" — 사실이지만 <b>이제 그게 의도다.</b>
        //
        // <b>테이저(<c>Taser.EvaluateAim</c>)의 같은 게이트는 그대로 둔다</b> — 두 무기의 근거가 여기서
        // 갈린다. 테이저는 무력화가 목적이라 이미 무력화된 대상에 쏘는 것이 진짜 무효타이고
        // (테이저 기절은 시간이 짧아 EnterStunned가 물러난다), 진압봉은 체력을 깎는 것이 목적이라
        // 쓰러진 대상에도 할 일이 남아 있다.
        //
        // 죽은 대상은 위 <see cref="NpcStateRules.CanBeDamaged"/>가 막으므로 여기까지 오지 않는다.

        return SwingResult.ValidTarget;
    }

    /// <summary>
    /// NPC가 아닌 것을 맞췄을 때의 분류 — 동료면 아군 오사, 추격 폭탄이면 즉발,
    /// 그 외(벽·소품)는 빗나감. 소지자 자신은 AimOcclusion.FindNearest가 제외 루트로
    /// 걸러내므로 후보에 오르지 않는다. 유예(Down) 중인 동료만 예외로 유효타 처리한다.
    /// </summary>
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
            // 유예(Down)만 예외 — IsDamageable은 CurrentHp>0을 요구해 다운을 걸러내지만, 확인사살은 의도된 동작이다(#725)
            //
            // IsTargetable이 아니라 IsDamageable을 본다 — <b>날아가는 동료도 유효타</b>다. 저쪽은
            // 표적 선정용이라 비행을 빼고, 그래서 예전에는 날아가는 사람에게 헛스윙이 났다.
            PlayerIncapacitation targetIncap = playerTarget.GetComponent<PlayerIncapacitation>();
            bool isDownException = targetIncap != null && targetIncap.IsDowned;
            return playerTarget.IsDamageable || isDownException
                ? SwingResult.ValidTarget
                : SwingResult.TargetInvalidState;
        }

        // 추격 폭탄 — 때리면 데미지가 아니라 즉발이다 (#399). 카운트다운 전이거나 이미 터진 폭탄은
        // 그냥 소품이라 빗나감으로 둔다(TargetInvalidState가 아니다 — "무효"라고 알려줄 만한 오조작이 아니다).
        bombTarget = hit.collider.GetComponentInParent<BombDevice>();
        if (bombTarget != null && bombTarget.CanBeStruck)
        {
            return SwingResult.ValidTarget;
        }

        bombTarget = null;
        return SwingResult.HitNonTarget;
    }
}
