using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 감옥 문 옆 수감 버튼 — <b>신병을 판정해 감옥으로 보내는</b> 조작 하나만 담당한다. (#537)
///
/// <b>왜 문과 갈랐나.</b> 처음에는 문 E 하나가 상황을 보고 네 갈래(잠그기·나오기·수감·들어가기)로
/// 갈렸는데, 같은 키가 상황에 따라 다른 일을 하면 <b>조준 윤곽선이 무엇을 약속하는지</b>가 흐려진다.
/// 신병을 끌고 문 앞에 선 순간 문에 윤곽선이 켜져도 그게 "들어간다"인지 "넣는다"인지 알 수 없다.
/// 버튼을 따로 두면 <b>문 = 내 몸이 오간다 / 버튼 = 신병을 넣는다</b>로 갈려, 누르기 전에 무슨 일이
/// 일어날지가 눈에 보인다 (본부 CCTV 콘솔이 모니터가 아니라 버튼을 상호작용물로 두는 것과 같은 이유, #362).
///
/// 판정·배치·순간이동은 전부 <see cref="JailIntake"/>가 한다 — 이 버튼은 "지금 눌렸다"만 서버로 넘긴다.
///
/// 씬 배치: 조준용 콜라이더를 <b>Interactable 레이어</b>에 둘 것 — PlayerInteractor의 조준 마스크가 그
/// 레이어만 본다 (다른 상호작용물과 같은 관례). 문 바로 옆, 신병을 끌고 선 자리에서 함께 보이는 곳에 둔다.
/// </summary>
public class JailIntakeButton : NetworkBehaviour, IInteractable
{
    [Header("감옥 출입구 (비우면 씬에서 자동 탐색)")]
    [Tooltip("판정·배치를 실제로 수행하는 쪽 — 이 버튼은 요청만 넘긴다")]
    [SerializeField] private JailIntake m_intake;

    [Header("안내")]
    [Tooltip("확보한 신병 없이 눌렀을 때 누른 사람에게만 띄울 문구 — HudTable/Hud.Jail.NoCustody")]
    [SerializeField] private LocalizedString m_noCustodyMessage;

    [Tooltip("안내 문구가 화면에 머무는 시간(초)")]
    [SerializeField] private float m_messageSeconds = 2.5f;

    /// <summary>수감이 실제로 일어났다 — 램프·소리 연출이 구독할 훅. 서버(또는 오프라인)에서 발생한다.</summary>
    public event System.Action<int> OnAdmitted;

    private void Awake()
    {
        if (m_intake == null)
            m_intake = App.Game.JailIntake;

        if (m_intake == null)
            Debug.LogWarning("JailIntakeButton: JailIntake를 찾지 못했다 — 수감이 동작하지 않는다", this);
    }

    /// <summary>
    /// 항상 뜬다 — <b>확보한 신병이 없어도 막지 않는다.</b> 빈손으로 눌러도 손해가 없는 조작이고,
    /// 여기서 false를 돌리면 안내 자체가 사라져 "왜 안 되는지"를 말할 자리가 없어진다.
    /// 없다는 사실은 회색 사유로 알린다 (<see cref="BlockedReason"/>).
    /// (사거리·가시선은 PlayerInteractor가 이미 걸러 준다)
    /// </summary>
    public bool CanInteract(GameObject interactor) => m_intake != null;

    // 조준 안내 (#664)
    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.JailAdmit;

    /// <summary>
    /// 확보한 신병이 없으면 회색으로 뜬다 — 눌러 보고 알던 것을 겨눌 때 알린다. (#664)
    ///
    /// 예전에는 이 사유를 달지 못했다: 기준(#637)이 서버 전용 표식 위에 있어 클라가 흉내 내면
    /// 서버와 어긋났다. 그 표식들이 동기화되면서(<see cref="NpcCustody.IsSecuredByAnyone"/>)
    /// 이제 같은 기준을 그대로 읽는다. <see cref="CanInteract"/>는 여전히 참이다 — 빈손으로 눌러도
    /// 손해가 없는 조작이고, 눌렀을 때의 안내(<see cref="ShowNoCustodyLocal"/>)가 정본으로 남는다.
    /// </summary>
    public LocalizedString BlockedReason(GameObject interactor) =>
        m_intake != null && !m_intake.HasAdmittableCustody(interactor)
            ? InteractPrompts.ReasonNoCustody
            : null;

    /// <summary>E — 확보한 신병을 그 자리에서 판정해 감옥으로 보낸다. (#537)</summary>
    public void Interact(GameObject interactor)
    {
        if (!IsSpawned)
        {
            ServerAdmit(interactor); // 오프라인 단독 테스트
            return;
        }

        NetworkObject netObject = interactor.GetComponentInParent<NetworkObject>();
        if (netObject != null)
            RequestAdmitRpc(new NetworkObjectReference(netObject));
    }

    // 클라 입력을 서버로 넘긴다 — 소유권을 요구하지 않는다(씬 오브젝트이고 누구나 누른다).
    // 누른 사람을 참조로 실어 보낸다: senderClientId로 되찾으려면 서버가 그 클라의 플레이어 오브젝트를
    // 다시 조회해야 하는데, 그쪽은 스폰 타이밍에 따라 null이 될 수 있다. (JailDoor와 같은 관례)
    [Rpc(SendTo.Server)]
    private void RequestAdmitRpc(NetworkObjectReference interactorRef)
    {
        if (interactorRef.TryGet(out NetworkObject interactor))
            ServerAdmit(interactor.gameObject);
    }

    private void ServerAdmit(GameObject interactor)
    {
        if (IsSpawned && !IsServer)
            return;

        if (interactor == null || m_intake == null)
            return;

        int handled = m_intake.ServerAdmitHeldBy(interactor);
        if (handled <= 0)
        {
            Debug.Log("[감옥] 수감 버튼 — 확보한 신병이 없다");
            NotifyInteractor(interactor);
            return;
        }

        OnAdmitted?.Invoke(handled);
    }

    /// <summary>
    /// 누른 사람 <b>한 명에게만</b> 안내를 띄운다 — 서버(또는 오프라인)에서 호출. (#537)
    /// 이 버튼은 씬 오브젝트라 오너가 서버다 — <c>SendTo.Owner</c>로는 호스트 화면에 뜬다.
    /// 그래서 누른 클라를 지목해 보낸다 (<see cref="JailDoor"/>와 같은 방식).
    /// </summary>
    private void NotifyInteractor(GameObject interactor)
    {
        if (!IsSpawned)
        {
            ShowNoCustodyLocal(); // 오프라인 단독 테스트
            return;
        }

        NetworkObject netObject = interactor.GetComponentInParent<NetworkObject>();
        if (netObject == null)
            return;

        if (netObject.OwnerClientId == NetworkManager.LocalClientId)
        {
            ShowNoCustodyLocal();
            return;
        }

        ShowNoCustodyRpc(RpcTarget.Single(netObject.OwnerClientId, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void ShowNoCustodyRpc(RpcParams rpcParams) => ShowNoCustodyLocal();

    // HUD가 없는 환경(데디케이티드 서버 등)에선 App.UI.Toast가 null이라 무동작
    private void ShowNoCustodyLocal() => App.UI.Toast?.Show(m_noCustodyMessage, m_messageSeconds);
}
