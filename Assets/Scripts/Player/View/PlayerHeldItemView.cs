using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 3인칭 장착 아이템 표시 — 장착 아이템을 NetworkVariable로 알리고 각 클라가 손 본에 모델을 로컬로 붙인다.
/// </summary>
[RequireComponent(typeof(PlayerItemUser))]
public class PlayerHeldItemView : NetworkBehaviour
{
    [Header("손 앵커 (캐릭터 리그의 손 본)")]
    [Tooltip("아이템을 들릴 손 본. Synty 리그의 Hand_R 하위에 배치할 것")]
    [SerializeField]
    private Transform m_handAnchor;

    public Transform HandAnchor => m_handAnchor;

    /// <summary>시체를 묶을 밧줄의 물리 앵커(운반자 루트)를 돌려준다.</summary>
    public static Transform ResolveRopeAnchor(Transform carrier) => carrier;

    private readonly NetworkVariable<NetworkObjectReference> m_equipped =
        new NetworkVariable<NetworkObjectReference>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner
        );

    private PlayerItemUser m_itemUser;
    private GameObject m_heldModelInstance;

    private int m_refreshVersion;

    private PlayerRagdoll m_ragdoll;
    private bool m_hiddenByRagdoll;

    public override void OnNetworkSpawn()
    {
        m_itemUser = GetComponent<PlayerItemUser>();
        m_ragdoll = GetComponentInParent<PlayerRagdoll>();

        if (m_handAnchor == null)
        {
            Debug.LogWarning(
                $"[PlayerHeldItemView] 손 앵커가 지정되지 않아 3인칭 장착 표시를 끈다. "
                    + $"{name} 프리팹의 손 본(Hand_R) 하위 앵커를 인스펙터에 지정할 것."
            );
            enabled = false;
            return;
        }

        if (IsOwner)
        {
            m_itemUser.OnEquippedItemChanged += PublishEquipped;
            PublishEquipped(m_itemUser.EquippedItem);
        }

        m_equipped.OnValueChanged += HandleEquippedChanged;
        HandleEquippedChanged(default, m_equipped.Value);
    }

    public override void OnNetworkDespawn()
    {
        if (m_itemUser != null)
        {
            m_itemUser.OnEquippedItemChanged -= PublishEquipped;
        }

        m_equipped.OnValueChanged -= HandleEquippedChanged;
        ClearHeldModel();
    }

    private void PublishEquipped(ItemBase item)
    {
        NetworkObject itemNetworkObject =
            item != null ? item.GetComponent<NetworkObject>() : null;

        m_equipped.Value =
            itemNetworkObject != null && itemNetworkObject.IsSpawned
                ? new NetworkObjectReference(itemNetworkObject)
                : default;
    }

    private void HandleEquippedChanged(
        NetworkObjectReference previous,
        NetworkObjectReference current
    )
    {
        RefreshHeldModelAsync(current, ++m_refreshVersion).Forget();
    }

    private async UniTaskVoid RefreshHeldModelAsync(NetworkObjectReference itemRef, int version)
    {
        ClearHeldModel();

        if (itemRef.NetworkObjectId == 0)
        {
            return;
        }

        EResolveResult result = await NetworkRefResolver.WaitAsync(
            itemRef,
            () => this != null && IsSpawned && version == m_refreshVersion
        );

        if (result != EResolveResult.Resolved)
        {
            return;
        }

        if (
            !itemRef.TryGet(out NetworkObject itemNetworkObject)
            || !itemNetworkObject.TryGetComponent(out ItemBase item)
            || item.HeldModelPrefab == null
        )
        {
            return;
        }

        ShowHeldModel(item);
    }

    private void Update()
    {
        bool hide = m_ragdoll != null && m_ragdoll.IsRagdollActive;
        if (hide == m_hiddenByRagdoll)
            return;

        m_hiddenByRagdoll = hide;
        ApplyRagdollVisibility();
    }

    private void ApplyRagdollVisibility()
    {
        if (m_heldModelInstance != null)
            m_heldModelInstance.SetActive(!m_hiddenByRagdoll);
    }

    private void ShowHeldModel(ItemBase item)
    {
        m_heldModelInstance = Instantiate(item.HeldModelPrefab, m_handAnchor, false);

        m_heldModelInstance.transform.localPosition = item.ThirdPersonPositionOffset;
        m_heldModelInstance.transform.localRotation = Quaternion.Euler(
            item.ThirdPersonRotationOffset
        );

        foreach (Collider heldCollider in m_heldModelInstance.GetComponentsInChildren<Collider>(true))
        {
            heldCollider.enabled = false;
        }

        if (IsOwner)
        {
            PlayerLook.SetLayerRecursively(
                m_heldModelInstance.transform,
                LayerMask.NameToLayer("OwnBody")
            );
        }

        ApplyRagdollVisibility();
    }

    private void ClearHeldModel()
    {
        if (m_heldModelInstance != null)
        {
            Destroy(m_heldModelInstance);
            m_heldModelInstance = null;
        }
    }
}
