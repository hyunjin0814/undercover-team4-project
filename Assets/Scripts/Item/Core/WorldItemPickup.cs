using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 월드에 놓인 아이템을 줍는 상호작용 진입점 — 바닥에 놓인 모습을 HeldModelPrefab으로 로컬 생성해 보여준다.
/// 들고 있을 때는 월드 비주얼·콜라이더를 끄고, 줍기 요청은 PlayerLoadout으로 넘긴다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class WorldItemPickup : MonoBehaviour, IInteractable
{
    private static readonly Vector3 k_minPickupSize = new Vector3(0.5f, 0.5f, 0.5f);

    private NetworkObject m_networkObject;
    private GameObject m_worldVisual;
    private BoxCollider m_pickupCollider;
    private DroppedItemHighlight m_highlight;

    public bool IsHeld => transform.parent != null;

    private void Awake()
    {
        m_networkObject = GetComponent<NetworkObject>();
        BuildWorldVisual();

        m_highlight = gameObject.AddComponent<DroppedItemHighlight>();
        m_highlight.Initialize();

        RefreshWorldPresence();
    }

    private void BuildWorldVisual()
    {
        ItemBase item = GetComponent<ItemBase>();
        if (item != null && item.HeldModelPrefab != null)
        {
            m_worldVisual = Instantiate(item.HeldModelPrefab, transform);
            m_worldVisual.transform.localPosition = Vector3.zero;
            m_worldVisual.transform.localRotation = Quaternion.identity;

            foreach (
                Collider modelCollider in m_worldVisual.GetComponentsInChildren<Collider>(true)
            )
            {
                Destroy(modelCollider);
            }
        }

        AddPickupCollider();
    }

    private void AddPickupCollider()
    {
        m_pickupCollider = gameObject.AddComponent<BoxCollider>();

        m_pickupCollider.isTrigger = true;

        if (!TryGetVisualBounds(gameObject, out Bounds bounds))
        {
            m_pickupCollider.size = k_minPickupSize;
            return;
        }

        Vector3 lossy = transform.lossyScale;
        Vector3 localSize = new Vector3(
            lossy.x != 0f ? bounds.size.x / lossy.x : bounds.size.x,
            lossy.y != 0f ? bounds.size.y / lossy.y : bounds.size.y,
            lossy.z != 0f ? bounds.size.z / lossy.z : bounds.size.z
        );

        m_pickupCollider.center = transform.InverseTransformPoint(bounds.center);
        m_pickupCollider.size = Vector3.Max(localSize, k_minPickupSize);
    }

    /// <summary>아이템의 실물 밑면을 groundY에 맞춰 파묻히지 않게 앉힌다.</summary>
    public static void SettleOnGround(GameObject item, float groundY)
    {
        if (!TryGetVisualBounds(item, out Bounds bounds))
            return;

        float sink = groundY - bounds.min.y;
        if (sink > 0f)
            item.transform.position += Vector3.up * sink;
    }

    private static bool TryGetVisualBounds(GameObject item, out Bounds bounds)
    {
        bounds = default;

        Renderer[] renderers = item.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
            return false;

        bounds = renderers[0].bounds;
        foreach (Renderer childRenderer in renderers)
        {
            bounds.Encapsulate(childRenderer.bounds);
        }

        return true;
    }

    private void OnTransformParentChanged()
    {
        RefreshWorldPresence();
    }

    private void RefreshWorldPresence()
    {
        bool inWorld = !IsHeld;

        foreach (Renderer childRenderer in GetComponentsInChildren<Renderer>(true))
        {
            childRenderer.enabled = inWorld;
        }

        foreach (Collider childCollider in GetComponentsInChildren<Collider>(true))
        {
            childCollider.enabled = inWorld;
        }

        if (m_highlight != null)
            m_highlight.SetGrounded(inWorld);
    }

    public LocalizedString PromptLabel(GameObject interactor) =>
        IsHeld ? null : InteractPrompts.Pickup;

    public void Interact(GameObject interactor)
    {
        if (IsHeld)
        {
            return;
        }

        PlayerLoadout loadout = interactor.GetComponentInParent<PlayerLoadout>();
        if (loadout == null)
        {
            return;
        }

        loadout.RequestPickup(m_networkObject);
    }
}
