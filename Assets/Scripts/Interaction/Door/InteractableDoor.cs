using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

public enum DoorOpenDirection
{
    [InspectorName("왼쪽")]
    Left,

    [InspectorName("오른쪽")]
    Right,

    [InspectorName("위")]
    Up,

    [InspectorName("아래")]
    Down,
}

/// <summary>
/// 본부 원격 단말로 여닫는 미닫이 문 — 개폐·잠금 상태를 서버 권위로 동기화하고 문짝을 로컬로 슬라이드시킨다.
/// </summary>
public class InteractableDoor : NetworkBehaviour, IInteractable
{
    [Header("식별")]
    [Tooltip("본부 원격 단말 목록에 표시할 이름 - 예: 창고 후문")]
    [SerializeField] private string m_doorLabel = "미지정";

    [Header("문짝 (미닫이)")]
    [SerializeField] private Transform m_leaf;
    [Tooltip("양문형 반대쪽 문짝 — 비우면 단문. 방향은 아래 설정의 반대로 자동 적용된다")]
    [SerializeField] private Transform m_leafOpposite;
    [Tooltip("문짝이 밀려나는 방향(문짝의 부모 기준). 양문형이면 반대쪽 문짝은 이 반대로 열린다")]
    [SerializeField] private DoorOpenDirection m_openDirection = DoorOpenDirection.Left;
    [Tooltip("문짝 하나가 밀려나는 거리(m) — 단문은 개구부 폭, 양문은 그 절반을 준다")]
    [SerializeField] private float m_openDistance = 1.05f;
    [SerializeField] private float m_slideSeconds = 0.7f;

    [Header("잠금")]
    [Tooltip("잠긴 문은 현장 E로 열리지 않는다 - 본부 원격 단말만 연다")]
    [SerializeField] private bool m_startLocked;

    [Header("자동 닫힘")]
    [Tooltip("열린 뒤 이 시간(초)이 지나면 서버가 다시 닫는다. 0이면 자동으로 닫히지 않는다")]
    [SerializeField] private float m_autoCloseSeconds;

    private readonly NetworkVariable<bool> m_isOpenSynced = new NetworkVariable<bool>(false);
    private readonly NetworkVariable<bool> m_isLockedSynced = new NetworkVariable<bool>(false);
    private bool m_localIsOpen;
    private bool m_localIsLocked;
    private Vector3 m_closedLocalPosition;
    private Vector3 m_closedLocalPositionOpposite;

    private float m_autoCloseTimer;

    public string DoorLabel => m_doorLabel;
    public bool IsOpen => IsSpawned ? m_isOpenSynced.Value : m_localIsOpen;
    public bool IsLocked => IsSpawned ? m_isLockedSynced.Value : m_localIsLocked;
    public event System.Action<bool> OnOpenChanged;

    private void Awake()
    {
        if (m_leaf != null) m_closedLocalPosition = m_leaf.localPosition;
        if (m_leafOpposite != null) m_closedLocalPositionOpposite = m_leafOpposite.localPosition;
        m_localIsLocked = m_startLocked;
    }

    public override void OnNetworkSpawn()
    {
        m_isOpenSynced.OnValueChanged += HandleOpenSyncedChanged;
        if (IsServer) m_isLockedSynced.Value = m_startLocked;
    }

    public override void OnNetworkDespawn() => m_isOpenSynced.OnValueChanged -= HandleOpenSyncedChanged;

    private void HandleOpenSyncedChanged(bool previous, bool current) => OnOpenChanged?.Invoke(current);

    public bool CanInteract(GameObject interactor) => m_leaf != null;

    public LocalizedString PromptLabel(GameObject interactor) =>
        IsOpen ? InteractPrompts.DoorClose : InteractPrompts.DoorOpen;

    public LocalizedString BlockedReason(GameObject interactor) =>
        IsLocked ? InteractPrompts.ReasonLocked : null;

    public void Interact(GameObject interactor)
    {
        if (IsLocked)
        {
            Debug.Log($"[문] {m_doorLabel} 잠겨 있음 — 본부 개방 필요", this);
            return;
        }
        if (!IsSpawned) { ServerToggleByField(); return; }

        RequestFieldToggleRpc();
    }

    [Rpc(SendTo.Server)]
    private void RequestFieldToggleRpc() => ServerToggleByField();

    private void ServerToggleByField()
    {
        if (IsSpawned && !IsServer) return;
        if (IsLocked) return;
        ServerSetOpen(!IsOpen);
    }

    /// <summary>개폐 설정 — 서버(또는 오프라인) 전용. 모든 개폐가 이 지점을 지난다.</summary>
    public void ServerSetOpen(bool open)
    {
        if (IsSpawned && !IsServer) return;
        if (IsOpen == open) return;

        m_localIsOpen = open;

        m_autoCloseTimer = open ? m_autoCloseSeconds : 0f;

        if (IsSpawned && IsServer)
            m_isOpenSynced.Value = open;
        else if (!IsSpawned)
            OnOpenChanged?.Invoke(open);
    }

    /// <summary>잠금 설정 — 서버(또는 오프라인) 전용.</summary>
    public void ServerSetLocked(bool locked)
    {
        if (IsSpawned && !IsServer) return;
        if (IsLocked == locked) return;

        m_localIsLocked = locked;

        if (IsSpawned && IsServer)
            m_isLockedSynced.Value = locked;
    }

    private void Update()
    {
        if (!IsSpawned || IsServer)
            TickAutoClose();

        TickLeafSlide();
    }

    private void TickAutoClose()
    {
        if (m_autoCloseSeconds <= 0f || !IsOpen)
            return;

        m_autoCloseTimer -= Time.deltaTime;
        if (m_autoCloseTimer <= 0f)
            ServerSetOpen(false);
    }

    private Vector3 OpenOffset =>
        m_openDirection switch
        {
            DoorOpenDirection.Right => Vector3.right,
            DoorOpenDirection.Up => Vector3.up,
            DoorOpenDirection.Down => Vector3.down,
            _ => Vector3.left,
        } * m_openDistance;

    private void TickLeafSlide()
    {
        Vector3 offset = OpenOffset;
        TickLeaf(m_leaf, m_closedLocalPosition, offset);
        TickLeaf(m_leafOpposite, m_closedLocalPositionOpposite, -offset);
    }

    private void TickLeaf(Transform leaf, Vector3 closedLocalPosition, Vector3 offset)
    {
        if (leaf == null) return;

        Vector3 target = IsOpen ? closedLocalPosition + offset : closedLocalPosition;
        if (leaf.localPosition == target) return;

        float speed = m_slideSeconds > 0f ? m_openDistance / m_slideSeconds : float.MaxValue;
        leaf.localPosition = Vector3.MoveTowards(leaf.localPosition, target, speed * Time.deltaTime);
    }
}
