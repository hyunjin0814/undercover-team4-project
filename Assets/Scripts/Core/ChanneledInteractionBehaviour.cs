using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 서버 권위 상호작용의 오너 판정 피드백 공통 기반 (#91) — "판정은 서버가 하되 그 행동을 시작한
/// 오너의 화면에만 보여준다"는 규칙을 따라, 호스트 오너·오프라인은 로컬로 즉시 구동하고 원격
/// 오너에게는 SendTo.Owner RPC로 전달한다.
///
/// 채널링 게이지(#184)는 여기서 분리돼 <see cref="ChannelGauge"/> 컴포넌트로 옮겨졌다 —
/// 모든 아이템이 채널링을 쓰지는 않기 때문이다. 경위는 docs/channeled-interaction-split.md 참고.
/// </summary>
public abstract class ChanneledInteractionBehaviour : NetworkBehaviour
{
    // 서버 권위 경로를 직접 실행해도 되는 피어인지 — 서버(호스트)이거나 오프라인.
    // 스폰 전(오프라인)엣 IsServer 캐시가 아직 갱신되지 않아 false일 수 있으므로, 스폰 여부를 함께 본다.
    protected bool HasServerAuthority => !IsSpawned || IsServer;

    // ---- 오너 판정 피드백 (#91) ----

    /// <summary>
    /// 서버 판정 결과를 <b>오너 콘솔에</b> 알린다. 판정 로그는 서버에서 찍히므로 원격 클라 오너는
    /// 볼 수 없다 — 오너 콘솔에도 같은 줄을 전달한다.
    ///
    /// <b>콘솔 전용이다.</b> 플레이어에게 보일 것은 <see cref="ToastOwner"/>로 보낸다 — 완성된
    /// 문장을 RPC에 실으면 받는 쪽 언어와 무관하게 서버 언어로 뜨기 때문이다 (#525).
    /// 여기 실리는 문장은 번역 대상이 아니므로 그대로 문자열이다 (localization.md §1).
    /// </summary>
    protected void NotifyOwner(string message)
    {
        Debug.Log(message); // 서버(호스트)·오프라인 콘솔
        if (IsSpawned && IsServer && !IsOwner)
            OwnerLogRpc(message); // 원격 클라가 오너면 거기서도 로그
    }

    /// <summary>
    /// 오너 화면 토스트를 요청한다 (#309 → #525). 사유는 enum으로만 싣고 문구는 받는 쪽이
    /// 자기 로케일로 조회한다. 콘솔에는 값 이름을 그대로 남긴다 — 콘솔은 번역 대상이 아니다.
    /// </summary>
    protected void ToastOwner(EItemFeedback feedback)
    {
        Debug.Log($"[오너 토스트] {feedback}");
        if (IsSpawned && IsServer && !IsOwner)
        {
            OwnerToastRpc(feedback); // 원격 클라가 오너면 거기서 발행
            return;
        }
        RaiseOwnerToast(feedback); // 호스트 오너·오프라인은 로컬 발행
    }

    [Rpc(SendTo.Owner)]
    private void OwnerLogRpc(string message) => Debug.Log($"[서버 판정] {message}");

    [Rpc(SendTo.Owner)]
    private void OwnerToastRpc(EItemFeedback feedback)
    {
        Debug.Log($"[서버 판정] {feedback}");
        RaiseOwnerToast(feedback);
    }

    /// <summary>
    /// 오너 화면 토스트를 발행한다 — 토스트 채널을 가진 하위만 재정의한다(Scanner.OnScanFeedback).
    /// 기본은 무동작이라 토스트를 쓰지 않는 하위는 콘솔 로그만 나간다.
    /// </summary>
    protected virtual void RaiseOwnerToast(EItemFeedback feedback) { }
}
