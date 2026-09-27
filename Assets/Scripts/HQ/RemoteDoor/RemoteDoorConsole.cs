using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 본부 원격 문 개방 콘솔 상태 보유자 — 문 목록과 선택된 문을 서버 권위로 동기화한다.
/// 선택된 문의 서버 API를 호출하며, 잠긴 문도 열 수 있다.
/// </summary>
public class RemoteDoorConsole : NetworkBehaviour
{
    [Tooltip("이 콘솔로 여닫을 문들 — 순서가 곧 모니터 목록 순서다")]
    [SerializeField]
    private InteractableDoor[] m_doors;

    [Tooltip("한 번에 하나만 — 문을 열 때 이 콘솔의 다른 문을 모두 닫는다")]
    [SerializeField]
    private bool m_singleOpenOnly = true;

    private readonly NetworkVariable<int> m_selectedIndex = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private DoorMinimapMarker[] m_markers;

    public int DoorCount => m_doors != null ? m_doors.Length : 0;
    public int SelectedIndex => m_selectedIndex.Value;

    public InteractableDoor SelectedDoor => GetDoor(SelectedIndex);

    /// <summary>목록 index번째 문 — 범위를 벗어나거나 미배선이면 null. 모니터 표시용.</summary>
    public InteractableDoor GetDoor(int index) =>
        m_doors != null && index >= 0 && index < m_doors.Length ? m_doors[index] : null;

    public event Action OnConsoleChanged;

    public override void OnNetworkSpawn()
    {
        CacheMarkers();
        m_selectedIndex.OnValueChanged += HandleSelectedIndexChanged;

        for (int i = 0; i < DoorCount; i++)
        {
            if (m_doors[i] != null)
                m_doors[i].OnOpenChanged += HandleDoorOpenChanged;
        }

        Apply();
    }

    private void CacheMarkers()
    {
        int count = DoorCount;
        m_markers = new DoorMinimapMarker[count];
        for (int i = 0; i < count; i++)
        {
            if (m_doors[i] == null)
                continue;

            m_markers[i] = m_doors[i].GetComponentInChildren<DoorMinimapMarker>();
        }
    }

    public override void OnNetworkDespawn()
    {
        m_selectedIndex.OnValueChanged -= HandleSelectedIndexChanged;

        for (int i = 0; i < DoorCount; i++)
        {
            if (m_doors[i] != null)
                m_doors[i].OnOpenChanged -= HandleDoorOpenChanged;
        }
    }

    private void HandleSelectedIndexChanged(int previous, int current) => Apply();

    private void HandleDoorOpenChanged(bool open) => Apply();

    private void Apply()
    {
        for (int i = 0; i < DoorCount; i++)
        {
            if (m_markers != null && i < m_markers.Length && m_markers[i] != null)
                m_markers[i].SetSelected(i == SelectedIndex);
        }

        OnConsoleChanged?.Invoke();
    }

    /// <summary>목록 이동 요청 — 이전/다음 버튼이 부른다. delta는 -1 또는 +1.</summary>
    [Rpc(SendTo.Server)]
    public void RequestSelectRpc(int delta)
    {
        int count = DoorCount;
        if (count == 0 || delta == 0)
            return;

        m_selectedIndex.Value = ((m_selectedIndex.Value + delta) % count + count) % count;
    }

    /// <summary>선택된 문 개폐 요청 — 개방 버튼이 부른다. 잠금과 무관하게 여닫는다.</summary>
    [Rpc(SendTo.Server)]
    public void RequestToggleSelectedRpc()
    {
        InteractableDoor door = SelectedDoor;
        if (door == null)
            return;

        bool open = !door.IsOpen;

        if (open && m_singleOpenOnly)
            ServerCloseOthers(door);

        door.ServerSetOpen(open);
    }

    private void ServerCloseOthers(InteractableDoor except)
    {
        for (int i = 0; i < DoorCount; i++)
        {
            if (m_doors[i] == null || m_doors[i] == except)
                continue;

            m_doors[i].ServerSetOpen(false);
        }
    }
}
