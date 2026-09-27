using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NGO 연결 승인 콜백의 단일 소유자 — 씬별 스폰 정책은 SpawnPolicy로 위임받는다.
/// </summary>
public class ConnectionApprovalGate
{
    public System.Action<NetworkManager.ConnectionApprovalResponse> SpawnPolicy { get; set; }

    private NetworkManager m_networkManager;

    /// <summary>SDK가 StartClient/StartHost를 부르기 전에 대입해야 실제 요청에 실린다.</summary>
    public static void StampLocalPayload(NetworkManager networkManager)
    {
        if (networkManager != null)
            networkManager.NetworkConfig.ConnectionData = NetworkProtocol.EncodePayload();
    }

    public void Install(NetworkManager networkManager)
    {
        m_networkManager = networkManager;
        if (m_networkManager != null)
            m_networkManager.ConnectionApprovalCallback = Approve;
    }

    private void Approve(
        NetworkManager.ConnectionApprovalRequest request,
        NetworkManager.ConnectionApprovalResponse response
    )
    {
        bool isSelf =
            m_networkManager != null && request.ClientNetworkId == m_networkManager.LocalClientId;

        if (!isSelf)
        {
            PeerStamp client = NetworkProtocol.DecodePayload(request.Payload);

            if (client.Version != NetworkProtocol.VersionString)
            {
                Debug.LogWarning(
                    $"[ConnectionApprovalGate] 버전 불일치로 연결 거부 / 내 버전(호스트): {NetworkProtocol.VersionString}, 클라 버전: {client.Version}, 내 sha: {BuildStamp.Sha}, 클라 sha: {client.Sha}"
                );
                response.Approved = false;
                response.Reason = NetworkProtocol.BuildMismatchReason(
                    NetworkProtocol.VersionString
                );
                return;
            }

            if (client.Sha != BuildStamp.Sha)
            {
                Debug.LogWarning(
                    $"[ConnectionApprovalGate] 버전은 같은데 커밋이 다름(참가 허용) / 버전: {NetworkProtocol.VersionString}, 내 sha: {BuildStamp.Sha}, 클라 sha: {client.Sha}"
                );
            }
            else
            {
                Debug.Log(
                    $"[ConnectionApprovalGate] 클라 접속 승인 / 버전: {client.Version}, sha: {client.Sha}"
                );
            }
        }

        response.Approved = true;
        response.CreatePlayerObject = false;
        SpawnPolicy?.Invoke(response);
    }
}
