using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 납치 결말용 맨홀 뚜껑을 서버가 여닫고 전 피어에 동기화한다.
/// 맨홀 지점이나 그 자식에 붙이며, 뚜껑 참조는 선택이다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class AbductionManhole : NetworkBehaviour
{
    [Tooltip("열릴 뚜껑 — 비워 두면 연출 없이 결말만 난다")]
    [SerializeField] private Transform m_lid;

    [Tooltip("뚜껑이 옆으로 밀려나는 거리(m)")]
    [Min(0f)]
    [SerializeField] private float m_slideDistance = 0.9f;

    [Tooltip("뚜껑이 다 밀리는 데 걸리는 시간(초) — 이벤트의 뚜껑 대기 시간보다 짧게 둘 것")]
    [Min(0.05f)]
    [SerializeField] private float m_slideSeconds = 1.2f;

    [Tooltip("뚜껑 여는 소리 — 없으면 무음")]
    [SerializeField] private AudioSource m_openSound;

    private readonly NetworkVariable<bool> m_open = new NetworkVariable<bool>();

    private bool m_openLocal;

    private Vector3 m_closedPosition;
    private float m_progress;
    private bool m_wasOpen;

    private bool IsOpen => IsSpawned ? m_open.Value : m_openLocal;

    private void Awake()
    {
        if (m_lid != null)
            m_closedPosition = m_lid.localPosition;
    }

    private void Update()
    {
        bool open = IsOpen;

        if (open != m_wasOpen)
        {
            m_wasOpen = open;
            if (open && m_openSound != null)
                m_openSound.Play();
        }

        if (m_lid == null)
            return;

        float target = open ? 1f : 0f;
        if (Mathf.Approximately(m_progress, target))
            return;

        m_progress = Mathf.MoveTowards(
            m_progress, target, Time.deltaTime / Mathf.Max(m_slideSeconds, 0.05f));
        m_lid.localPosition = m_closedPosition + Vector3.right * (m_slideDistance * m_progress);
    }

    /// <summary>뚜껑을 연다 — 맨홀 도착 시. 서버(또는 오프라인) 전용.</summary>
    public void ServerOpen() => SetOpen(true);

    /// <summary>뚜껑을 닫는다 — 구조 성공·라운드 정리. 서버(또는 오프라인) 전용.</summary>
    public void ServerClose() => SetOpen(false);

    private void SetOpen(bool value)
    {
        if (IsSpawned && !IsServer)
            return;

        m_openLocal = value;
        if (IsSpawned)
            m_open.Value = value;
    }
}
