using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 서버가 로딩 화면을 덮기 직전에 클라이언트에 씬 전환을 예고해 함께 덮게 하는 상주 컴포넌트.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SceneTransitionAnnouncer : NetworkedManagerBase
{
    /// <summary>클라이언트 전원에게 로딩 화면을 미리 덮으라고 알린다 — 서버 전용.</summary>
    public void AnnounceCover()
    {
        if (!IsSpawned || !IsServer)
            return;

        CoverRpc();
    }

    [Rpc(SendTo.NotServer)]
    private void CoverRpc() => App.UI.Loading?.CoverForIncomingSceneChange();
}
