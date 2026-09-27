using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 서버 판정 로그를 행동한 오너의 콘솔에도 남기는 능력 컴포넌트(콘솔 전용).
/// 플레이어에게 보일 문구는 ToastFeedback으로 보낸다.
/// </summary>
public class OwnerFeedback : NetworkBehaviour
{
    /// <summary>서버 판정 결과를 오너 콘솔에 알린다.</summary>
    public void NotifyOwner(string message)
    {
        Debug.Log(message);
        if (IsSpawned && IsServer && !IsOwner)
            OwnerLogRpc(message);
    }

    [Rpc(SendTo.Owner)]
    private void OwnerLogRpc(string message) => Debug.Log($"[서버 판정] {message}");
}
