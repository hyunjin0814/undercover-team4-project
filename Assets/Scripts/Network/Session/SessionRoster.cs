using System;
using Cysharp.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 세션 접속자 명부(닉네임·PlayerId·음소거) — 세션 시작 시 스폰되는 상주 NetworkObject.
/// 각 클라가 스폰 시 자기 값을 보고하고, 늦은 참가자에게는 OnListReady로 전체 갱신을 알린다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SessionRoster : NetworkedManagerBase
{
    private readonly NetworkList<LobbyPlayerEntry> m_players = new NetworkList<LobbyPlayerEntry>();

    public NetworkList<LobbyPlayerEntry> Players => m_players;

    /// <summary>이 엔트리가 방장인가 — 별도 필드를 두지 않고 서버 clientId와 비교한다.</summary>
    public bool IsHostEntry(LobbyPlayerEntry entry) => NetworkManager != null && entry.ClientId == NetworkManager.ServerClientId;

    /// <summary>이 클라이언트의 명부 항목을 찾는다. 아직 보고가 없으면 false.</summary>
    public bool TryGetEntry(ulong clientId, out LobbyPlayerEntry entry)
    {
        for (int i = 0; i < m_players.Count; i++)
        {
            if (m_players[i].ClientId != clientId)
                continue;

            entry = m_players[i];
            return true;
        }

        entry = default;
        return false;
    }

    public event Action OnListReady;

    public event Action<string> OnPlayerJoined;

    public event Action<string> OnPlayerLeft;

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            m_players.Clear();
            NetworkManager.OnClientDisconnectCallback += HandleClientDisconnected;
        }

        OnListReady?.Invoke();

        GameSettings.OnMicMutedChanged += HandleMicMutedChanged;

        CosmeticLoadout.OnPlayerColorChanged += HandlePlayerColorChanged;

        CosmeticLoadout.OnAccessoryChanged += HandleAccessoryChanged;

        ReportSelfRpc(BuildSelf());
    }

    public override void OnNetworkDespawn()
    {
        GameSettings.OnMicMutedChanged -= HandleMicMutedChanged;
        CosmeticLoadout.OnPlayerColorChanged -= HandlePlayerColorChanged;
        CosmeticLoadout.OnAccessoryChanged -= HandleAccessoryChanged;

        if (IsServer && NetworkManager != null)
            NetworkManager.OnClientDisconnectCallback -= HandleClientDisconnected;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void ReportSelfRpc(LobbyPlayerEntry entry, RpcParams rpcParams = default)
    {
        entry.ClientId = rpcParams.Receive.SenderClientId;

        for (int i = 0; i < m_players.Count; i++)
        {
            if (m_players[i].ClientId == entry.ClientId)
            {
                bool hadNickname = !m_players[i].Nickname.IsEmpty;
                m_players[i] = entry;

                if (!hadNickname && !entry.Nickname.IsEmpty)
                    AnnounceJoinedRpc(entry.Nickname, entry.ClientId);
                return;
            }
        }

        m_players.Add(entry);

        if (!entry.Nickname.IsEmpty)
            AnnounceJoinedRpc(entry.Nickname, entry.ClientId);
    }

    private void HandleClientDisconnected(ulong clientId)
    {
        for (int i = m_players.Count - 1; i >= 0; i--)
        {
            if (m_players[i].ClientId != clientId)
                continue;

            FixedString64Bytes nickname = m_players[i].Nickname;

            string playerId = m_players[i].PlayerId.ToString();

            m_players.RemoveAt(i);
            AnnounceLeftRpc(nickname, clientId);
            App.Net.Session?.RemovePlayerAsync(playerId).Forget();
        }
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void AnnounceJoinedRpc(FixedString64Bytes nickname, ulong clientId) =>
        Announce(OnPlayerJoined, nickname, clientId);

    [Rpc(SendTo.ClientsAndHost)]
    private void AnnounceLeftRpc(FixedString64Bytes nickname, ulong clientId) =>
        Announce(OnPlayerLeft, nickname, clientId);

    private void Announce(Action<string> handler, FixedString64Bytes nickname, ulong clientId)
    {
        if (NetworkManager != null && clientId == NetworkManager.LocalClientId)
            return;

        handler?.Invoke(nickname.ToString());
    }

    private void HandleMicMutedChanged(bool _) => ReportSelf();

    private void HandlePlayerColorChanged(EBodyPart _) => ReportSelf();

    private void HandleAccessoryChanged(EAccessorySlot _) => ReportSelf();

    private void ReportSelf()
    {
        if (IsSpawned)
            ReportSelfRpc(BuildSelf());
    }

    private static LobbyPlayerEntry BuildSelf()
    {
        AuthBootstrap auth = App.Net.Auth;

        var nickname = (auth != null ? auth.Nickname : null).ToFixed64();
        var playerId = (auth != null ? auth.PlayerId : null).ToFixed64();

        return new LobbyPlayerEntry
        {
            Nickname = nickname,
            PlayerId = playerId,
            MicMuted = GameSettings.MicMuted,
            Colors = PlayerColorSet.FromSettings(),
            Accessories = AccessorySet.FromSettings(),
        };
    }
}
