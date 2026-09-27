using UnityEngine;

/// <summary>
/// 능력 컴포넌트(<see cref="ChannelGauge"/>·<see cref="ToastFeedback"/>·<see cref="OwnerFeedback"/>)를
/// 같은 오브젝트에서 찾아 쥐는 공통 경로.
///
/// <b>왜 lazy인가</b> — <c>Awake</c>에서 캐싱하지 않는다. 소비자가 이미 자기 <c>Awake</c>를 갖고 있는
/// 경우가 있어 기반이 <c>Awake</c>를 새로 넣으면 조용히 가려지고, <see cref="ReviveKit"/>의 오프라인
/// 자가 부활처럼 스폰 전에 처음 평가되는 경로도 있다. lazy + <c>LogError</c> 폴백이면 둘 다 안전하다.
///
/// <b>왜 모았는가</b> — 같은 null 검사·에러 문구가 소비자마다 복제돼 18벌까지 늘어났다. 프리팹 누락을
/// 알리는 문구는 한 군데서 관리한다. 경위는 docs/channeled-interaction-split.md 참고.
/// </summary>
public static class CapabilityComponent
{
    /// <summary>
    /// 캐시가 비어 있으면 같은 오브젝트에서 찾아 채운다. 없으면 <b>원인을 먼저 알리고</b> null을 돌려준다 —
    /// <c>[RequireComponent]</c>의 자동 보정은 에디터 편의라 기존 프리팹 자산을 소급 수정하지 않기 때문이다
    /// (docs/channeled-interaction-split.md "함정" §2).
    ///
    /// ⚠ 없다고 해서 <b>런타임에 붙이면 안 된다</b> — NGO가 스폰 시점의 NetworkBehaviour 인덱스로 RPC를
    /// 라우팅하므로 피어 간 인덱스가 어긋난다. 반드시 프리팹에 박혀 있어야 한다.
    /// </summary>
    public static TComponent ResolveCapability<TComponent>(this Component self, ref TComponent cache)
        where TComponent : Component
    {
        if (cache == null)
        {
            cache = self.GetComponent<TComponent>();
            if (cache == null)
                Debug.LogError(
                    $"{self.GetType().Name}: {typeof(TComponent).Name}이(가) 프리팹에 없다 — "
                        + "프리팹을 열어 추가하고 저장할 것",
                    self
                );
        }

        return cache;
    }
}
