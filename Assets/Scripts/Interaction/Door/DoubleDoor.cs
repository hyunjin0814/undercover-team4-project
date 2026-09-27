using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// E로 여닫는 양쪽 대문 — 문짝 두 짝이 반대로 회전하며, 닫힌 각도는 씬에 저장된 회전값이다.
/// 개폐 상태는 서버 권위로 동기화하고, 라운드 시작 전 잠금 옵션이 있다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class DoubleDoor : NetworkBehaviour, IInteractable
{
    [Header("문짝 (닫힌 자세로 저장해 둘 것)")]
    [SerializeField] private Transform m_leafLeft;
    [SerializeField] private Transform m_leafRight;

    [Header("열림 각도 (닫힌 각도 기준 상대값, Y축)")]
    [Tooltip("두 짝이 반대로 열리므로 부호가 서로 반대여야 한다. ±180도 미만으로 둘 것")]
    [SerializeField] private float m_openAngleLeft = 160f;
    [SerializeField] private float m_openAngleRight = -160f;

    [Tooltip("완전히 열리거나 닫히는 데 걸리는 시간(초)")]
    [SerializeField] private float m_swingSeconds = 0.7f;

    [Header("잠금")]
    [Tooltip("라운드가 시작될 때까지 잠가 둔다 — 준비 중 현장 선점을 막는 본부 대문용. 씬에 RoundManager가 없으면 무시된다")]
    [SerializeField] private bool m_lockedUntilRoundStart = true;

    private readonly NetworkVariable<bool> m_isOpenSynced = new NetworkVariable<bool>(false);
    private bool m_localIsOpen;

    private readonly NetworkVariable<bool> m_isLockedSynced = new NetworkVariable<bool>(false);
    private bool m_localIsLocked;

    private Quaternion m_closedLeft;
    private Quaternion m_closedRight;

    private RoundManager Round => App.Game.Round;

    public bool IsOpen => IsSpawned ? m_isOpenSynced.Value : m_localIsOpen;

    public bool IsLocked => IsSpawned ? m_isLockedSynced.Value : m_localIsLocked;

    public event System.Action<bool> OnOpenChanged;

    internal Transform LeafLeft => m_leafLeft;

    internal Transform LeafRight => m_leafRight;

    private void Awake()
    {
        if (m_leafLeft != null)
            m_closedLeft = m_leafLeft.localRotation;

        if (m_leafRight != null)
            m_closedRight = m_leafRight.localRotation;
    }

    private void Start()
    {
        if (Round != null)
            Round.OnRoundStarted += HandleRoundStarted;

        ServerRefreshRoundLock();
    }

    public override void OnDestroy()
    {
        if (Round != null)
            Round.OnRoundStarted -= HandleRoundStarted;

        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        m_isOpenSynced.OnValueChanged += HandleOpenSyncedChanged;

        ServerRefreshRoundLock();
    }

    public override void OnNetworkDespawn() => m_isOpenSynced.OnValueChanged -= HandleOpenSyncedChanged;

    private void HandleOpenSyncedChanged(bool previous, bool current) => OnOpenChanged?.Invoke(current);

    private void HandleRoundStarted() => ServerSetLocked(false);

    private void ServerRefreshRoundLock()
    {
        if (IsSpawned && !IsServer)
            return;

        bool locked = m_lockedUntilRoundStart && Round != null && Round.Phase == RoundPhase.Preparing;
        ServerSetLocked(locked);
    }

    /// <summary>문짝이 하나라도 연결돼 있으면 여닫을 수 있다. (사거리·가시선은 PlayerInteractor가 걸러 준다)</summary>
    public bool CanInteract(GameObject interactor) => m_leafLeft != null || m_leafRight != null;

    public LocalizedString PromptLabel(GameObject interactor) =>
        IsOpen ? InteractPrompts.DoorClose : InteractPrompts.DoorOpen;

    public LocalizedString BlockedReason(GameObject interactor) =>
        IsLocked ? InteractPrompts.ReasonLocked : null;

    /// <summary>E — 여닫기 토글. 잠겨 있으면 거부한다.</summary>
    public void Interact(GameObject interactor)
    {
        if (IsLocked)
        {
            Debug.Log("[문] 잠겨 있음 — 라운드 시작 전", this);
            return;
        }

        if (!IsSpawned)
        {
            ServerToggle();
            return;
        }

        RequestToggleRpc();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestToggleRpc() => ServerToggle();

    private void ServerToggle()
    {
        if (IsSpawned && !IsServer)
            return;

        if (IsLocked)
            return;

        ServerSetOpen(!IsOpen);
    }

    /// <summary>개폐 설정 — 서버(또는 오프라인) 전용. 외부 강제(연출·치트)도 이 지점을 지난다.</summary>
    public void ServerSetOpen(bool open)
    {
        if (IsSpawned && !IsServer)
            return;

        if (IsOpen == open)
            return;

        m_localIsOpen = open;

        if (IsSpawned && IsServer)
            m_isOpenSynced.Value = open;
        else if (!IsSpawned)
            OnOpenChanged?.Invoke(open);
    }

    /// <summary>잠금 설정 — 서버(또는 오프라인) 전용. 잠글 때 열려 있으면 함께 닫는다.</summary>
    public void ServerSetLocked(bool locked)
    {
        if (IsSpawned && !IsServer)
            return;

        if (IsLocked == locked)
            return;

        m_localIsLocked = locked;

        if (IsSpawned && IsServer)
            m_isLockedSynced.Value = locked;

        if (locked && IsOpen)
            ServerSetOpen(false);
    }

    private void Update()
    {
        Swing(m_leafLeft, m_closedLeft, m_openAngleLeft);
        Swing(m_leafRight, m_closedRight, m_openAngleRight);
    }

    private void Swing(Transform leaf, Quaternion closed, float openAngle)
    {
        if (leaf == null)
            return;

        Quaternion target = IsOpen ? closed * Quaternion.Euler(0f, openAngle, 0f) : closed;
        if (leaf.localRotation == target)
            return;

        float speed = m_swingSeconds > 0f ? Mathf.Abs(openAngle) / m_swingSeconds : float.MaxValue;
        leaf.localRotation = Quaternion.RotateTowards(leaf.localRotation, target, speed * Time.deltaTime);
    }
}
