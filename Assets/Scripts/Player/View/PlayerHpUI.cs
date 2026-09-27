using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 로컬 플레이어의 HP를 좌하단 HpBarView로 표시한다. 오너 전용.
/// </summary>
[RequireComponent(typeof(PlayerHealth))]
public class PlayerHpUI : NetworkBehaviour
{
    [Header("UI 컴포넌트 연결")]
    [Tooltip("좌하단 HpPanel에 붙은 HP 바 뷰를 드래그하여 연결하세요.")]
    [SerializeField]
    private HpBarView m_gauge;

    private PlayerHealth m_health;

    private bool IsLocalOwner => !IsSpawned || m_isLocalPlayer;

    private bool m_isLocalPlayer;

    private void Awake()
    {
        m_health = GetComponent<PlayerHealth>();
    }

    public override void OnNetworkSpawn()
    {
        m_isLocalPlayer = IsOwner;

        if (IsOwner)
            return;

        if (m_gauge != null)
            m_gauge.gameObject.SetActive(false);

        enabled = false;
    }

    private void Update()
    {
        if (!IsLocalOwner || m_gauge == null || m_health == null)
            return;

        m_gauge.SetHealth(m_health.CurrentHp, m_health.MaxHp);
    }
}
