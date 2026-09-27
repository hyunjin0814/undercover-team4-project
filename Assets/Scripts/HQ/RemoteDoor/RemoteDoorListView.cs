using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// 원격 개방 문 목록을 문마다 한 줄(이름·상태)로 모니터 TMP 텍스트에 표시한다.
/// </summary>
public class RemoteDoorListView : MonoBehaviour
{
    [SerializeField]
    private RemoteDoorConsole m_console;

    [SerializeField]
    private TMP_Text m_label;

    [Tooltip("한 줄 형식 — {0}=선택 표시, {1}=문 이름, {2}=상태")]
    [SerializeField]
    private string m_rowFormat = "{0} {1} — {2}";

    [Tooltip("선택된 줄 앞에 붙는 표시")]
    [SerializeField]
    private string m_selectedMarker = "▶";

    private readonly StringBuilder m_builder = new StringBuilder();

    private void OnEnable()
    {
        if (m_console != null)
            m_console.OnConsoleChanged += Refresh;
        Refresh();
    }

    private void OnDisable()
    {
        if (m_console != null)
            m_console.OnConsoleChanged -= Refresh;
    }

    private void Refresh()
    {
        if (m_label == null)
            return;

        if (m_console == null || !m_console.IsSpawned)
        {
            m_label.text = string.Empty;
            return;
        }

        int count = m_console.DoorCount;
        if (count == 0)
        {
            m_label.text = "등록된 문 없음";
            return;
        }

        m_builder.Clear();
        for (int i = 0; i < count; i++)
        {
            InteractableDoor door = m_console.GetDoor(i);
            string marker = i == m_console.SelectedIndex ? m_selectedMarker : " ";

            if (i > 0)
                m_builder.AppendLine();

            m_builder.AppendFormat(
                m_rowFormat,
                marker,
                door != null ? door.DoorLabel : "(미배선)",
                DescribeState(door)
            );
        }

        m_label.text = m_builder.ToString();
    }

    private static string DescribeState(InteractableDoor door)
    {
        if (door == null)
            return "-";
        if (door.IsOpen)
            return "열림";

        return door.IsLocked ? "잠김" : "닫힘";
    }
}
