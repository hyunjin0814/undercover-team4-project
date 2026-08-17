using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 밧줄 아이템 — 기본 검거 수단. 겨냥한 NPC를 좌클릭으로 묶어 누운 채 질질 끌고 다닌다. (#269 → #369)
/// <b>묶을 수 있는 것은 무력화된 대상뿐이다</b> (#446) — 깨어 있는 NPC를 좌클릭 3초 홀드로 묶던 경로는
/// 제거됐다. 진압봉·테이저로 먼저 쓰러뜨려야 하고, 쓰러진 대상은 홀드 없이 한 번에 묶인다.
/// 같은 좌클릭이 대상 상태로 갈린다: 놓아둔 체포(Captured)나 내 줄이 걸린 대상에겐 <b>끌기 재개</b>다.
/// <b>밧줄 좌클릭은 줄을 거는 조작으로 모였다</b> (#513) — 묶기·합류·재개가 전부 이 키다.
/// 반대로 손을 떼는 쪽(풀기)은 상호작용키(E)다 — 겨냥한 대상 하나는 NpcSubdueInteractable이,
/// 겨냥 없이 누른 E(끌던 대상 전원)는 PlayerInteractor가 PlayerEscortCommands로 넘긴다 (#638).
/// 실제 채널링·사거리·반응 판정·끌기는 서버 권위이며 PlayerEscortCommands가 수행한다 (수갑과 동일한 허브 패턴, #59/#118).
/// 끌기 중엔 손이 묶여 다른 아이템을 쓸 수 없고, 한 번에 1명만 확보할 수 있다.
/// 밧줄은 소모되지 않는다 — 대상에 남지 않으므로 풀기 판정도 상태만 본다.
///
/// <b>기능 정지(Die)된 동료도 같은 밧줄로 끈다</b> (#365) — 대상이 NPC냐 동료냐로 갈릴 뿐,
/// 겨냥하고 좌클릭해 묶고 E로 내려놓는 조작은 같다. 동료 한 명이 밧줄 한 개를 차지하므로 NPC 끌기와
/// 같은 자원 상한(소지한 밧줄 개수, #390)을 나눠 쓴다 — 줄이 남으면 NPC를 끌면서 동료를 옮길 수 있다.
/// 동료 쪽 서버 로직은 PlayerCarrier가 든다(대상이 플레이어라 상태·이동 권한이 NPC와 다르기 때문).
/// 저항하지 않는 몸이라 묶기 채널링은 없다 — 기절한 NPC를 즉시 묶는 것과 같은 취급이다.
/// </summary>
public class Rope : ItemBase
{
    /// <summary>이 밧줄을 든 플레이어의 연행 요청 허브 — 묶기·풀기 요청을 서버로 넘긴다. (Handcuffs와 동일 관례, #88)</summary>
    private PlayerEscortCommands Commands => GetComponentInParent<PlayerEscortCommands>();

    /// <summary>이 밧줄을 든 플레이어의 밧줄 연결 상태 — 용량 게이트 조기검증용. (#390)</summary>
    private PlayerEscorter Escorter => GetComponentInParent<PlayerEscorter>();

    /// <summary>이 밧줄을 든 플레이어의 운반 허브 — 동료(Die) 대상 요청을 서버로 넘긴다. (#365)</summary>
    private PlayerCarrier Carrier => GetComponentInParent<PlayerCarrier>();

    public override bool CanUse() => true;

    public override void Use(GameObject aimTarget)
    {
        PlayerEscortCommands escorter = Commands;
        if (escorter == null)
        {
            Debug.LogWarning("Rope: PlayerEscortCommands를 찾지 못함 — 사용 불가", this);
            return;
        }

        // 기능 정지된 동료를 겨냥했으면 운반이다 (#365) — 상태 판정·사거리는 서버(PlayerCarrier)가 한다
        PlayerCarrier carryTarget = ResolveCarryTarget(aimTarget);
        if (carryTarget != null)
        {
            PlayerCarrier carrier = Carrier;
            if (carrier == null)
            {
                Debug.LogWarning("Rope: PlayerCarrier를 찾지 못함 — 동료 운반 불가", this);
                return;
            }

            carrier.RequestCarry(carryTarget);
            return;
        }

        NpcController target = ResolveTarget(aimTarget);
        if (target == null)
        {
            Debug.Log("밧줄을 쓸 대상이 없음 (NPC를 겨냥하지 않음)");
            return;
        }

        // 이미 내가 끌고 있는 대상에는 좌클릭이 할 일이 없다 (#664). 재개는 손을 뗀 줄을 다시 쥐는
        // 것이고(CanResumeRopeDrag는 '끌고 있음'을 보지 않는다), 합류·묶기는 중복 줄이라 서버가 막는다.
        if (IsAlreadyDragging(target))
        {
            Debug.Log($"이미 끌고 있는 대상: {target.name}");
            return;
        }

        // 유치장에서 꺼낸 신병은 밧줄로 다루지 않는다 (팀 확정 2026-08-05) — 아래 세 갈래 전부가 막힌다.
        // 유치장 안이거나 반출돼 따라오는 중이면 걸리고, 끌고 들어온 줄을 푸는 것(E)은 그대로다.
        if (PlayerEscortCommands.IsRopeBlocked(target))
        {
            Debug.Log($"밧줄을 쓸 수 없는 대상 (유치장 안 / 반출한 신병): {target.name}");
            return;
        }

        // 같은 좌클릭이 대상에 따라 세 갈래로 갈린다 (#390/#513). 판정 기준은 서버 가드·조준 피드백과 동일 (#184):
        //   놓아둔 체포·내 줄이 걸린 대상 → 끌기 재개 / 남이 끄는 중 → 합류 / 나머지 → 새로 묶기
        // 풀기는 이 키에서 빠졌다 — E로 옮겼다 (#513).

        // 놓아둔 신병을 다시 끈다. 남이 계속 끄는 중이라도 내 줄이 걸려 있으면 줄다리기에 다시 낀다 (#398).
        if (escorter.CanResumeRopeDrag(target))
        {
            escorter.RequestRopeResume(target);
            return;
        }

        // 남이 끌고 가는 중이면 밧줄을 덧걸어 합류한다 — 기존 끌기는 끊기지 않는다(탈취 차단).
        // 새 대상을 묶는 것과 같은 자원(밧줄 1개)을 쓰므로 진입 판정도 같은 경로다.
        if (!NpcStateRules.CanJoinDrag(target.CurrentState)
            && !NpcStateRules.CanRopeBind(target))
        {
            Debug.Log("밧줄로 묶을 수 없는 대상 (깨어 있음 / 이미 신병 확보됨 / 페널티 진행 중)");
            return;
        }

        PlayerEscorter tethers = Escorter;
        if (tethers != null && tethers.IsAtRopeCapacity)
        {
            Debug.Log("소지한 밧줄을 전부 쓰고 있음 — 먼저 풀거나 인계할 것");
            return;
        }

        escorter.RequestRopeDrag(target);
    }

    /// <summary>Use()의 조기 검증과 동일 기준 — 조준 피드백(윤곽선)용. 묶기·합류·재개 어느 쪽이든 반응한다. (#184/#513)</summary>
    public override bool CanTarget(GameObject aimTarget)
    {
        PlayerEscortCommands escorter = Commands;
        PlayerEscorter tethers = Escorter;

        // 기능 정지된 동료 — 끌 수 있는 상태이고 남는 밧줄이 있어야 한다 (서버 가드 ServerBeginCarry와 단일 기준, #365).
        // 운반은 한 번에 1명이지만 상한 자체는 NPC 끌기와 같은 자원(소지한 밧줄 개수)에서 나온다 (#390).
        PlayerCarrier carryTarget = ResolveCarryTarget(aimTarget);
        if (carryTarget != null)
        {
            PlayerCarrier carrier = Carrier;
            return carrier != null
                && !carrier.IsCarrying
                && (tethers == null || !tethers.IsAtRopeCapacity);
        }

        NpcController target = ResolveTarget(aimTarget);
        if (target == null)
            return false;

        if (escorter == null)
            return false;

        // 이미 끌고 있으면 좌클릭이 할 일이 없다 — 윤곽선도 안내도 끈다 (Use의 같은 가드와 한 쌍, #664)
        if (IsAlreadyDragging(target))
            return false;

        // 밧줄을 쓸 수 없는 대상에는 윤곽선도 뜨지 않는다 — Use의 조기 차단과 단일 기준 (#184)
        if (PlayerEscortCommands.IsRopeBlocked(target))
            return false;

        // 재개(놓아둔 체포 / 내 줄)는 새 밧줄을 쓰지 않으므로 용량과 무관하게 언제나 가능하다.
        if (escorter.CanResumeRopeDrag(target))
            return true;

        // 새로 묶기·합류는 소지한 밧줄 개수까지만 (서버 가드 CanBeginRopeDrag와 단일 기준, #184/#390)
        // 새로 묶기는 무력화된 대상만이라 깨어 있는 NPC에는 윤곽선도 뜨지 않는다 (#446)
        return (tethers == null || !tethers.IsAtRopeCapacity)
            && (NpcStateRules.CanRopeBind(target)
                || NpcStateRules.CanJoinDrag(target.CurrentState));
    }

    /// <summary>
    /// 조준 안내 (#664) — 위 <see cref="CanTarget"/>의 갈래를 그대로 따라간다.
    /// 저쪽이 true일 때만 불리므로 자원·상태 검사는 다시 하지 않는다.
    /// </summary>
    public override LocalizedString TargetPromptLabel(GameObject aimTarget)
    {
        if (ResolveCarryTarget(aimTarget) != null)
            return InteractPrompts.RopeBind; // 동료도 NPC와 같은 '묶기'로 적는다 — 조작이 같은 좌클릭이다

        NpcController target = ResolveTarget(aimTarget);
        if (target == null)
            return null;

        PlayerEscortCommands commands = Commands;
        if (commands != null && commands.CanResumeRopeDrag(target))
            return InteractPrompts.RopeResume;

        return NpcStateRules.CanJoinDrag(target.CurrentState)
            ? InteractPrompts.RopeJoin
            : InteractPrompts.RopeBind;
    }

    /// <summary>좌클릭 뗌 — 진행 중인 합류 채널링 취소를 서버에 요청한다. 풀기는 채널이 없어졌다 (#513). (#91)</summary>
    public override void CancelUse() => Commands?.CancelCapture();

    /// <summary>
    /// 버리기 등 소유권 이전 경로에서 서버가 직접 채널을 끊는다 (ItemBase 훅).
    /// 채널이 아이템이 아니라 PlayerEscortCommands에 있어 분리되면 못 찾으므로,
    /// 아직 부착돼 있을 때 부르는 이 훅에서 서버 권위로 끊는다. (수갑에서 한 번 터진 버그 — 커밋 7a06861)
    /// </summary>
    public override void ServerCancelActiveUse() => Commands?.ServerCancelChannel();

    // 내가 지금 이 대상을 끌고 있는가 — 묶여만 있는(E로 놓아둔) 대상은 false다.
    // 그 구분이 곧 '다시 끌기'가 의미 있는 조건이다 (NpcSubdueInteractable.CanRejoinOwnRope와 같은 기준).
    private bool IsAlreadyDragging(NpcController target)
    {
        PlayerEscorter tethers = Escorter;
        return tethers != null && tethers.IsDraggingNpc(target);
    }

    private static NpcController ResolveTarget(GameObject aimTarget)
    {
        if (aimTarget == null)
            return null;
        return aimTarget.GetComponentInParent<NpcController>();
    }

    // 겨냥한 것이 '끌 수 있는 동료'인가 — 조준에 잡히는 것은 쓰러진 동안 켜지는 히트박스이고,
    // 그 부모가 플레이어다. 상태 판정은 PlayerCarrier가 단독으로 갖는다(클라·서버 단일 기준, #365).
    private PlayerCarrier ResolveCarryTarget(GameObject aimTarget)
    {
        if (aimTarget == null)
            return null;

        PlayerCarrier target = aimTarget.GetComponentInParent<PlayerCarrier>();
        if (target == null || target == Carrier)
            return null; // 자기 자신 제외

        return target.CanBeCarried ? target : null;
    }

    // ---- 라이프사이클 ----

    public override void OnNetworkDespawn() => CancelUse();

    private void OnDisable() => CancelUse();
}
