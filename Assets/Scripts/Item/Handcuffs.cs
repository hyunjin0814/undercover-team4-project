using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
// using Unity.Netcode; // TODO: 네트워크 테스트 시 주석 해제

/// <summary>
/// 수갑 아이템 — 체포 채널링 전용. 좌클릭 홀드로 겨냥한 NPC(레이캐스트 타겟)를 대상으로 잡아
/// 3초 채널링 후 대상 FSM을 Captured로 전이시킨다. 홀드 중 클릭을 떼면 취소된다. (GDD 8-2, #36/#35/#91)
/// 연행 놓기·재연행은 상호작용키(E)로 이관됨 — PlayerInteractor/NpcSubdueInteractable 참고 (#91).
/// 배터리 등 자원 소모는 없다.
/// </summary>
public class Handcuffs : ItemBase
{
    [Header("수갑 설정")]
    [SerializeField]
    private float m_channelSeconds = 3f;

    [Header("체포 판정")]
    [Tooltip("채널링 도중 대상이 이 거리(m)를 벗어나면 체포 실패로 처리한다")]
    [SerializeField]
    private float m_captureRange = 2.5f;

    private bool m_isRestraining;
    private CancellationTokenSource m_cts;
    private PlayerEscorter m_escorter;

    private void Awake()
    {
        // 이 수갑을 든 플레이어의 연행 관리자 — 체포 성공 시 연행을 시작한다 (#59)
        m_escorter = GetComponentInParent<PlayerEscorter>();
    }

    // ---- ItemBase ----

    // TODO: 네트워크 테스트 시 서버 권위로 재검증 (클라 CanUse 결과는 신뢰 불가)
    /// <summary>구속 중이 아닐 때만 사용 가능.</summary>
    public override bool CanUse() => !m_isRestraining;

    // TODO: 네트워크 테스트 시 서버 권위로 실행 (오너 입력 → ServerRpc 요청 → 서버가 채널링/구속 실행 후 결과 동기화)
    public override void Use(GameObject aimTarget)
    {
        if (!CanUse())
        {
            return;
        }

        // 겨냥한 대상에서 NPC를 조회한다 (#35). 대상이 없거나 NPC가 아니면 채널링 자체를 시작하지 않는다.
        NpcController target = ResolveTarget(aimTarget);
        if (target == null)
        {
            Debug.Log("체포할 대상이 없음 (NPC를 겨냥하지 않음)");
            return;
        }

        // 연행 중인 NPC는 대상에서 제외 — 중복 연행·타인의 연행 가로채기 방지 (#59)
        if (target.CurrentState == NpcState.Escorted)
        {
            Debug.Log("이미 연행 중인 대상 — 체포 불가");
            return;
        }

        // 이미 체포된 대상은 채널링 대상이 아니다 — 재연행은 상호작용키(E)로 (#91)
        // 상태는 반드시 동기화된 CurrentState로 읽는다 — StateMachine 값은 서버에서만 갱신됨 (#56)
        if (target.CurrentState == NpcState.Captured)
        {
            Debug.Log("이미 체포된 대상 — 재연행은 상호작용키로");
            return;
        }

        RestrainAsync(target).Forget();
    }

    // ---- 구속 채널링 ----

    // TODO: 네트워크 테스트 시 채널링 타이밍을 서버 권위로 (클라 시간 조작 방지). 구속 결과는 ClientRpc/NetworkVariable로 전파
    private async UniTaskVoid RestrainAsync(NpcController target)
    {
        m_isRestraining = true;
        m_cts = new CancellationTokenSource();

        try
        {
            // 단일 Delay가 아닌 프레임 루프 — 도중 거리 이탈을 즉시 실패시킨다 (#91, 도주형 NPC 대응 GDD 6장)
            // 뗌 취소는 Yield의 토큰 예외(catch)로, 거리 이탈은 return으로 — 취소 사유가 구분된다
            float elapsed = 0f;
            while (elapsed < m_channelSeconds)
            {
                if (target == null || !IsInRange(target))
                {
                    Debug.Log("구속 실패 — 대상이 범위를 벗어남");
                    return;
                }

                await UniTask.Yield(PlayerLoopTiming.Update, m_cts.Token);
                elapsed += Time.deltaTime;
            }

            // 채널링 성공 순간 대상의 반응이 갈린다 (GDD 6-1, #76).
            // 기절 중인 대상은 반응하지 못하고 그대로 연행된다 (테이저 연결고리).
            ReactionType reaction = ResolveReaction(target);
            switch (reaction)
            {
                case ReactionType.Flee:
                    // 뿌리치고 도주 — 근접 제압 홀드 또는 테이저(후속)로만 잡힌다
                    Debug.Log($"체포 실패 — 뿌리치고 도주: {target.name}");
                    target.StartFlee(m_escorter != null ? m_escorter.transform : transform);
                    break;

                case ReactionType.Resist:
                    // 그 자리에서 저항 — 제압 게이지를 깎아야 체포된다
                    Debug.Log($"체포 실패 — 저항 시작: {target.name}");
                    target.StartResist();
                    break;

                default:
                    Debug.Log($"NPC 구속됨: {target.name}");
                    // 체포 성공 즉시 연행 시작 — 플레이어를 따라온다 (#59).
                    // 에스코터가 없는 구성(테스트 등)에서는 기존처럼 그 자리에서 체포 상태 유지
                    if (m_escorter != null)
                        m_escorter.StartEscort(target);
                    else
                        target.StateMachine.ChangeState(NpcState.Captured);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            Debug.Log("구속 취소됨");
        }
        finally
        {
            m_isRestraining = false;
            m_cts?.Dispose();
            m_cts = null;
        }
    }

    /// <summary>좌클릭 뗌 — 진행 중인 구속 채널링을 취소한다 (#91).</summary>
    public override void CancelUse() => CancelRestrain();

    /// <summary>진행 중인 구속 채널링을 취소한다. (이동·피격 등 방해 시 호출)</summary>
    public void CancelRestrain() => m_cts?.Cancel();

    /// <summary>
    /// 채널링 성공 순간의 반응을 정한다. (#76)
    /// 기절(Stunned) 중이거나 신원이 없으면 반응 없이 순응 취급 — 즉시 연행된다.
    /// </summary>
    private static ReactionType ResolveReaction(NpcController target)
    {
        if (target.CurrentState == NpcState.Stunned)
            return ReactionType.Compliant;

        CitizenIdentity identity = target.GetComponent<CitizenIdentity>();
        return identity != null ? identity.Reaction : ReactionType.Compliant;
    }

    // ---- 대상 탐색 ----

    /// <summary>
    /// 겨냥한 대상 GameObject에서 NPC를 조회한다. NPC(NpcController)가 아니면 null.
    /// 콜라이더가 NPC 루트의 자식일 수 있으므로 부모까지 탐색한다 (Scanner.ResolveProfile과 동일 관례, #34/#35).
    /// </summary>
    private static NpcController ResolveTarget(GameObject aimTarget)
    {
        if (aimTarget == null)
        {
            return null;
        }

        return aimTarget.GetComponentInParent<NpcController>();
    }

    private bool IsInRange(NpcController target)
    {
        return (target.transform.position - transform.position).sqrMagnitude
            <= m_captureRange * m_captureRange;
    }

    // ---- 라이프사이클 ----

    // TODO: 네트워크 테스트 시 OnNetworkDespawn에서도 취소 처리 추가
    private void OnDisable()
    {
        CancelRestrain();
    }

    private void OnDestroy()
    {
        m_cts?.Cancel();
        m_cts?.Dispose();
    }
}
