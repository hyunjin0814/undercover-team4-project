using UnityEngine;

/// <summary>
/// NPC의 상호작용키(E) 반응 (#76/#79/#91) — 누르는 즉시 NPC 상태에 따라 갈린다.
/// 도주(Run) 중이면 그 자리에서 체포(Captured), 저항(Attack) 중이면 제압 타격 1회로
/// 게이지를 깎는다 — 여럿이 함께 누르면 그만큼 빨리 제압된다 (GDD 7-4 협동 인센티브).
/// 체포(Captured) 상태면 재연행을 시작한다 — 연행 동작을 수갑 클릭에서 E로 이관 (#91).
/// PlayerInteractor의 IInteractable 경로를 그대로 사용하므로
/// NPC가 사거리·조준을 벗어나면 자연히 실패한다 (추격전·몸싸움 성립).
/// 제압 프롬프트 표시는 상호작용 UI 이슈(#65 계열) 후속.
/// </summary>
// TODO: 상호작용 네트워크 전환(#55 계열) 시 도주 제압도 클라 입력 → ServerRpc 경로로 호출
//       (저항 타격은 RequestSubdueHit이 자체 RPC 경로를 가진다, #79)
[RequireComponent(typeof(NpcController))]
public class NpcSubdueInteractable : MonoBehaviour, IInteractable
{
    private NpcController m_controller;

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();
    }

    public void Interact(GameObject interactor)
    {
        // 도주·저항·체포 상태일 때만 반응 — 배회 중인 NPC 오작동 방지 (체포는 수갑 채널링이 정식 경로)
        switch (m_controller.CurrentState)
        {
            case NpcState.Run:
                Debug.Log($"도주 NPC 제압: {m_controller.name}");
                m_controller.CaptureBySubdue();
                break;

            case NpcState.Attack:
                Debug.Log($"저항 NPC 제압 타격: {m_controller.name}");
                m_controller.RequestSubdueHit();
                break;

            case NpcState.Captured:
                // 체포되어 멈춘 NPC를 E로 재연행 — 연행 시작을 수갑 클릭에서 상호작용키로 이관 (#91)
                // 중복 연행 가드(동시 1명)는 PlayerEscorter.StartEscort가 처리한다
                interactor.GetComponentInParent<PlayerEscorter>()?.StartEscort(m_controller);
                break;
        }
    }
}
