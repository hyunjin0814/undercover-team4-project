using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// NPC에 대한 E 상호작용 — Captured면 밧줄을 풀고, 남이 끄는 대상에 내 줄이 있으면 내 줄만 뺀다.
/// 반출된 수감자는 밧줄 없는 추종을 재개한다.
/// </summary>
[RequireComponent(typeof(NpcController))]
public class NpcSubdueInteractable : MonoBehaviour, IInteractable
{
    private NpcController m_controller;

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();
    }

    /// <summary>E 상호작용이 동작하는 상태인지 판정한다(조준 윤곽선용).</summary>
    public bool CanInteract(GameObject interactor) =>
        NpcStateRules.HasInteractKeyAction(m_controller.CurrentState)
        || (
            m_controller.CurrentState == NpcState.Escorted
            && CanRejoinOwnRope(FindTethers(interactor))
        );

    private static PlayerEscortCommands FindCommands(GameObject interactor) =>
        interactor != null ? interactor.GetComponentInParent<PlayerEscortCommands>() : null;

    private static PlayerEscorter FindTethers(GameObject interactor) =>
        interactor != null ? interactor.GetComponentInParent<PlayerEscorter>() : null;

    /// <summary>남이 끄는 대상에 내 줄이 걸려 있어 E로 뺄 수 있는지 판정한다.</summary>
    private bool CanRejoinOwnRope(PlayerEscorter tethers) =>
        m_controller.CurrentState == NpcState.Escorted
        && tethers != null
        && tethers.IsTetheredTo(m_controller)
        && !tethers.IsDraggingNpc(m_controller);

    /// <summary>Interact의 갈래에 맞는 조준 안내 문구를 돌려준다.</summary>
    public LocalizedString PromptLabel(GameObject interactor)
    {
        switch (m_controller.CurrentState)
        {
            case NpcState.Escorted:
                return CanRejoinOwnRope(FindTethers(interactor))
                    ? InteractPrompts.NpcUnropeMine
                    : null;

            case NpcState.Captured:
                return InteractPrompts.NpcUnrope;
        }

        return null;
    }

    public void Interact(GameObject interactor)
    {
        PlayerEscortCommands escorter = FindCommands(interactor);

        switch (m_controller.CurrentState)
        {
            case NpcState.Escorted:
                if (CanRejoinOwnRope(FindTethers(interactor)))
                {
                    Debug.Log($"E 입력 — 내 줄 빼기 요청(줄다리기 이탈): {m_controller.name}");
                    escorter?.RequestUnrope(m_controller);
                }
                break;

            case NpcState.Captured:
                Debug.Log($"E 입력 — 밧줄 풀기 요청: {m_controller.name}");
                escorter?.RequestUnrope(m_controller);
                break;
        }
    }
}
