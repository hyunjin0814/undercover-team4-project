using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 감옥 문 옆 수감 버튼 — 눌렸다는 사실만 서버로 넘기고, 판정·배치는 JailIntake가 한다.
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

    public event System.Action<int> OnAdmitted;

    private void Awake()
    {
        if (m_intake == null)
            m_intake = App.Game.JailIntake;

        if (m_intake == null)
            Debug.LogWarning("JailIntakeButton: JailIntake를 찾지 못했다 — 수감이 동작하지 않는다", this);
    }

    /// <summary>항상 상호작용 가능하다. 신병이 없으면 BlockedReason으로 알린다.</summary>
    public bool CanInteract(GameObject interactor) => m_intake != null;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.JailAdmit;

    /// <summary>확보한 신병이 없으면 막힌 사유를 돌려준다.</summary>
    public LocalizedString BlockedReason(GameObject interactor) =>
        m_intake != null && !m_intake.HasAdmittableCustody(interactor)
            ? InteractPrompts.ReasonNoCustody
            : null;

    /// <summary>E — 확보한 신병을 그 자리에서 판정해 감옥으로 보낸다.</summary>
    public void Interact(GameObject interactor)
    {
        if (!IsSpawned)
        {
            ServerAdmit(interactor);
            return;
        }

        NetworkObject netObject = interactor.GetComponentInParent<NetworkObject>();
        if (netObject != null)
            RequestAdmitRpc(new NetworkObjectReference(netObject));
    }

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

    /// <summary>누른 클라이언트 한 명에게만 안내를 띄운다. 서버(또는 오프라인)에서 호출한다.</summary>
    private void NotifyInteractor(GameObject interactor)
    {
        if (!IsSpawned)
        {
            ShowNoCustodyLocal();
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

    private void ShowNoCustodyLocal() => App.UI.Toast?.Show(m_noCustodyMessage, m_messageSeconds);
}
