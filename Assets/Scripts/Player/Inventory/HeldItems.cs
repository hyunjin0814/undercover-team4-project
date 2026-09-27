using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어가 부착해 들고 있는 아이템 집합 — 부착 지점의 자식 목록을 진실로 삼는다.
/// 개수·소속 판정·부착·디스폰만 담당한다.
/// </summary>
public sealed class HeldItems
{
    private readonly Transform m_anchor;

    public HeldItems(Transform anchor)
    {
        m_anchor = anchor;
    }

    private bool IsUsable => m_anchor != null;

    public int Count => CountOf<ItemBase>();

    /// <summary>TComponent를 가진 소지품 수를 센다(무할당).</summary>
    public int CountOf<TComponent>()
        where TComponent : Component
    {
        if (!IsUsable)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < m_anchor.childCount; i++)
        {
            if (m_anchor.GetChild(i).GetComponent<TComponent>() != null)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>이 아이템을 지금 들고 있는가 — 부착 여부로 판정한다(버리기 권한 검증용).</summary>
    public bool Holds(NetworkObject item) =>
        item != null && IsUsable && item.transform.parent == m_anchor;

    /// <summary>TComponent를 가진 첫 소지품을 돌려준다. 없으면 null(무할당).</summary>
    public TComponent FirstOf<TComponent>()
        where TComponent : Component
    {
        if (!IsUsable)
        {
            return null;
        }

        for (int i = 0; i < m_anchor.childCount; i++)
        {
            TComponent found = m_anchor.GetChild(i).GetComponent<TComponent>();
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>소지품을 into에 담는다(기존 내용은 지운다) — 목록이 필요한 쪽만 쓴다.</summary>
    public void CollectInto(List<ItemBase> into)
    {
        into.Clear();
        if (!IsUsable)
        {
            return;
        }

        for (int i = 0; i < m_anchor.childCount; i++)
        {
            ItemBase item = m_anchor.GetChild(i).GetComponent<ItemBase>();
            if (item != null)
            {
                into.Add(item);
            }
        }
    }

    /// <summary>아이템을 부착하고 로컬 원점에 맞춘다. 서버에서만 호출 — 부착 자체는 NGO가 복제한다.</summary>
    public void Attach(NetworkObject item)
    {
        item.TrySetParent(m_anchor, false);
        item.transform.localPosition = Vector3.zero;
        item.transform.localRotation = Quaternion.identity;
    }

    /// <summary>오너에게 보낼 보유 목록을 만든다. 디스폰된 아이템은 건너뛴다.</summary>
    public NetworkObjectReference[] BuildRefs()
    {
        if (!IsUsable)
        {
            return Array.Empty<NetworkObjectReference>();
        }

        List<NetworkObjectReference> refs = new List<NetworkObjectReference>();
        for (int i = 0; i < m_anchor.childCount; i++)
        {
            ItemBase item = m_anchor.GetChild(i).GetComponent<ItemBase>();
            if (item != null && item.NetworkObject != null && item.NetworkObject.IsSpawned)
            {
                refs.Add(new NetworkObjectReference(item.NetworkObject));
            }
        }

        return refs.ToArray();
    }

    /// <summary>들고 있는 아이템을 전부 디스폰한다. 서버 전용.</summary>
    public int DespawnAll()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening || !IsUsable)
        {
            return 0;
        }

        List<NetworkObject> held = new List<NetworkObject>();
        for (int i = 0; i < m_anchor.childCount; i++)
        {
            ItemBase item = m_anchor.GetChild(i).GetComponent<ItemBase>();
            if (item != null && item.NetworkObject != null)
            {
                item.ServerCancelActiveUse();
                held.Add(item.NetworkObject);
            }
        }

        foreach (NetworkObject item in held)
        {
            if (item != null && item.IsSpawned)
            {
                item.Despawn(destroy: true);
            }
        }

        return held.Count;
    }
}
