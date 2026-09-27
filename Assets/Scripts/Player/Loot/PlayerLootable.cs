using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 약탈당하는 쪽 — 털릴 수 있는 상태와 소지품·지갑을 내주고, 당한 본인에게 알린다.
/// 무엇을 가져갈지는 PlayerLooter가 정한다.
/// </summary>
public class PlayerLootable : NetworkBehaviour
{
    private PlayerIncapacitation m_incapacitation;
    private PlayerLoadout m_loadout;
    private PlayerWallet m_wallet;
    private PlayerTheftView m_theftView;

    public bool CanBeLooted => m_incapacitation != null && m_incapacitation.IsOutOfAction;

    internal PlayerLoadout Loadout => m_loadout;

    internal PlayerWallet Wallet => m_wallet;

    private void Awake()
    {
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_loadout = GetComponent<PlayerLoadout>();
        m_wallet = GetComponent<PlayerWallet>();
        m_theftView = GetComponent<PlayerTheftView>();
    }

    /// <summary>소지품을 뺏겼다 — 본인에게만 알린다. 서버 전용.</summary>
    internal void ServerNotifyRobbedItem() =>
        ServerNotifyVictim("[약탈] 소지품을 빼앗겼다", stolenToast: true);

    /// <summary>개인 자금을 뺏긴 사실을 본인에게만 알린다. 서버 전용.</summary>
    internal void ServerNotifyRobbedFunds(int amount) =>
        ServerNotifyVictim($"[약탈] 개인 자금을 빼앗겼다 — {amount}", stolenToast: false);

    private void ServerNotifyVictim(string message, bool stolenToast)
    {
        Debug.Log(message);

        ulong victim =
            m_incapacitation != null ? m_incapacitation.BodyOwnerClientId : OwnerClientId;

        if (!IsSpawned || !IsServer || victim == NetworkManager.ServerClientId)
        {
            if (stolenToast)
                m_theftView?.ShowStolenLocal();
            return;
        }

        VictimNotifyRpc(message, stolenToast, RpcTarget.Single(victim, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void VictimNotifyRpc(string message, bool stolenToast, RpcParams rpcParams)
    {
        Debug.Log(message);
        if (stolenToast)
            m_theftView?.ShowStolenLocal();
    }
}
