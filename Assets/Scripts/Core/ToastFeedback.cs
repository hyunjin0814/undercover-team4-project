using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 서버 판정 사유를 <b>오너 화면 토스트로</b> 띄우는 능력 컴포넌트 (#309 → #525).
/// 판정은 서버가 하되 보여줄 곳은 그 행동을 시작한 오너뿐이므로, 호스트 오너·오프라인은 로컬로
/// 즉시 발행하고 원격 오너에게는 SendTo.Owner RPC로 전달한다.
///
/// <b>사유는 enum으로만 싣는다</b> — 완성된 문장을 RPC에 실으면 받는 쪽 언어와 무관하게 서버 언어로
/// 뜬다 (#525). 문구 조회는 <see cref="OnToast"/>를 구독하는 UI의 몫이다.
///
/// 토스트가 필요한 프리팹에만 부착한다 — 소비자는 <c>[RequireComponent(typeof(ToastFeedback))]</c>로
/// 선언한다. 상속에서 합성으로 옮긴 경위는 docs/channeled-interaction-split.md 참고.
///
/// <b>발행 지점이 <c>protected virtual</c> 재정의에서 이벤트로 뒤집혔다</b> — 상속이 사라지면
/// override할 자리가 없기 때문이다. 대신 같은 오브젝트의 여럿이 함께 구독할 수 있게 됐다:
/// <see cref="Scanner"/>와 <see cref="ItemBattery"/>가 <c>Scanner.prefab</c> 루트에서 이 컴포넌트
/// 하나를 공유하므로, 배터리 토스트를 본체 채널로 <b>손으로 중계할 필요가 없어졌다</b>.
/// </summary>
public class ToastFeedback : NetworkBehaviour
{
    /// <summary>오너 로컬에서 발행되는 토스트 사유 — 표시할 UI가 구독한다.</summary>
    public event Action<EItemFeedback> OnToast;

    /// <summary>
    /// 오너 화면 토스트를 요청한다. 콘솔에는 값 이름을 그대로 남긴다 — 콘솔은 번역 대상이 아니다
    /// (localization.md §1).
    /// </summary>
    public void ToastOwner(EItemFeedback feedback)
    {
        Debug.Log($"[오너 토스트] {feedback}");
        if (IsSpawned && IsServer && !IsOwner)
        {
            OwnerToastRpc(feedback); // 원격 클라가 오너면 거기서 발행
            return;
        }
        Raise(feedback); // 호스트 오너·오프라인은 로컬 발행
    }

    [Rpc(SendTo.Owner)]
    private void OwnerToastRpc(EItemFeedback feedback)
    {
        Debug.Log($"[서버 판정] {feedback}");
        Raise(feedback);
    }

    private void Raise(EItemFeedback feedback) => OnToast?.Invoke(feedback);
}
