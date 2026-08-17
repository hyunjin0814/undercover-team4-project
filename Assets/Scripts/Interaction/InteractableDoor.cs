using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 미닫이 문짝이 열릴 때 밀려나는 방향 — <b>문짝의 부모 기준 로컬 축</b>이라,
/// 문 전체를 씬에서 회전시켜도 "왼쪽"은 그 문에서 본 왼쪽으로 유지된다.
/// 위/아래는 셔터(올라가는 문)·바닥 해치에 쓴다.
/// </summary>
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

    // 자동 닫힘까지 남은 시간 — 서버(또는 오프라인)만 센다. 결과인 개폐 상태만 동기화되므로
    // 클라는 이 값을 알 필요가 없다 (m_manualOpen을 동기화하지 않는 JailDoor와 같은 이유).
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
        // 잠금 초기값은 인스펙터 값이라 NetworkVariable 생성자에 넣을 수 없다 — 서버가 스폰 때 밀어준다
        if (IsServer) m_isLockedSynced.Value = m_startLocked;
    }

    public override void OnNetworkDespawn() => m_isOpenSynced.OnValueChanged -= HandleOpenSyncedChanged;

    private void HandleOpenSyncedChanged(bool previous, bool current) => OnOpenChanged?.Invoke(current);

    // 잠겨 있어도 true — 아웃라인이 떠야 "문이 있고 잠겼다"를 알 수 있다. 거부는 Interact에서 한다.
    public bool CanInteract(GameObject interactor) => m_leaf != null;

    // 조준 안내 (#664). 열림 여부로 동작이 갈리고, 잠겼으면 사유가 붙어 회색으로 뜬다 —
    // CanInteract가 true라 윤곽선만 뜨던 자리에 "왜 안 열리는지"가 들어간다.
    public LocalizedString PromptLabel(GameObject interactor) =>
        IsOpen ? InteractPrompts.DoorClose : InteractPrompts.DoorOpen;

    public LocalizedString BlockedReason(GameObject interactor) =>
        IsLocked ? InteractPrompts.ReasonLocked : null;

    public void Interact(GameObject interactor)
    {
        if (IsLocked)
        {
            // 거부 피드백 — 소리/HUD 문구
            Debug.Log($"[문] {m_doorLabel} 잠겨 있음 — 본부 개방 필요", this);
            return;
        }
        if (!IsSpawned) { ServerToggleByField(); return; } // 오프라인 단독 테스트

        RequestFieldToggleRpc();
    }

    [Rpc(SendTo.Server)]
    private void RequestFieldToggleRpc() => ServerToggleByField();

    private void ServerToggleByField()
    {
        if (IsSpawned && !IsServer) return;
        if (IsLocked) return; // 클라 게이트는 신뢰 대상이 아니다 — 서버에서 한 번 더
        ServerSetOpen(!IsOpen);
    }

    // 본부 원격 개폐는 여기에 진입점을 두지 않는다 — 본부 콘솔(RemoteDoorConsole)이 이미 서버에서
    // 돌고 있으므로 아래 ServerSetOpen을 직접 부른다. 클라 요청을 서버로 넘기는 것은 콘솔의 몫이다.

    /// <summary>개폐 설정 — 서버(또는 오프라인) 전용. 모든 개폐가 이 지점을 지난다.</summary>
    public void ServerSetOpen(bool open)
    {
        if (IsSpawned && !IsServer) return;
        if (IsOpen == open) return;

        m_localIsOpen = open;

        // 열 때마다 자동 닫힘 시간을 다시 채운다 — 누가 열었는지(현장 E·본부 원격)와 무관하다
        m_autoCloseTimer = open ? m_autoCloseSeconds : 0f;

        if (IsSpawned && IsServer)
            m_isOpenSynced.Value = open;   // OnValueChanged를 거쳐 모든 피어에서 이벤트 발생
        else if (!IsSpawned)
            OnOpenChanged?.Invoke(open);   // 오프라인 — 동기화 콜백이 없으므로 직접 알린다
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
        // 자동 닫힘 판단은 서버(또는 오프라인)만 한다 — 클라는 동기화된 IsOpen을 보고 연출만 따라간다
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

    // 방향 드롭다운 + 거리를 문짝의 부모 기준 오프셋으로 환산한다
    private Vector3 OpenOffset =>
        m_openDirection switch
        {
            DoorOpenDirection.Right => Vector3.right,
            DoorOpenDirection.Up => Vector3.up,
            DoorOpenDirection.Down => Vector3.down,
            _ => Vector3.left,
        } * m_openDistance;

    // 양문형은 반대쪽 문짝을 오프셋 부호만 뒤집어 함께 민다 — 두 짝이 같은 부모 아래 나란히 있는 배치가 전제다
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

        // 속도는 완주 시간에서 역산 — 거리를 바꿔도 체감 속도가 유지된다
        float speed = m_slideSeconds > 0f ? m_openDistance / m_slideSeconds : float.MaxValue;
        leaf.localPosition = Vector3.MoveTowards(leaf.localPosition, target, speed * Time.deltaTime);
    }
}
