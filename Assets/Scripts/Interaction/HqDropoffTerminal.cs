using UnityEngine;

/// <summary>
/// 본부 인계 단말 (#414) — 연행해 온 NPC를 여기서 상호작용키(E)로 넘겨야 검거 판정이 난다.
/// 예전에는 인계 구역 콜라이더에 닿는 순간 자동 판정됐다(#59) — 어색해서 수동 상호작용으로 바꿨다.
///
/// <b>내려놓고 접수해도 된다.</b> 대상이 끌려오는 중(Escorted)이든 인계존에 내려놓은 상태(Captured)든
/// 인계할 수 있다 — 끌고 선 채로는 단말을 겨누는 동안 대상이 존을 벗어나기 쉽다. 판별 기준은 끌기가
/// 아니라 <b>밧줄</b>이다: 놓기(E)는 줄을 풀지 않으므로(#369) "누가 데려온 신병인가"가 그대로 남는다.
/// 줄이 끊기면(먼 이탈·풀기 채널링) 다시 묶어 와야 접수된다.
///
/// <b>판정을 직접 하지 않는다.</b> 요청을 끌고 있는 플레이어(<see cref="PlayerEscorter.RequestDeliver"/>)에게
/// 넘기고, 서버가 <see cref="ArrestJudge.TryDeliver"/>로 상태·구역을 재검증한 뒤 판정한다
/// (요청/실행 분리, #118 관례). 대상 NPC를 클라가 지정하지 않으므로 위조할 여지도 없고,
/// 씬 NetworkObject가 아니어도 원격 클라이언트에서 그대로 동작한다.
///
/// 씬 배치: 조준용 콜라이더는 <b>Interactable 레이어</b>에 둘 것 — PlayerInteractor의 조준 마스크가
/// 그 레이어만 본다. 모델 원본 콜라이더는 Default로 남겨 물리 충돌·시야 차단을 유지하고,
/// 자식 트리거 콜라이더를 조준용으로 쓴다(스크립트는 루트에 둬도 GetComponentInParent로 찾힌다).
/// </summary>
public class HqDropoffTerminal : MonoBehaviour, IInteractable
{
    [Header("인계 구역 (비우면 씬에서 자동 탐색)")]
    [Tooltip("끌고 온 NPC가 이 구역 안에 있어야 인계할 수 있다")]
    [SerializeField] private HqDropoffZone m_zone;

    private void Awake()
    {
        // HqDropoffZone은 장소 오브젝트라 App 대상이 아님 — 씬 탐색 유지 (ArrestJudge와 같은 관례)
        if (m_zone == null)
            m_zone = FindFirstObjectByType<HqDropoffZone>();

        if (m_zone == null)
            Debug.LogWarning("HqDropoffTerminal: 인계 구역(HqDropoffZone)을 찾지 못했다 — 구역 판정 없이 인계된다", this);
    }

    /// <summary>
    /// 끌기 중 E가 '놓기'로 소비되지 않게 우선권을 갖는다 — 인계는 끌고 온 상태에서만 하는 행동이다. (#414)
    /// </summary>
    public bool TakesPriorityOverRelease(GameObject interactor) => true;

    /// <summary>
    /// 지금 E가 실제로 먹히는가 — 조준 윤곽선(#184)과 서버 판정이 같은 기준을 쓴다.
    /// 원격 클라이언트에서도 성립해야 하므로 동기화되는 값만 본다(TetheredCount·GetTetheredNpc·
    /// NpcController.CurrentState) — 끌기 여부(IsDraggingNpc)는 서버 전용이라 여기서 보면 남의
    /// 화면에서 윤곽선이 영영 안 켜진다.
    /// 여러 명을 한 번에 끌고 왔으면(#390) 그중 하나라도 접수 가능할 때 켜진다 — 서버(ServerDeliver)도
    /// 묶인 대상을 전부 돌며 판정하므로 기준이 어긋나지 않는다.
    /// </summary>
    public bool CanInteract(GameObject interactor)
    {
        PlayerEscorter escorter = FindEscorter(interactor);
        if (escorter == null)
            return false; // 내 밧줄에 묶인 대상이 없으면 넘길 것이 없다 (끌기 여부는 묻지 않는다)

        for (int i = 0; i < escorter.TetheredCount; i++)
        {
            NpcController npc = escorter.GetTetheredNpc(i);
            if (npc == null)
                continue;

            // 확보된 신병만 — 끌려오는 중(Escorted)과 내려놓은 대상(Captured) 둘 다 통과한다.
            // 상태는 동기화되므로(NpcController.CurrentState) 클라에서도 서버와 같은 답이 나온다.
            if (!NpcStateRules.CanDeliver(npc.CurrentState))
                continue;

            if (m_zone == null || m_zone.Contains(npc.transform.position))
                return true;
        }

        return false;
    }

    public void Interact(GameObject interactor)
    {
        // 클라 게이팅은 조준 피드백용이고 실제 기준은 서버(ArrestJudge.TryDeliver)다 — 여기서 한 번 더
        // 보는 것은 "윤곽선이 꺼진 상태에서 눌러 서버 로그만 남기는" 헛발질을 줄이기 위한 것.
        if (!CanInteract(interactor))
            return;

        Debug.Log("E 입력 — 본부 인계 요청");
        FindEscorter(interactor).RequestDeliver();
    }

    private static PlayerEscorter FindEscorter(GameObject interactor) =>
        interactor != null ? interactor.GetComponentInParent<PlayerEscorter>() : null;
}
