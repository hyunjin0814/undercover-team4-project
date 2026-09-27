using System.Collections.Generic;

/// <summary>
/// 여러 ISuddenEvent를 한 컴포넌트에서 제공하기 위한 확장 지점. 현재 구현체는 없다.
/// </summary>
public interface ISuddenEventProvider
{
    /// <summary>가진 이벤트를 into에 추가한다. 리스트를 비우지 말 것.</summary>
    void CollectEvents(List<ISuddenEvent> into);
}
