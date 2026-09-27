using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 서버 판정 사유(enum)를 행동한 오너 화면에 토스트로 띄우는 능력 컴포넌트.
/// 호스트 오너·오프라인은 로컬로, 원격 오너에게는 Owner RPC로 전달하며 문구 조회는 OnToast 구독 UI가 한다.
/// </summary>
public class ToastFeedback : NetworkBehaviour
{
    public event Action<EItemFeedback> OnToast;

    /// <summary>오너 화면 토스트를 요청하고 콘솔에 값 이름을 남긴다.</summary>
    public void ToastOwner(EItemFeedback feedback)
    {
        Debug.Log($"[오너 토스트] {feedback}");
        if (IsSpawned && IsServer && !IsOwner)
        {
            OwnerToastRpc(feedback);
            return;
        }
        Raise(feedback);
    }

    [Rpc(SendTo.Owner)]
    private void OwnerToastRpc(EItemFeedback feedback)
    {
        Debug.Log($"[서버 판정] {feedback}");
        Raise(feedback);
    }

    private void Raise(EItemFeedback feedback) => OnToast?.Invoke(feedback);
}
