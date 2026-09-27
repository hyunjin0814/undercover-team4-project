using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어별 개인 자금 — 수배범을 직접 유치장에 넣은 사람에게 정산 시 현상금 일부가 들어간다.
/// 잔액은 본인(Owner)만 읽을 수 있다.
/// </summary>
public class PlayerWallet : NetworkBehaviour
{
    private readonly NetworkVariable<int> m_balance = new NetworkVariable<int>(
        0, 
        NetworkVariableReadPermission.Owner,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<int> m_roundEarned = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Owner,
        NetworkVariableWritePermission.Server);

    public int Balance => m_balance.Value;

    public int RoundEarned => m_roundEarned.Value;

    private string m_ownerPlayerId;

    public string OwnerPlayerId => m_ownerPlayerId;

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
            ReportPlayerIdRpc((App.Net.Auth != null ? App.Net.Auth.PlayerId : null).ToFixed64());
    }

    [Rpc(SendTo.Server)]
    private void ReportPlayerIdRpc(FixedString64Bytes playerId)
    {
        m_ownerPlayerId = playerId.ToString();

        if (SaveService.TryTakeWalletBalance(m_ownerPlayerId, out int saved))
        {
            m_balance.Value = saved;
            Debug.Log($"[개인 자금] 세이브 복원 — {OwnerClientId}번 잔액 {saved}");
        }
    }

    public void ServerAdd(int amount)
    {
        if (!IsServer)
        {
            Debug.LogWarning("PlayerWallet.ServerAdd는 서버에서만", this);
            return;
        }
        if (amount <= 0) return;

        m_balance.Value = Mathf.Max(0, m_balance.Value + amount);
        m_roundEarned.Value += amount;
        Debug.Log($"[개인 자금] {OwnerClientId}번 + {amount} -> 잔액 {m_balance.Value}");
    }

    /// <summary>잔액 전액을 다른 지갑으로 옮긴다(아군 약탈). 서버 전용.</summary>
    public int ServerTransferAllTo(PlayerWallet to)
    {
        if (!IsServer)
        {
            Debug.LogWarning("PlayerWallet.ServerTransferAllTo는 서버에서만", this);
            return 0;
        }
        if (to == null || to == this) return 0;

        int amount = m_balance.Value;
        if (amount <= 0) return 0;

        m_balance.Value = 0;
        to.ServerAdd(amount);
        Debug.Log($"[개인 자금] 이전 — {OwnerClientId}번 → {to.OwnerClientId}번, {amount}");
        return amount;
    }

    public void ServerResetRound()
    {
        if (!IsServer) return;
        m_roundEarned.Value = 0;
    }

    /// <summary>clientId로 플레이어 지갑을 찾는다. 없으면 null. 서버 전용.</summary>
    public static PlayerWallet FindByClientId(ulong clientId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client.PlayerObject == null) return null;

        return client.PlayerObject.GetComponent<PlayerWallet>();
    }
}
