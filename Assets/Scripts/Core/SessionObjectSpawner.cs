using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 세션(서버) 시작 시 세션 내내 유지할 상주 네트워크 오브젝트(팀 자금 등)를 스폰한다.
/// OnServerStarted가 발화하는 Title 씬에만 배치할 것.
/// </summary>
public class SessionObjectSpawner : MonoBehaviour
{
    [Tooltip("세션 동안 유지할 프리팹들 (DefaultNetworkPrefabs에 등록 필수)")]
    [SerializeField] private NetworkObject[] m_persistentPrefabs;

    private NetworkManager m_nm;
    private bool m_spawned;

    private void Start()
    {
        m_nm = NetworkManager.Singleton;
        if (m_nm != null)
        {
            m_nm.OnServerStarted += HandleServerStarted;
            m_nm.OnServerStopped += HandleServerStopped;
        }
    }

    private void OnDestroy()
    {
        if (m_nm != null)
        {
            m_nm.OnServerStarted -= HandleServerStarted;
            m_nm.OnServerStopped -= HandleServerStopped;
        }
    }

    private void HandleServerStopped(bool _)
    {
        m_spawned = false;
    }

    private void HandleServerStarted()
    {
        if (m_spawned)
            return;
        m_spawned = true;

        foreach (NetworkObject prefab in m_persistentPrefabs)
        {
            if (prefab == null) continue;
            Instantiate(prefab).Spawn(destroyWithScene: false);
        }
    }
}
