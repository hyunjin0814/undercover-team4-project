using UnityEngine;

/// <summary>
/// NPC의 상호작용키(E) 반응 (#76/#91/#398) — 누르는 즉시 NPC 상태에 따라 갈린다.
/// 체포(Captured) 상태면 재연행을 시작한다 — 연행 동작을 수갑 클릭에서 E로 이관 (#91).
/// 단 그 대상이 반출된 수감자면(#517) 밧줄이 아니라 <b>밧줄 없는 추종</b>을 재개한다 — 반출 흐름 왕복.
/// 남이 끌고 있는(Escorted) 대상에 내 줄이 걸려 있으면 끌기를 재개한다 — 줄다리기 복귀 (#398).
/// 수감(Jailed) 상태면 유치장에서 빼내 따라오게 한다 — 밧줄 없이 추종만 건다 (#492).
/// PlayerInteractor의 IInteractable 경로를 그대로 사용하므로
/// NPC가 사거리·조준을 벗어나면 자연히 실패한다.
/// 프롬프트 표시는 상호작용 UI 이슈(#65 계열) 후속.
///
/// <b>E는 신병 조작 전용 키가 됐다</b> — 때리는 것도 잡는 것도 하지 않는다.
/// 두 단계로 걷혔다: 도주형 전용 3초 제압 홀드(#332 → #436 제거),
/// 그리고 배회·도주·저항에 대한 제압 타격(#79/#366 → #438 제거).
/// 이제 체력을 깎는 것은 <see cref="Baton"/>(진압봉), 즉시 무력화는 <see cref="Taser"/>(#292),
/// 신병 확보는 <see cref="Rope"/>(밧줄, #369)가 각각 맡는다.
///
/// 클래스·파일명에 남은 "Subdue"(제압)는 <b>이름만 남은 이력</b>이다 — 프리팹이 스크립트를
/// 파일 GUID로 참조하므로 개명 churn을 피해 그대로 뒀다. 실제 역할은 위 두 갈래뿐이다.
/// </summary>
[RequireComponent(typeof(NpcController))]
public class NpcSubdueInteractable : MonoBehaviour, IInteractable
{
    private NpcController m_controller;

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();
    }

    /// <summary>E 상호작용이 실제로 동작하는 상태인지 — 조준 피드백(윤곽선) 판정용. (#184)
    /// 상태를 먼저 본다: 조준 대상마다 매 프레임 도는 경로라 Escorted가 아니면 escorter 조회조차
    /// 하지 않는다(인자가 즉시 평가되므로 CanRejoinOwnRope 안의 상태 검사로는 늦다).</summary>
    public bool CanInteract(GameObject interactor) =>
        NpcStateRules.HasInteractKeyAction(m_controller.CurrentState)
        || NpcStateRules.IsFollowingUnroped(m_controller)
        || (
            m_controller.CurrentState == NpcState.Escorted
            && CanRejoinOwnRope(FindTethers(interactor))
        );

    // 요청은 명령 허브로, "내 줄인가" 판정은 연결 상태로 — 둘은 서로 다른 컴포넌트다.
    private static PlayerEscortCommands FindCommands(GameObject interactor) =>
        interactor != null ? interactor.GetComponentInParent<PlayerEscortCommands>() : null;

    private static PlayerEscorter FindTethers(GameObject interactor) =>
        interactor != null ? interactor.GetComponentInParent<PlayerEscorter>() : null;

    /// <summary>
    /// 남이 계속 끌고 있는(Escorted) 대상이라도 <b>내 줄이 걸려 있으면</b> E로 다시 낄 수 있는가 —
    /// 줄다리기에서 E로 빠졌다 복귀하는 경로다. (#398)
    ///
    /// 상태 순수 함수인 <see cref="NpcStateRules"/>에 둘 수 없다 — "누구의 줄인가"는 요청자마다 다르다.
    /// 그리고 그 조건이 곧 <b>탈취 차단</b>이다: 남의 신병에는 내 줄이 없다.
    /// 이미 끌고 있으면 제외한다 — 그때 E는 '놓기'로 가로채진다(PlayerInteractor).
    /// </summary>
    private bool CanRejoinOwnRope(PlayerEscorter tethers) =>
        m_controller.CurrentState == NpcState.Escorted
        && tethers != null
        && tethers.IsTetheredTo(m_controller)
        && !tethers.IsDraggingNpc(m_controller);

    public void Interact(GameObject interactor)
    {
        PlayerEscortCommands escorter = FindCommands(interactor);

        // 신병이 걸린 두 상태에서만 반응한다. 배회·도주·저항은 #438에서 빠졌다 — 그 상태의
        // NPC에게 E는 아무 일도 하지 않으며, CanInteract가 false라 윤곽선도 뜨지 않는다.
        // 이 switch의 분기 집합은 NpcStateRules.HasInteractKeyAction + CanRejoinOwnRope와 반드시
        // 일치해야 한다 (#184 — Escorted 분기만 상태가 아니라 요청자의 줄로 갈린다, #398)
        switch (m_controller.CurrentState)
        {
            case NpcState.Escorted:
                // 반출로 따라오는 수감자를 세운다 (#492) — 밧줄이 없어 아래 줄다리기 분기와 배타적이다.
                // 유치장 안이면 JailIntake가 좌석에 다시 앉히고, 밖이면 그 자리에 선다.
                if (NpcStateRules.IsFollowingUnroped(m_controller))
                {
                    Debug.Log($"E 입력 — 따라오는 수감자 정지 요청: {m_controller.name}");
                    escorter?.RequestEscortHalt(m_controller);
                    break;
                }

                // 남이 계속 끄는 중인 대상에 내 줄로 다시 끼기 (#398) — 서버가 줄 소유·사거리를
                // 다시 검증하므로 여기 검사는 조기 차단일 뿐이다.
                if (CanRejoinOwnRope(FindTethers(interactor)))
                {
                    Debug.Log(
                        $"E 입력 — 내 줄로 끌기 재개 요청(줄다리기 복귀): {m_controller.name}"
                    );
                    escorter?.RequestRopeResume(m_controller);
                }
                break;

            case NpcState.Captured:
                // 반출된 수감자가 거리 이탈로 멈춘 것이면 반출 흐름을 잇는다 — 밧줄 없이 다시 따라오게 한다 (#517).
                // 밧줄 분기보다 <b>먼저</b> 봐야 한다: 상태가 같아서 아래로 내려가면 그대로 밧줄에 묶여 눕는다.
                if (NpcStateRules.CanResumeUnropedEscort(m_controller))
                {
                    Debug.Log($"E 입력 — 반출 수감자 추종 재개 요청: {m_controller.name}");
                    escorter?.RequestEscortResume(m_controller);
                    break;
                }

                // 체포되어 멈춘 NPC를 E로 다시 끌기 시작 — 좌클릭은 같은 대상에서 '풀어주기'라 재개는 E다 (#91/#369).
                // 서버 직접 호출은 가드에 막힌다 — 요청 API로 서버에 넘긴다 (#118).
                // 중복 확보 가드(동시 1명)·밧줄 소지·사거리는 서버가 처리한다
                Debug.Log($"E 입력 — 밧줄 끌기 재개 요청: {m_controller.name}");
                escorter?.RequestRopeResume(m_controller);
                break;

            case NpcState.Jailed:
                // 앉은 수감자를 일으켜 따라오게 한다 (#492) — 밧줄을 걸지 않으므로 밧줄 소지·용량과 무관하다.
                // 좌석 반납·정산 제외는 서버(JailIntake)가 하고, 여기 검사는 조기 차단일 뿐이다.
                Debug.Log($"E 입력 — 유치장 반출 요청: {m_controller.name}");
                escorter?.RequestJailRelease(m_controller);
                break;
        }
    }
}
