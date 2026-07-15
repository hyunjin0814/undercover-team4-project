using UnityEngine;

/// <summary>
/// 수갑 아이템 — 좌클릭 홀드로 겨냥한 NPC(레이캐스트 타겟)를 대상으로 체포를 요청한다. (GDD 8-2, #36/#35/#91)
/// 실제 채널링·사거리·반응 판정·연행은 서버 권위이며 PlayerEscorter가 수행한다 (#118).
/// 이 컴포넌트는 오너 클라의 "의도"만 담당한다 — 누름(Use)에 체포 요청, 뗌(CancelUse)에 취소 요청.
/// (채널링을 여기서 직접 돌리면 서버 가드에 막혀 클라 검거가 조용히 실패한다 — #88 머지 회귀의 원인)
/// 연행 놓기·재연행은 상호작용키(E)로 이관됨 — PlayerInteractor/NpcSubdueInteractable 참고 (#91).
/// 배터리 등 자원 소모는 없다.
/// </summary>
public class Handcuffs : ItemBase
{
    /// <summary>
    /// 이 수갑을 든 플레이어의 검거·연행 관리자 — 체포/연행 요청을 서버로 넘긴다 (#59/#118).
    /// 아이템이 독립 NetworkObject가 되어(#88) 줍기/버리기로 부모가 바뀌므로 캐시하지 않고
    /// 사용 시점마다 부모 계층에서 해석한다 (버려진 상태에선 부모가 없어 null).
    /// </summary>
    private PlayerEscorter Escorter => GetComponentInParent<PlayerEscorter>();

    // ---- ItemBase ----

    /// <summary>수갑은 언제나 사용 시도 가능 — 실제 가부(채널링 중복 등)는 서버가 판정한다.</summary>
    public override bool CanUse() => true;

    public override void Use(GameObject aimTarget)
    {
        PlayerEscorter escorter = Escorter;
        if (escorter == null)
        {
            Debug.LogWarning("Handcuffs: PlayerEscorter를 찾지 못함 — 검거 불가", this);
            return;
        }

        // 겨냥한 대상에서 NPC를 조회한다 (#35). 대상이 없거나 NPC가 아니면 요청 자체를 보내지 않는다.
        NpcController target = ResolveTarget(aimTarget);
        if (target == null)
        {
            Debug.Log("체포할 대상이 없음 (NPC를 겨냥하지 않음)");
            return;
        }

        // 체포 가능 상태 판정은 NpcStateRules 단일 기준 — 서버 가드·조준 피드백과 동일 (#184)
        // 상태는 반드시 동기화된 CurrentState로 읽는다 — StateMachine 값은 서버에서만 갱신됨 (#56)
        if (!NpcStateRules.IsCapturable(target.CurrentState))
        {
            // 재연행은 상호작용키(E)로 (#91) / 연행 중 대상은 가로채기 방지 (#59)
            Debug.Log(
                target.CurrentState == NpcState.Captured
                    ? "이미 체포된 대상 — 재연행은 상호작용키로"
                    : "이미 연행 중인 대상 — 체포 불가"
            );
            return;
        }

        // 채널링·사거리·반응 판정은 서버가 수행한다 — 여기서는 요청만 넘긴다.
        // 이 로그 뒤에 서버의 "[서버 판정] 구속 채널링 시작"이 안 오면 RPC 경로 문제다 (진단용)
        Debug.Log($"좌클릭 — 체포 채널링 요청: {target.name}");
        escorter.RequestCapture(target);
    }

    /// <summary>Use()의 조기 검증과 동일 기준 — 체포 채널링이 실제로 시작될 수 있는 대상인지. (#184)</summary>
    public override bool CanTarget(GameObject aimTarget)
    {
        NpcController target = ResolveTarget(aimTarget);
        if (target == null || !NpcStateRules.IsCapturable(target.CurrentState))
            return false;

        // 연행 중엔 체포 불가 (서버 ServerBeginCapture 가드와 동일) — 오너 클라는 동기화 플래그로 판정
        PlayerEscorter escorter = Escorter;
        return escorter != null && !escorter.IsEscorting;
    }

    /// <summary>좌클릭 뗌 — 진행 중인 체포 채널링 취소를 서버에 요청한다 (#91).</summary>
    public override void CancelUse() => CancelRestrain();

    /// <summary>진행 중인 구속 채널링을 취소한다. (이동·피격 등 방해 시 호출) — 서버 채널링에 취소를 요청한다.</summary>
    public void CancelRestrain() => Escorter?.CancelCapture();

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

    // ---- 라이프사이클 ----

    public override void OnNetworkDespawn()
    {
        // 디스폰(버리기·파괴) 시 진행 중인 서버 채널링도 취소 요청 (#88)
        CancelRestrain();
    }

    private void OnDisable()
    {
        // 장착 해제·비활성 시 진행 중인 서버 채널링도 취소 요청
        CancelRestrain();
    }
}
