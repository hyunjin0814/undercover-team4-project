using UnityEngine;

/// <summary>
/// 밧줄 아이템 — 기본 검거 수단. 겨냥한 NPC를 좌클릭 홀드로 묶어 누운 채 질질 끌고 다닌다. (#269 → #369)
/// 같은 좌클릭이 대상 상태로 갈린다: 체포되어 멈춘 대상(Captured)에겐 '풀어주기'다.
/// 놓았던 대상을 다시 끄는 것은 상호작용키(E) — NpcSubdueInteractable이 담당한다.
/// 실제 채널링·사거리·반응 판정·끌기는 서버 권위이며 PlayerEscorter가 수행한다 (수갑과 동일한 허브 패턴, #59/#118).
/// 끌기 중엔 손이 묶여 다른 아이템을 쓸 수 없고, 한 번에 1명만 확보할 수 있다.
/// 밧줄은 소모되지 않는다 — 대상에 남지 않으므로 풀기 판정도 상태만 본다.
/// </summary>
public class Rope : ItemBase
{
    /// <summary>이 밧줄을 든 플레이어의 연행 허브 — 요청을 서버로 넘긴다. (Handcuffs와 동일 관례, #88)</summary>
    private PlayerEscorter Escorter => GetComponentInParent<PlayerEscorter>();

    public override bool CanUse() => true;

    public override void Use(GameObject aimTarget)
    {
        PlayerEscorter escorter = Escorter;
        if (escorter == null)
        {
            Debug.LogWarning("Rope: PlayerEscorter를 찾지 못함 — 사용 불가", this);
            return;
        }

        NpcController target = ResolveTarget(aimTarget);
        if (target == null)
        {
            Debug.Log("밧줄을 쓸 대상이 없음 (NPC를 겨냥하지 않음)");
            return;
        }

        // 같은 좌클릭이 대상에 따라 네 갈래로 갈린다 (#390). 판정 기준은 서버 가드·조준 피드백과 동일 (#184):
        //   내 줄이 걸림 → 내 줄만 풀기 / 놓아둔 체포 → 풀어주기 / 남이 끄는 중 → 합류 / 나머지 → 새로 묶기

        // 내 줄이 걸린 대상은 푼다 — 끌리는 중이어도 자기 줄은 뺄 수 있다(줄다리기에서 손 떼기).
        // 놓아둔 체포(Captured)는 남이 묶었어도 풀어준다 — 오검거 구제·방해 수단 (수갑 해제 #290의 자리).
        if (escorter.CanUnrope(target))
        {
            escorter.RequestUnrope(target);
            return;
        }

        // 남이 끌고 가는 중이면 밧줄을 덧걸어 합류한다 — 기존 끌기는 끊기지 않는다(탈취 차단).
        // 새 대상을 묶는 것과 같은 자원(밧줄 1개)을 쓰므로 진입 판정도 같은 경로다.
        if (!NpcStateRules.CanJoinDrag(target.CurrentState)
            && !NpcStateRules.CanArrest(target.CurrentState))
        {
            Debug.Log("밧줄로 묶을 수 없는 대상 (이미 신병 확보됨 / 페널티 진행 중)");
            return;
        }

        if (escorter.IsAtRopeCapacity)
        {
            Debug.Log("소지한 밧줄을 전부 쓰고 있음 — 먼저 풀거나 인계할 것");
            return;
        }

        escorter.RequestRopeDrag(target);
    }

    /// <summary>Use()의 조기 검증과 동일 기준 — 조준 피드백(윤곽선)용. 묶기·풀기 어느 쪽이든 반응한다. (#184)</summary>
    public override bool CanTarget(GameObject aimTarget)
    {
        NpcController target = ResolveTarget(aimTarget);
        if (target == null)
            return false;

        PlayerEscorter escorter = Escorter;
        if (escorter == null)
            return false;

        // 풀기(내 줄 / 놓아둔 체포)는 용량과 무관하게 언제나 가능하다.
        if (escorter.CanUnrope(target))
            return true;

        // 새로 묶기·합류는 소지한 밧줄 개수까지만 (서버 가드 CanBeginRopeDrag와 단일 기준, #184/#390)
        return !escorter.IsAtRopeCapacity
            && (NpcStateRules.CanArrest(target.CurrentState)
                || NpcStateRules.CanJoinDrag(target.CurrentState));
    }

    /// <summary>좌클릭 뗌 — 진행 중인 묶기/풀기 채널링 취소를 서버에 요청한다. (Handcuffs와 동일, #91)</summary>
    public override void CancelUse() => Escorter?.CancelCapture();

    /// <summary>
    /// 버리기 등 소유권 이전 경로에서 서버가 직접 채널을 끊는다 (ItemBase 훅).
    /// 채널이 아이템이 아니라 PlayerEscorter에 있어 분리되면 Escorter를 못 찾으므로,
    /// 아직 부착돼 있을 때 부르는 이 훅에서 서버 권위로 끊는다. (수갑에서 한 번 터진 버그 — 커밋 7a06861)
    /// </summary>
    public override void ServerCancelActiveUse() => Escorter?.ServerCancelChannel();

    private static NpcController ResolveTarget(GameObject aimTarget)
    {
        if (aimTarget == null)
            return null;
        return aimTarget.GetComponentInParent<NpcController>();
    }

    // ---- 라이프사이클 ----

    public override void OnNetworkDespawn() => CancelUse();

    private void OnDisable() => CancelUse();
}
