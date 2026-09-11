using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 부활 키트 — Die 상태의 플레이어를 그 자리에서 즉시 부활시키는 소모성 아이템.
/// 남에게 쓰는 좌클릭 경로(<see cref="Use"/>)에 더해, 소지자 본인이 Down·Die 중 E를 홀드해
/// 스스로 일으키는 경로(<see cref="RequestSelfRevive"/>)를 함께 담는다. (#820)
///
/// <b>자가 부활의 채널링이 왜 여기(아이템)에 있는가.</b> Die 중에는 플레이어 오브젝트의 네트워크
/// 오너가 서버로 이관된다(<see cref="PlayerIncapacitation"/>의 ApplyDeathOwnership, #763) — 그래서
/// 플레이어 오브젝트에 붙은 컴포넌트의 SendTo.Owner(게이지·NotifyOwner)는 쓰러진 본인에게 닿지
/// 않는다. 반면 소지품(이 아이템)의 소유권은 사망 중에도 그대로 남는다(사망 경로에 ChangeOwnership이
/// 없다) — 그래서 채널링·게이지·피드백을 플레이어가 아니라 이 아이템에 둔다.
/// (합성 전환 후에도 같다: <see cref="ChannelGauge"/>가 이 아이템 프리팹에 붙으므로 오너 라우팅이
/// 아이템의 소유권을 따른다.)
/// </summary>
[RequireComponent(typeof(ChannelGauge))]
public class ReviveKit : ItemBase
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
                        "ReviveKit: ChannelGauge가 프리팹에 없다 — 프리팹을 열어 추가하고 저장할 것",
                        this
                    );
            }
            return m_gauge;
        }
    }

    // 사거리는 조준·윤곽선과 같은 기준 — PlayerInteractor.Range를 재사용 (#147 패턴, #184).
    private const float k_fallbackRange = 3f; // 테스트 구성 등 PlayerInteractor가 없을 때

    [Header("자가 부활 (#820)")]
    [Tooltip("자가 부활 채널링 시간(초) — 동료 구조(PlayerReviver)와 같은 값으로 시작한다")]
    [SerializeField]
    private float m_selfReviveSeconds = 3f;

    // 자가 부활 채널링 생명주기(CTS 소유·재진입 가드)는 ServerChannel에 위임 (#109, Scanner 관례)
    private readonly ServerChannel m_selfChannel = new();

    /// <summary>
    /// 이 키트로 일으킬 수 있는 대상을 조준 중인지 — 윤곽선·크로스헤어 게이트. (#184)
    /// </summary>
    public override bool CanTarget(GameObject aimTarget) => ResolveTarget(aimTarget) != null;

    // 조준 안내 (#664). CanTarget이 true인 아이템은 문구를 반드시 준다 — 안 주면 윤곽선은 키트 색인데
    // 안내는 E 쪽으로 흘러가 색과 글자가 다른 키를 가리킨다.
    public override LocalizedString TargetPromptLabel(GameObject aimTarget) => InteractPrompts.Revive;

    /// <summary>
    /// 사용 진입점 — 오너의 의도를 서버로 넘긴다. 실제 부활과 소모는 서버가 한다 (Scanner.Use와 같은 구조, #55).
    /// </summary>
    public override void Use(GameObject target)
    {
        PlayerHealth revivable = ResolveTarget(target);
        if (revivable == null)
        {
            // 살아 있는 동료·NPC·자기 자신·빈 조준이 전부 여기로 떨어진다
            Debug.Log("부활 실패: 기능 정지된 동료를 조준해야 한다");
            return;
        }

        // 서버(호스트 포함)·오프라인은 로컬 참조로 바로 실행 (PlayerReviver.RequestBeginRevive 관례)
        if (HasServerAuthority)
        {
            ServerTryRevive(revivable);
            return;
        }

        if (!IsOwner)
            return; // 남의 아이템에서 온 호출 방지 — 서버도 RPC 소유권으로 다시 막는다

        // 원격 클라 → 서버로 대상을 넘기려면 스폰돼 있어야 한다(NetworkObjectReference 제약)
        if (revivable.NetworkObject == null || !revivable.NetworkObject.IsSpawned)
        {
            Debug.LogWarning($"부활 요청 무시 — 대상이 네트워크 스폰되지 않음: {revivable.name}", this);
            return;
        }

        RequestReviveRpc(new NetworkObjectReference(revivable.NetworkObject));
    }

    // 조준 대상에서 '기능 정지된 아군'을 찾는다. 아니면 null. (PlayerReviver.FindAllyTarget과 같은 규칙)
    // 콜라이더가 몸의 자식일 수 있으므로 부모까지 훑는다 — 쓰러진 동안 켜지는 조준 히트박스가
    // 루트의 자식이다(PlayerIncapacitation.m_reviveHitbox).
    private PlayerHealth ResolveTarget(GameObject aimTarget)
    {
        if (aimTarget == null)
            return null;

        PlayerHealth target = aimTarget.GetComponentInParent<PlayerHealth>();
        if (target == null || target == HolderHealth)
            return null; // 자기 자신에게는 쓸 수 없다 — 자가 부활은 RequestSelfRevive의 별도 경로다 (#820)

        // 몸이 회수 불가능한 곳으로 사라졌으면(맨홀 납치, #775) 부활 대상이 아니다
        PlayerIncapacitation targetIncapacitation = target.GetComponent<PlayerIncapacitation>();
        return targetIncapacitation != null && targetIncapacitation.IsRevivable ? target : null;
    }

    // 이 키트를 든 사람의 체력 컴포넌트 — 자기 자신 판정용. 바닥에 놓여 있으면 null.
    private PlayerHealth HolderHealth
    {
        get
        {
            PlayerInteractor holder = Holder;
            return holder != null ? holder.GetComponent<PlayerHealth>() : null;
        }
    }

    [Rpc(SendTo.Server)]
    private void RequestReviveRpc(NetworkObjectReference targetRef)
    {
        if (targetRef.TryGet(out NetworkObject targetObject)
            && targetObject.TryGetComponent(out PlayerHealth target))
        {
            ServerTryRevive(target);
        }
    }

    // ---- 서버 실행 (권위) ----

    /// <summary>
    /// 부활 판정과 소모 — 서버(또는 오프라인) 전용.
    /// 클라 조기검증을 그대로 다시 본다: 변조된 클라가 RPC를 직접 던지는 경로가 있기 때문이다 (#148 관례).
    /// </summary>
    private void ServerTryRevive(PlayerHealth target)
    {
        if (!HasServerAuthority || target == null)
            return;

        PlayerInteractor holder = Holder;
        if (holder == null)
        {
            // 바닥에 놓인 키트로 온 요청 — 든 사람이 없으면 사거리 기준점 자체가 없다
            Debug.LogWarning("[부활 키트] 든 사람이 없는 키트로 부활 요청이 들어왔다 — 거부", this);
            return;
        }

        if (target == holder.GetComponent<PlayerHealth>())
        {
            Debug.LogWarning($"[부활 키트] 자가 부활 시도 거부 — {holder.name}", this);
            return;
        }

        // 쓰러진 사람은 아이템을 쓸 수 없다 — 오너 입력(PlayerItemUser)이 이미 막지만 위조 RPC로 뚫린다
        PlayerIncapacitation userIncapacitation = holder.GetComponent<PlayerIncapacitation>();
        if (userIncapacitation != null && userIncapacitation.IsIncapacitated)
        {
            NotifyOwner("부활 실패 — 무력화 상태에서는 키트를 쓸 수 없다");
            return;
        }

        PlayerIncapacitation targetIncapacitation = target.GetComponent<PlayerIncapacitation>();
        if (targetIncapacitation == null || !targetIncapacitation.IsRevivable)
        {
            // 살아 있는 동료, 이미 일어난 대상, 또는 몸이 회수 불가능한 곳으로 사라진 대상(#775) —
            // 어느 경우든 키트는 소모하지 않는다
            NotifyOwner($"부활 실패 — {target.name}은 부활 대상이 아니다");
            return;
        }

        // 사거리 + 가시선 — 거리만 보면 위조 RPC로 벽 너머 부활이 뚫린다 (#360)
        if (!PlayerInteractor.IsWithinReach(
                holder,
                target.transform,
                PlayerInteractor.RangeOf(holder, k_fallbackRange),
                transform.position))
        {
            NotifyOwner($"부활 실패 — {target.name}이 사거리를 벗어났다");
            return;
        }

        // 부활은 본부 장치와 같은 경로 — HP 부분 회복 + 무력화 해제 (#365와 동일)
        target.ServerRevive();
        holder.GetComponent<PlayerAssistCredit>()?.ServerCreditRescue(); // 정산 "최다 팀원 구조" 집계 (#739)
        NotifyOwner($"부활 완료: {target.name} (부활 키트 소모)");

        // 성공했을 때만 소모한다 — 거부된 사용으로 키트가 사라지면 산 값을 그냥 잃는다
        ServerConsume();
    }

    // ---- 자가 부활 (#820) ----

    /// <summary>
    /// 자가 부활 채널링 시작 요청 — 소지자(오너) 본인이 E를 누르는 순간 PlayerSelfRevive가 호출한다.
    /// PlayerReviver.RequestBeginRevive와 같은 3분기 구조.
    /// </summary>
    public void RequestSelfRevive()
    {
        if (HasServerAuthority)
        {
            ServerBeginSelfRevive();
            return;
        }

        if (!IsOwner)
            return; // 남의 키트에서 온 호출 방지

        BeginSelfReviveRpc();
    }

    /// <summary>자가 부활 채널링 취소 요청 — E를 떼거나 다른 취소 조건이 걸리면 호출한다.</summary>
    public void RequestCancelSelfRevive()
    {
        if (!IsSpawned)
        {
            ServerCancelSelfRevive();
            return;
        }
        if (!IsOwner)
            return;

        CancelSelfReviveRpc();
    }

    [Rpc(SendTo.Server)]
    private void BeginSelfReviveRpc() => ServerBeginSelfRevive();

    [Rpc(SendTo.Server)]
    private void CancelSelfReviveRpc() => ServerCancelSelfRevive();

    private void ServerBeginSelfRevive()
    {
        if (!HasServerAuthority || m_selfChannel.IsActive)
            return;

        PlayerInteractor holder = Holder;
        if (holder == null)
        {
            // 바닥에 놓인 키트로 온 요청 — 든 사람이 없으면 자가 부활 대상 자체가 없다
            Debug.LogWarning("[부활 키트] 든 사람이 없는 키트로 자가 부활 요청이 들어왔다 — 거부", this);
            return;
        }

        // 라운드 종료 뒤에는 부활을 막는다 — PlayerMovement.IsRoundOver와 같은 기준(GameplayFrozen)
        if (App.Game.Round != null && App.Game.Round.GameplayFrozen)
            return;

        PlayerIncapacitation incap = holder.GetComponent<PlayerIncapacitation>();
        if (incap == null || !(incap.IsDowned || incap.IsRevivable))
        {
            // Down도 Die(맨홀로 몸이 사라지지 않은 경우)도 아니면 자가 부활 대상이 아니다
            // (몸이 회수 불가능한 경우는 IsRevivable이 걸러낸다, #775)
            return;
        }

        ServerSelfChannelAsync(holder, incap).Forget();
    }

    private async UniTaskVoid ServerSelfChannelAsync(PlayerInteractor holder, PlayerIncapacitation incap)
    {
        NotifyOwner($"자가 부활 채널링 시작 ({m_selfReviveSeconds}초)");
        // 루프음은 동료 구조(PlayerReviver)와 같은 소리로 통일한다
        Gauge?.Begin(m_selfReviveSeconds, EAudioClip.ReviveLoop);

        // Down 유예 시계를 얼린다 — Die 중이면 IsDowned가 아니라 무동작으로 넘어간다 (#725)
        incap.ServerSetBeingRevived(true, m_selfReviveSeconds);

        ServerChannel.Result result;
        try
        {
            result = await m_selfChannel.RunAsync(
                m_selfReviveSeconds,
                () =>
                    Holder == holder // 채널링 중 약탈(#487)로 손이 바뀌면 즉시 중단
                    && (incap.IsDowned || incap.IsRevivable) // 동료가 먼저 살렸거나 몸을 잃으면 중단
            );
        }
        finally
        {
            // 완료·취소·예외 어떤 경로로 끝나도 게이지 숨김과 유예 시계 해동을 보장한다 (#184, #725)
            Gauge?.End();
            incap.ServerSetBeingRevived(false);
        }

        switch (result)
        {
            case ServerChannel.Result.Canceled:
                NotifyOwner("자가 부활 취소됨");
                return;

            case ServerChannel.Result.OutOfRange:
                NotifyOwner("자가 부활 중단 — 대상이 부활 대상이 아니게 됨");
                return;

            case ServerChannel.Result.Completed:
                break;
        }

        // 채널링 동안 손이 바뀌었으면(약탈 등) 실패 — keepAlive가 대부분 잡지만 완료 프레임과의
        // 경합을 한 번 더 막는다
        if (Holder != holder)
        {
            NotifyOwner("자가 부활 실패 — 키트를 손에서 놓쳤다");
            return;
        }

        PlayerHealth health = holder.GetComponent<PlayerHealth>();
        if (health == null || !(incap.IsDowned || incap.IsRevivable))
        {
            NotifyOwner("자가 부활 실패 — 이미 복구됐거나 부활 대상이 아니다");
            return;
        }

        // 부활이 먼저다 — ServerRevive()의 Recover()가 소유권을 본인에게 되돌려야, 뒤따르는
        // ServerConsume() 안의 SyncHeldItemsRpc(SendTo.Owner)가 서버가 아니라 본인에게 간다 (#763, #820)
        health.ServerRevive();
        NotifyOwner("자가 부활 완료 (부활 키트 소모)");
        ServerConsume();
    }

    private void ServerCancelSelfRevive() => m_selfChannel.Cancel();

    /// <summary>
    /// 서버 권위로 자가 부활 채널링을 즉시 중단한다 — 약탈로 소유권이 넘어가는 경로 등에서
    /// 서버가 직접 호출한다 (ItemBase 계약, Scanner 관례). 남에게 쓰는 경로는 채널링이 없어 영향받지 않는다.
    /// </summary>
    public override void ServerCancelActiveUse() => m_selfChannel.Cancel();

    public override void OnDestroy()
    {
        m_selfChannel.Dispose();
        base.OnDestroy(); // NetworkBehaviour 내부 정리 — 반드시 호출 (R5)
    }
}
