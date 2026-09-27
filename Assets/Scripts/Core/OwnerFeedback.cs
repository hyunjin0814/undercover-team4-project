using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 서버 판정 결과를 <b>오너 콘솔에</b> 남기는 능력 컴포넌트 (#91). 판정 로그는 서버에서 찍히므로
/// 원격 클라 오너는 볼 수 없다 — 오너 콘솔에도 같은 줄을 전달한다.
///
/// <b>콘솔 전용이다.</b> 플레이어에게 보일 것은 <see cref="ToastFeedback.ToastOwner"/>로 보낸다 —
/// 완성된 문장을 RPC에 실으면 받는 쪽 언어와 무관하게 서버 언어로 뜨기 때문이다 (#525).
/// 여기 실리는 문장은 번역 대상이 아니므로 그대로 문자열이다 (localization.md §1).
///
/// 오너 로그가 필요한 프리팹에만 부착한다 — 소비자는 <c>[RequireComponent(typeof(OwnerFeedback))]</c>로
/// 선언한다. 상속에서 합성으로 옮긴 경위는 docs/channeled-interaction-split.md 참고.
/// </summary>
public class OwnerFeedback : NetworkBehaviour
{
    /// <summary>서버 판정 결과를 오너 콘솔에 알린다.</summary>
    public void NotifyOwner(string message)
    {
        Debug.Log(message); // 서버(호스트)·오프라인 콘솔
        if (IsSpawned && IsServer && !IsOwner)
            OwnerLogRpc(message); // 원격 클라가 오너면 거기서도 로그
    }

    [Rpc(SendTo.Owner)]
    private void OwnerLogRpc(string message) => Debug.Log($"[서버 판정] {message}");
}
