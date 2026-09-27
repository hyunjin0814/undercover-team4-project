using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 장착 아이템이 휴대용 미니맵이면 HUD 위젯을 켠다. 오너 클라에서만 동작한다.
/// </summary>
public class PortableMinimapWatcher : NetworkBehaviour
{
    private PlayerItemUser m_itemUser;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        m_itemUser = GetComponentInParent<PlayerItemUser>();
        if (m_itemUser == null)
        {
            Debug.LogWarning("PortableMinimapWatcher: PlayerItemUser를 찾지 못함", this);
            return;
        }

        m_itemUser.OnEquippedItemChanged += HandleEquippedItemChanged;
        HandleEquippedItemChanged(m_itemUser.EquippedItem);
    }

    public override void OnNetworkDespawn()
    {
        if (m_itemUser != null)
            m_itemUser.OnEquippedItemChanged -= HandleEquippedItemChanged;
    }

    private void HandleEquippedItemChanged(ItemBase item) =>
        App.UI.PortableMinimap?.SetVisible(item is PortableMinimap);
}
