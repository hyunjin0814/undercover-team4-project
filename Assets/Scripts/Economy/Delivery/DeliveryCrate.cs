using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// DeliveryPad에 놓이는 배달 상자 — 열면 서버가 구매 목록을 읽어 그 자리에 아이템을 스폰한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class DeliveryCrate : NetworkBehaviour, IInteractable
{
    private const float k_interactRange = 3f;

    [Tooltip("강하 연출 길이(초)")]
    [SerializeField]
    private float m_descentSeconds = 1.2f;

    private readonly NetworkVariable<double> m_landTimeSynced = new NetworkVariable<double>();
    private bool m_opened;

    public float DescentSeconds => m_descentSeconds;

    private bool HasLanded =>
        !IsSpawned || NetworkManager == null || NetworkManager.ServerTime.Time >= m_landTimeSynced.Value;

    public bool CanInteract(GameObject interactor) => !m_opened && HasLanded;

    public LocalizedString PromptLabel(GameObject interactor) =>
        CanInteract(interactor) ? InteractPrompts.OpenCrate : null;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor))
            return;

        RequestOpenRpc();
    }

    /// <summary>스폰 전에 착지 시각(ServerTime 기준)을 정한다. Spawn 이전에 호출해야 한다.</summary>
    public void ServerScheduleLanding(double landTime)
    {
        m_landTimeSynced.Value = landTime;
    }

    public override void OnNetworkSpawn()
    {
        DeliveryPad pad = DeliveryPad.All.Count > 0 ? DeliveryPad.All[0] : null;
        double remaining = m_landTimeSynced.Value - NetworkManager.ServerTime.Time;
        if (remaining <= 0d)
        {
            if (pad != null)
                transform.position = pad.ResolveCratePosition();
            return;
        }

        pad?.PlayDroneDelivery(transform, (float)remaining);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestOpenRpc(RpcParams rpcParams = default)
    {
        if (m_opened || !HasLanded || !IsRequesterInRange(rpcParams.Receive.SenderClientId))
            return;

        ServerOpen();
    }

    private bool IsRequesterInRange(ulong clientId)
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (
            manager == null
            || !manager.ConnectedClients.TryGetValue(clientId, out NetworkClient client)
            || client.PlayerObject == null
        )
            return false;

        PlayerInteractor interactor = client.PlayerObject.GetComponent<PlayerInteractor>();
        return PlayerInteractor.IsWithinReach(
            interactor,
            transform,
            PlayerInteractor.RangeOf(interactor, k_interactRange),
            transform.position
        );
    }

    private void ServerOpen()
    {
        m_opened = true;
        SpillItems();
        PlayOpenSoundRpc();
        NetworkObject.Despawn(destroy: true);
    }

    [Rpc(SendTo.Everyone)]
    private void PlayOpenSoundRpc() =>
        App.Sound?.PlaySfxAt(EAudioClip.CrateOpen, transform.position);

    private void SpillItems()
    {
        DeliveryPad pad = DeliveryPad.All.Count > 0 ? DeliveryPad.All[0] : null;
        if (pad == null)
        {
            Debug.LogWarning("[배달 상자] 배달 지점을 찾지 못해 아이템을 놓지 못했다", this);
            return;
        }

        ShopPurchases purchases = App.Game.ShopPurchases;
        if (purchases == null)
            return;

        int index = 0;
        foreach (ItemBase itemPrefab in purchases.Carried)
        {
            if (itemPrefab == null)
                continue;

            Vector3 position = pad.ResolveItemPosition(index++);
            ItemBase item = Instantiate(itemPrefab, position, Quaternion.identity);
            WorldItemPickup.SettleOnGround(item.gameObject, position.y);

            item.gameObject.AddComponent<ShopDeliveredItem>().SourcePrefab = itemPrefab;
            item.NetworkObject.Spawn(destroyWithScene: false);
        }

        if (index > 0)
            Debug.Log($"[배달 상자] 소지형 {index}개 개봉");
    }
}
