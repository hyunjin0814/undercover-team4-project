using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 소매치기 NPC의 탈취 행동 — 피해자 소지품을 채 NPC 밑으로 옮기고, 제압 시 떨어뜨리거나 라운드 종료 시 없앤다.
/// 서버 전용으로 런타임에 부착된다.
/// </summary>
public class Pickpocket : MonoBehaviour
{
    private readonly List<ItemBase> m_candidates = new List<ItemBase>();

    private ItemBase m_stolen;

    /// <summary>피해자 소지품 하나를 무작위로 채 이 NPC 밑으로 옮긴다. 뺏을 것이 없으면 null.</summary>
    public ItemBase ServerStealFrom(PlayerLoadout victim)
    {
        if (victim == null)
            return null;

        victim.CollectDetachableItems(m_candidates);
        if (m_candidates.Count == 0)
            return null;

        ItemBase stolen = m_candidates[Random.Range(0, m_candidates.Count)];
        m_candidates.Clear();

        stolen.ServerCancelActiveUse();

        NetworkObject stolenObject = stolen.NetworkObject;
        stolenObject.RemoveOwnership();
        stolenObject.TrySetParent(transform, false);
        stolenObject.transform.localPosition = Vector3.zero;
        stolenObject.transform.localRotation = Quaternion.identity;

        m_stolen = stolen;
        victim.ServerNotifyHeldItemsChanged();
        return stolen;
    }

    private const float k_dropSideDistance = 0.8f;
    private const float k_dropHeight = 0.2f;

    private const int k_groundMask = 1;

    /// <summary>훔친 물건을 몸 옆에 떨어뜨린다(무력화 시).</summary>
    public void ServerDropStolenItem()
    {
        if (m_stolen == null)
            return;

        NetworkObject stolenObject = m_stolen.NetworkObject;
        m_stolen = null;

        if (stolenObject == null || !stolenObject.IsSpawned)
            return;

        Vector3 dropPosition = ResolveDropPosition();
        stolenObject.transform.SetPositionAndRotation(dropPosition, Quaternion.identity);
        WorldItemPickup.SettleOnGround(stolenObject.gameObject, dropPosition.y);
        stolenObject.TrySetParent((Transform)null, true);
    }

    private Vector3 ResolveDropPosition()
    {
        Vector3 origin = transform.position + Vector3.up * k_dropHeight;

        Vector3 side = transform.right;
        side.y = 0f;
        if (side.sqrMagnitude < 0.01f)
            side = Vector3.right;
        side.Normalize();

        Vector3 candidate = origin;

        foreach (Vector3 dir in new[] { side, -side })
        {
            if (!Physics.Raycast(origin, dir, k_dropSideDistance, ~0, QueryTriggerInteraction.Ignore))
            {
                candidate = origin + dir * k_dropSideDistance;
                break;
            }
        }

        return DeliveryScatter.SnapToGround(candidate, k_groundMask);
    }

    /// <summary>놓쳤다 — 물건을 없앤다. 구매품이면 다음 라운드 배달 목록에서도 빼 영구 손실로 만든다.</summary>
    public void ServerLoseStolenItem()
    {
        if (m_stolen == null)
            return;

        ItemBase stolen = m_stolen;
        m_stolen = null;

        if (stolen.TryGetComponent(out ShopDeliveredItem delivered))
            App.Game.ShopPurchases?.RemoveCarried(delivered.SourcePrefab);

        NetworkObject stolenObject = stolen.NetworkObject;
        if (stolenObject != null && stolenObject.IsSpawned)
            stolenObject.Despawn(destroy: true);
    }
}
