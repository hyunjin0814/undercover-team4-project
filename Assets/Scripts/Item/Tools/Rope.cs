using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 밧줄 아이템 — 무력화된 NPC나 기능 정지된 동료를 좌클릭으로 묶어 끌고 다닌다.
/// 채널링·판정·끌기는 서버 권위로 PlayerEscortCommands가 처리하며, 풀기는 E로 한다.
/// </summary>
public class Rope : ItemBase
{
    private PlayerEscortCommands Commands => GetComponentInParent<PlayerEscortCommands>();

    private PlayerEscorter Escorter => GetComponentInParent<PlayerEscorter>();

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

        if (IsAlreadyDragging(target))
        {
            Debug.Log($"이미 끌고 있는 대상: {target.name}");
            return;
        }

        if (PlayerEscortCommands.IsRopeBlocked(target))
        {
            Debug.Log($"밧줄을 쓸 수 없는 대상 (유치장 안 / 반출한 신병): {target.name}");
            return;
        }

        if (escorter.CanResumeRopeDrag(target))
        {
            escorter.RequestRopeResume(target);
            return;
        }

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

    /// <summary>Use()의 조기 검증과 동일 기준 — 조준 피드백(윤곽선)용. 묶기·합류·재개 어느 쪽이든 반응한다.</summary>
    public override bool CanTarget(GameObject aimTarget)
    {
        PlayerEscortCommands escorter = Commands;
        PlayerEscorter tethers = Escorter;

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

        if (IsAlreadyDragging(target))
            return false;

        if (PlayerEscortCommands.IsRopeBlocked(target))
            return false;

        if (escorter.CanResumeRopeDrag(target))
            return true;

        return (tethers == null || !tethers.IsAtRopeCapacity)
            && (NpcStateRules.CanRopeBind(target)
                || NpcStateRules.CanJoinDrag(target.CurrentState));
    }

    /// <summary>CanTarget 갈래에 맞는 조준 안내 문구를 돌려준다.</summary>
    public override LocalizedString TargetPromptLabel(GameObject aimTarget)
    {
        if (ResolveCarryTarget(aimTarget) != null)
            return InteractPrompts.RopeBind;

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

    /// <summary>좌클릭 뗌 — 진행 중인 합류 채널링 취소를 서버에 요청한다. 풀기는 채널이 없어졌다.</summary>
    public override void CancelUse() => Commands?.CancelCapture();

    /// <summary>소유권 이전 시 서버에서 진행 중인 채널을 끊는다.</summary>
    public override void ServerCancelActiveUse() => Commands?.ServerCancelChannel();

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

    private PlayerCarrier ResolveCarryTarget(GameObject aimTarget)
    {
        if (aimTarget == null)
            return null;

        PlayerCarrier target = aimTarget.GetComponentInParent<PlayerCarrier>();
        if (target == null || target == Carrier)
            return null;

        return target.CanBeCarried ? target : null;
    }

    public override void OnNetworkDespawn() => CancelUse();

    private void OnDisable() => CancelUse();
}
