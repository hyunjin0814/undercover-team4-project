using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 감옥 문 (#415/#537) — 도시와 격리된 감옥 방을 잇는 <b>순간이동 상호작용 오브젝트</b>다.
/// 문 뒤에 실제 공간은 없다. 걸어서 지날 수 있는 통로가 아니라 E를 누르는 지점이다.
///
/// <b>여닫는 개념 자체가 없다</b> (#537). 자동 개폐(#522)는 "닫힌 문을 신병이 뚫고 지나간다"를 막으려고
/// 넣은 것인데(문짝 콜라이더가 CharacterController만 막고 NavMeshAgent·밧줄 끌기는 통과했다),
/// 문턱을 넘는 이동 자체가 없어져 막을 대상이 사라졌다. 미끄러지는 연출도 함께 걷어냈다 — 통과가
/// 아니라 순간이동이라 문짝이 열릴 이유가 없다. 잠김 상태는 문에 걸린 사슬
/// (<see cref="JailLockView"/>)이 대신 보여 준다.
///
/// <b>이 문은 내 몸이 오가는 것만 담당한다.</b> 신병을 넣는 것은 옆에 둔 <see cref="JailIntakeButton"/>이다 —
/// 같은 키가 상황에 따라 다른 일을 하면 조준 윤곽선이 무엇을 약속하는지 흐려지기 때문이다
/// (그쪽 주석에 근거가 있다). E는 세 갈래이고 순서가 곧 우선순위다:
///
///  1. <b>감옥 안에 있으면 나온다</b> — 따라오던 반출 대상도 함께 문 밖으로 나온다. 확보한 대상이
///     없어도 언제든 나올 수 있다.
///  2. <b>자물쇠가 풀려 있으면 잠근다</b> (#492/#231) — 털린 감옥을 되돌리는 것은 플레이어의 책임이고,
///     그 조작이 여기다. 들어가기보다 앞서는 이유: 탈옥 중에 출입부터 되면 "먼저 잠근다"는 압박이 사라진다.
///  3. <b>밖에서 빈손이면 들어간다</b> — 감옥 안 입장 지점으로 순간이동한다.
///     신병을 <b>끌고 있으면 막는다</b>: 이 문은 플레이어만 옮기므로, 묶인 신병을 둔 채 들어가면
///     줄이 400m로 늘어나 끊기고 신병만 도시에 남는다. 넣는 것은 옆 버튼의 일이다.
///
/// <b>이 컴포넌트는 두 곳에 붙는다</b> — 도시 쪽 컨테이너 문과 <b>감옥 방 안의 출구 문</b>이다.
/// 방 안에도 조준할 대상이 있어야 나올 수 있기 때문이고, 갈래가 위치로 갈리므로 같은 스크립트로 족하다.
/// 방 안 문에서는 1번만 성립한다(자물쇠 잠그기는 도시 쪽 일이라 안에서는 건너뛴다).
///
/// <b>이 문으로 감옥에 들어가는 것은 플레이어뿐이다.</b> NPC는 상호작용을 걸 수단이 없고
/// (E는 <see cref="PlayerInteractor"/>만 쏜다), 서버 처리도 <see cref="PlayerMovement"/>가 있는
/// 대상에서만 진행한다. NPC가 감옥 안으로 들어가는 경로는 <b>판정을 통과한 수감뿐</b>이며
/// (<see cref="JailIntakeButton"/> → <see cref="JailIntake"/>의 배치 순간이동), 반출한 대상은
/// 따라다니다 플레이어가 문을 쓸 때 <b>밖으로만</b> 함께 나온다 — 들어오는 방향은 없다.
///
/// 씬 배치: 조준용 콜라이더를 <b>Interactable 레이어</b>에 둘 것 — PlayerInteractor의 조준 마스크가 그
/// 레이어만 본다 (다른 상호작용물과 같은 관례).
///
/// 서버 권위 — 순간이동 판단은 전부 서버가 한다. 동기화할 자체 상태가 없어 NetworkVariable도 없다.
/// </summary>
public class JailDoor : NetworkBehaviour, IInteractable
{
    [Header("감옥 출입구 (비우면 씬에서 자동 탐색)")]
    [Tooltip("판정·배치·순간이동을 실제로 수행하는 쪽 — 이 문은 요청만 넘긴다")]
    [SerializeField] private JailIntake m_intake;

    [Header("자물쇠 — 도시 쪽 문만 물린다")]
    [Tooltip(
        "E로 '잠그기'를 할 수 있게 하는 자물쇠 (#231/#492).\n\n"
            + "감옥 방 안의 출구 문은 <b>비워 둘 것</b>: 잠그는 것은 도시 쪽 조작이라, 물리면 방 안에서도 "
            + "잠그기 갈래가 생겨 나가기와 경합한다"
    )]
    [SerializeField] private JailLock m_jailLock;

    [Header("거절 안내")]
    [Tooltip("신병을 끌고 들어가려 할 때 누른 사람에게만 띄울 문구 — HudTable/Hud.Jail.NeedButton")]
    [SerializeField] private LocalizedString m_needButtonMessage;

    [Tooltip("안내 문구가 화면에 머무는 시간(초)")]
    [SerializeField] private float m_messageSeconds = 2.5f;

    private void Awake()
    {
        // 감옥 방이 도시에서 떨어져 있어 부모 탐색으로는 닿지 않는다 — 장소 오브젝트라 씬 탐색을 쓴다
        if (m_intake == null)
            m_intake = App.Game.JailIntake;

        if (m_intake == null)
            Debug.LogWarning("JailDoor: JailIntake를 찾지 못했다 — 출입·수감이 동작하지 않는다", this);

        // 자물쇠는 <b>자동 탐색하지 않는다</b> — 비어 있는 것이 곧 "이 문은 잠그는 문이 아니다"라는 뜻이다.
        // 찾아 넣으면 감옥 방 안의 출구 문까지 자물쇠를 물어, 탈옥 중에 방 안 문이 계속 열려 있고
        // 잠그기 갈래가 위치 판정 순서에만 기대게 된다. 도시 쪽 문은 프리팹에서 직접 배선한다.
    }

    // ---- 플레이어 상호작용 ----

    /// <summary>
    /// 항상 뜬다 — 문은 이제 조작이 아니라 출입구다. (사거리·가시선은 PlayerInteractor가 이미 걸러 준다)
    /// 자물쇠가 잠겨 있어도 막지 않는다: 자물쇠는 침입자(#231)를 막는 장치이지 경찰의 출입을 막는 것이
    /// 아니고, 잠긴 문 앞에서 E가 죽으면 감옥에 들어갈 방법 자체가 없어진다.
    /// </summary>
    public bool CanInteract(GameObject interactor) => m_intake != null;

    /// <summary>E — 상황에 따라 잠그기·나오기·수감·들어가기 중 하나. (#537)</summary>
    public void Interact(GameObject interactor)
    {
        if (!IsSpawned)
        {
            ServerHandleInteract(interactor); // 오프라인 단독 테스트
            return;
        }

        RequestInteractRpc(new NetworkObjectReference(interactor.GetComponentInParent<NetworkObject>()));
    }

    // 클라 입력을 서버로 넘긴다 — 소유권을 요구하지 않는다(씬 오브젝트이고 누구나 드나든다).
    // 판정·순간이동 권위는 서버에 있으므로 여기서 상태를 직접 건드리지 않는다 (CCTVSwitcher와 같은 관례, #362).
    //
    // 누른 사람을 참조로 실어 보낸다: RPC의 senderClientId로 되찾으려면 서버가 그 클라의 플레이어
    // 오브젝트를 다시 조회해야 하는데, 그쪽은 스폰 타이밍에 따라 null이 될 수 있다.
    [Rpc(SendTo.Server)]
    private void RequestInteractRpc(NetworkObjectReference interactorRef)
    {
        if (interactorRef.TryGet(out NetworkObject interactor))
            ServerHandleInteract(interactor.gameObject);
    }

    // E 처리 — 서버(또는 오프라인) 전용. 갈래 순서는 클래스 주석 참고.
    private void ServerHandleInteract(GameObject interactor)
    {
        if (IsSpawned && !IsServer)
            return;

        if (interactor == null || m_intake == null)
            return;

        JailZone zone = m_intake.Zone;
        if (zone == null)
            return;

        PlayerMovement mover = interactor.GetComponent<PlayerMovement>();
        if (mover == null)
            return;

        // 1. 안에 있으면 나온다 — 따라오던 반출 대상도 함께 (JailIntake가 동행을 찾는다).
        //
        // <b>자물쇠보다 먼저 본다.</b> 잠그기는 도시 쪽 문의 일이고, 방 안에서 잠글 이유가 없다.
        // 순서를 뒤집으면 탈옥이 진행 중일 때 방 안 문이 '잠그기'로 먹혀 <b>갇힌 것처럼 보인다</b> —
        // 두 번 눌러야 나가지는데, 그 한 번이 무엇을 했는지 안에서는 보이지 않는다.
        if (zone.ContainsPoint(interactor.transform.position))
        {
            m_intake.ServerExitJail(mover);
            return;
        }

        // 2. 털린 감옥을 잠근다 — 자물쇠가 풀린 동안은 들어가기보다 이것이 앞선다 (#492).
        //    탈옥 중에 출입부터 되면 "먼저 잠근다"는 압박이 사라진다.
        if (m_jailLock != null && !m_jailLock.IsLocked)
        {
            m_jailLock.ServerRelock();
            Debug.Log("[감옥 문] 자물쇠를 다시 잠갔다");
            return;
        }

        // 3. <b>신병을 끌고 있으면 들여보내지 않는다.</b>
        //
        // 이 문은 <b>플레이어만</b> 옮긴다(클래스 주석). 묶인 신병을 둔 채 들어가면 줄이 도시와
        // 감옥 방 사이 400m로 늘어나 끊기고, 신병은 도시에 홀로 남는다 — 판정도 안 받은 채
        // 방치돼 결국 달아난다. 넣는 조작은 옆 버튼이므로 여기서는 막고 그쪽으로 보낸다.
        PlayerEscorter escorter = interactor.GetComponent<PlayerEscorter>();
        if (escorter != null && escorter.TetheredCount > 0)
        {
            Debug.Log("[감옥 문] 용의자를 끌고는 들어갈 수 없다 — 옆 판정 버튼을 쓸 것");
            NotifyInteractor(interactor);
            return;
        }

        // 4. 빈손 — 감옥 안으로 들어간다. 신병 수감은 이 문이 아니라 옆 버튼이다 (JailIntakeButton)
        m_intake.ServerEnterJail(mover);
    }

    // ---- 거절 안내 (누른 사람에게만) ----

    /// <summary>
    /// 누른 사람 <b>한 명에게만</b> 안내를 띄운다 — 서버(또는 오프라인)에서 호출. (#537)
    ///
    /// <c>NotifyOwner</c>(ChanneledInteractionBehaviour)를 쓸 수 없다: 그 경로는 <b>오브젝트의 오너</b>에게
    /// 보내는데 이 문은 씬 오브젝트라 오너가 서버다 — 원격 클라가 눌러도 호스트 화면에 뜬다.
    /// 그래서 누른 클라를 지목해 보낸다.
    ///
    /// <b>문구를 RPC에 싣지 않는다.</b> <see cref="LocalizedString"/>은 직렬화해 보낼 수 없기도 하고,
    /// 애초에 각 피어가 같은 프리팹 필드를 들고 있어 보낼 이유가 없다 — "띄워라"만 보내면 된다.
    /// </summary>
    private void NotifyInteractor(GameObject interactor)
    {
        if (!IsSpawned)
        {
            ShowNeedButtonLocal(); // 오프라인 단독 테스트
            return;
        }

        NetworkObject netObject = interactor.GetComponentInParent<NetworkObject>();
        if (netObject == null)
            return;

        if (netObject.OwnerClientId == NetworkManager.LocalClientId)
        {
            ShowNeedButtonLocal(); // 누른 사람이 호스트 자신 — 보낼 것 없이 로컬 표시
            return;
        }

        ShowNeedButtonRpc(RpcTarget.Single(netObject.OwnerClientId, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void ShowNeedButtonRpc(RpcParams rpcParams) => ShowNeedButtonLocal();

    // HUD가 없는 환경(데디케이티드 서버 등)에선 App.UI.Toast가 null이라 무동작 — PlayerPenaltyView와 같은 방침
    private void ShowNeedButtonLocal() => App.UI.Toast?.Show(m_needButtonMessage, m_messageSeconds);
}
