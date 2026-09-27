using UnityEngine;

/// <summary>
/// 같은 오브젝트의 능력 컴포넌트(ChannelGauge·ToastFeedback·OwnerFeedback)를 lazy하게 찾아 캐싱하는 공통 확장.
/// </summary>
public static class CapabilityComponent
{
    /// <summary>캐시가 비어 있으면 같은 오브젝트에서 찾아 채우고, 없으면 에러를 남기고 null을 돌려준다.</summary>
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
