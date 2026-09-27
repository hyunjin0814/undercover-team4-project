using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 돌발 이벤트를 F1~F12·넘패드 키로 즉시 발동하는 에디터 전용 단축키.
/// 번호는 인스펙터 이벤트 풀 순서이며, 시작 시 매핑을 콘솔에 출력한다. 서버에서만 동작한다.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class SuddenEventDevHotkeys : MonoBehaviour
{
#if UNITY_EDITOR
    private static readonly Key[] k_keys =
    {
        Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7, Key.F8,
        Key.F10, Key.F11, Key.F12,
        Key.Numpad1, Key.Numpad2, Key.Numpad3, Key.Numpad4, Key.Numpad5,
        Key.Numpad6, Key.Numpad7, Key.Numpad8, Key.Numpad9,
    };

    [Tooltip("끄면 단축키가 듣지 않는다 — 같은 키를 쓰는 다른 테스트를 할 때 잠깐 내린다")]
    [SerializeField] private bool m_enabled = true;

    [Tooltip("Play 시작 시 어떤 키에 무슨 이벤트가 물렸는지 콘솔에 찍는다")]
    [SerializeField] private bool m_logMappingOnStart = true;

    private SuddenEventManager m_manager;

    private void Awake() => m_manager = GetComponent<SuddenEventManager>();

    private void Start()
    {
        if (m_logMappingOnStart)
            LogMapping();
    }

    private void Update()
    {
        if (!m_enabled || m_manager == null)
            return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        for (int i = 0; i < k_keys.Length; i++)
        {
            if (keyboard[k_keys[i]].wasPressedThisFrame)
                m_manager.ForceTrigger(i);
        }
    }

    /// <summary>지금 어떤 키에 무엇이 물렸는지 콘솔에 찍는다 — 풀 순서를 바꿨을 때 확인용.</summary>
    [ContextMenu("Debug/단축키 매핑 찍기")]
    private void LogMapping()
    {
        if (m_manager == null)
            m_manager = GetComponent<SuddenEventManager>();

        var sb = new System.Text.StringBuilder(
            "[돌발이벤트] 개발자 단축키 매핑 (F9는 청탁 몫이라 건너뜀 · F 열 다음은 넘패드 1~9)");

        for (int i = 0; i < k_keys.Length; i++)
        {
            string eventName = m_manager.EventNameAt(i);
            if (eventName == null)
                break;

            sb.AppendLine();
            sb.Append($"  {k_keys[i]} -> {eventName}");
        }

        if (m_manager.EventCount == 0)
        {
            sb.AppendLine();
            sb.Append("  ⚠ 이벤트 풀이 비어 있다 — SuddenEventManager의 인스펙터 리스트를 확인할 것");
        }
        else if (m_manager.EventCount > k_keys.Length)
        {
            sb.AppendLine();
            sb.Append($"  ⚠ 이벤트 {m_manager.EventCount}개 중 앞의 {k_keys.Length}개만 물렸다");
            sb.Append(" — 키가 모자라니 풀 순서를 바꿔서 테스트할 것");
        }

        Debug.Log(sb.ToString(), this);
    }
#endif
}
