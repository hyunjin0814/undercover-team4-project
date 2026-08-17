using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 월드에 놓인 아이템을 줍는 상호작용 진입점. (#88)
/// 아이템 프리팹(독립 NetworkObject)에 부착된다. 바닥에 놓인 모습은 아이템의 실물 모델
/// (ItemBase.HeldModelPrefab, 1인칭 손 표시와 동일 에셋)을 런타임에 인스턴스화해 보여준다 —
/// 별도 월드 모델을 두지 않아 한 소스(HeldModelPrefab)로 통일된다.
/// 들고 있을 때는(부모에 부착) 이 비주얼·콜라이더를 꺼서 손 표시(#45)와 이중 렌더링되지 않게,
/// 또 플레이어 자신의 상호작용 레이캐스트에 걸리지 않게 한다.
/// 월드 비주얼은 순수 로컬 표현이라 네트워크로 스폰하지 않고 각 클라가 자기 몫을 만든다.
/// PlayerInteractor의 IInteractable 경로로 호출되며(오너 클라 로컬), 줍기 요청을
/// 상호작용한 플레이어의 PlayerLoadout으로 넘긴다 — 실제 소유권 이전·부착은 서버가 처리한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class WorldItemPickup : MonoBehaviour, IInteractable
{
    // 줍기용 박스 최소 크기(로컬). 아이템 실물이 작아도(예: 13cm 스캐너) 크로스헤어로 겨냥할 수 있게
    // 이보다 작아지지 않도록 보장한다 — 바닥에 놓인 작은 아이템을 조준하지 못해 못 줍는 문제 방지.
    private static readonly Vector3 k_minPickupSize = new Vector3(0.5f, 0.5f, 0.5f);

    private NetworkObject m_networkObject;
    private GameObject m_worldVisual;
    private BoxCollider m_pickupCollider;
    private DroppedItemHighlight m_highlight;

    /// <summary>이미 누군가 들고 있으면(부모가 있으면) 주울 수 없다 — 중복 줍기 방지.</summary>
    public bool IsHeld => transform.parent != null;

    private void Awake()
    {
        m_networkObject = GetComponent<NetworkObject>();
        BuildWorldVisual();

        // 바닥 상시 하이라이트 (#330) — 프리팹 배선 없이 런타임 부착, 놓임/들림에 맞춰 켜고 끈다
        m_highlight = gameObject.AddComponent<DroppedItemHighlight>();
        m_highlight.Initialize();

        RefreshWorldPresence();
    }

    // 아이템의 실물 모델을 월드 표시용으로 인스턴스화하고, 줍기용 콜라이더를 붙인다.
    private void BuildWorldVisual()
    {
        ItemBase item = GetComponent<ItemBase>();
        if (item != null && item.HeldModelPrefab != null)
        {
            m_worldVisual = Instantiate(item.HeldModelPrefab, transform);
            m_worldVisual.transform.localPosition = Vector3.zero;
            m_worldVisual.transform.localRotation = Quaternion.identity;

            // 모델에 딸린 콜라이더는 표시 전용이라 제거한다 — 줍기 조준은 아래 전용 박스로 통일한다.
            // (Synty 소품의 MeshCollider는 실물만큼 작아 바닥에서 겨냥이 사실상 불가능하다)
            foreach (
                Collider modelCollider in m_worldVisual.GetComponentsInChildren<Collider>(true)
            )
            {
                Destroy(modelCollider);
            }
        }

        AddPickupCollider();
    }

    // 겨냥하기 충분한 크기의 줍기 전용 박스를 루트에 붙인다. 실물 경계에 맞추되 최소 크기를 보장한다.
    private void AddPickupCollider()
    {
        m_pickupCollider = gameObject.AddComponent<BoxCollider>();

        // 조준 판정 전용이므로 트리거로 둔다 — 솔리드면 최소 크기(0.5m) 박스가 그대로 발판이 되어
        // 버린 아이템을 밟고 떠오른다(#263). CharacterController는 트리거와 물리 충돌하지 않는다.
        // 줍기 조준은 그대로 동작한다: PlayerInteractor의 Raycast가 queryTriggerInteraction을 지정하지 않아
        // 프로젝트 설정(Physics.queriesHitTriggers = true)을 따르므로 트리거도 잡힌다.
        m_pickupCollider.isTrigger = true;

        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            m_pickupCollider.size = k_minPickupSize;
            return;
        }

        Bounds bounds = renderers[0].bounds;
        foreach (Renderer childRenderer in renderers)
        {
            bounds.Encapsulate(childRenderer.bounds);
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

    // NetworkObject.TrySetParent 부착/분리가 전 클라에 복제될 때마다 호출된다 — 표시 상태를 맞춘다.
    private void OnTransformParentChanged()
    {
        RefreshWorldPresence();
    }

    // 바닥에 놓였을 때만 하위 모델·콜라이더를 보이고, 들고 있을 때는 숨긴다.
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

        // 바닥에 있을 때만 상시 하이라이트 (#330)
        if (m_highlight != null)
            m_highlight.SetGrounded(inWorld);
    }

    // 조준 안내 (#664). 들려 있는 동안은 안내하지 않는다 — 아래 Interact가 그대로 되돌아가는 상태다.
    public LocalizedString PromptLabel(GameObject interactor) =>
        IsHeld ? null : InteractPrompts.Pickup;

    public void Interact(GameObject interactor)
    {
        if (IsHeld)
        {
            return;
        }

        // 상호작용한 플레이어의 로드아웃으로 줍기 요청을 넘긴다 — 서버 검증·소유권 이전은 그쪽에서.
        PlayerLoadout loadout = interactor.GetComponentInParent<PlayerLoadout>();
        if (loadout == null)
        {
            return;
        }

        loadout.RequestPickup(m_networkObject);
    }
}
