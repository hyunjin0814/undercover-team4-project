using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 본부와 격리된 유치장을 잇는 철창문 — E를 누르면 서 있는 위치에 따라 셀로 들어가거나 나온다.
/// 나올 때는 따라오던 반출 대상도 함께 나온다. 순간이동은 서버 권위다.
/// </summary>
public class JailDoor : NetworkBehaviour, IInteractable
{
    [Header("감옥 출입구 (비우면 씬에서 자동 탐색)")]
    [Tooltip("판정·배치·순간이동을 실제로 수행하는 쪽 — 이 문은 요청만 넘긴다")]
    [SerializeField] private JailIntake m_intake;

    [Header("거절 안내")]
    [Tooltip("신병을 끌고 들어가려 할 때 누른 사람에게만 띄울 문구 — HudTable/Hud.Jail.NeedButton")]
    [SerializeField] private LocalizedString m_needButtonMessage;

    [Tooltip("안내 문구가 화면에 머무는 시간(초)")]
    [SerializeField] private float m_messageSeconds = 2.5f;

    private void Awake()
    {
        if (m_intake == null)
            m_intake = App.Game.JailIntake;

        if (m_intake == null)
            Debug.LogWarning("JailDoor: JailIntake를 찾지 못했다 — 출입·수감이 동작하지 않는다", this);
    }

    /// <summary>항상 상호작용 가능하다(잠금과 무관한 출입구).</summary>
    public bool CanInteract(GameObject interactor) => m_intake != null;

    /// <summary>서 있는 위치에 따른 출입 동작 안내 문구를 돌려준다.</summary>
    public LocalizedString PromptLabel(GameObject interactor)
    {
        JailZone zone = m_intake != null ? m_intake.Zone : null;
        if (zone == null || interactor == null)
            return null;

        if (zone.ContainsPoint(interactor.transform.position))
            return InteractPrompts.JailExit;

        return InteractPrompts.JailEnter;
    }

    /// <summary>신병을 끌고는 들어갈 수 없다 (갈래 2) — 눌러 보고 알던 것을 겨눌 때 알린다.</summary>
    public LocalizedString BlockedReason(GameObject interactor)
    {
        if (!ReferenceEquals(PromptLabel(interactor), InteractPrompts.JailEnter))
            return null;

        PlayerEscorter escorter = interactor.GetComponent<PlayerEscorter>();
        return escorter != null && escorter.TetheredCount > 0
            ? InteractPrompts.ReasonEscorting
            : null;
    }

    /// <summary>E — 셀 안이면 나오기, 밖이면 들어가기.</summary>
    public void Interact(GameObject interactor)
    {
        if (!IsSpawned)
        {
            ServerHandleInteract(interactor);
            return;
        }

        RequestInteractRpc(new NetworkObjectReference(interactor.GetComponentInParent<NetworkObject>()));
    }

    [Rpc(SendTo.Server)]
    private void RequestInteractRpc(NetworkObjectReference interactorRef)
    {
        if (interactorRef.TryGet(out NetworkObject interactor))
            ServerHandleInteract(interactor.gameObject);
    }

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

        if (zone.ContainsPoint(interactor.transform.position))
        {
            m_intake.ServerExitJail(mover);
            return;
        }

        PlayerEscorter escorter = interactor.GetComponent<PlayerEscorter>();
        if (escorter != null && escorter.TetheredCount > 0)
        {
            Debug.Log("[감옥 문] 용의자를 끌고는 들어갈 수 없다 — 옆 판정 버튼을 쓸 것");
            NotifyInteractor(interactor);
            return;
        }

        m_intake.ServerEnterJail(mover);
    }

    /// <summary>누른 클라이언트 한 명에게만 안내를 띄운다. 서버(또는 오프라인)에서 호출한다.</summary>
    private void NotifyInteractor(GameObject interactor)
    {
        if (!IsSpawned)
        {
            ShowNeedButtonLocal();
            return;
        }

        NetworkObject netObject = interactor.GetComponentInParent<NetworkObject>();
        if (netObject == null)
            return;

        if (netObject.OwnerClientId == NetworkManager.LocalClientId)
        {
            ShowNeedButtonLocal();
            return;
        }

        ShowNeedButtonRpc(RpcTarget.Single(netObject.OwnerClientId, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void ShowNeedButtonRpc(RpcParams rpcParams) => ShowNeedButtonLocal();

    private void ShowNeedButtonLocal() => App.UI.Toast?.Show(m_needButtonMessage, m_messageSeconds);
}
