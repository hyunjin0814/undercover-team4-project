using System.Collections.Generic;

/// <summary>
/// 여러 <see cref="ISuddenEvent"/>를 품고 프레임워크에 넘겨주는 컴포넌트. (#106)
/// "컴포넌트 1개 = 이벤트 1종"이 성립하지 않는 경우를 위한 통로다 — <see cref="SpawnedNpcEventSet"/>처럼
/// 인스펙터 리스트 항목마다 이벤트가 하나씩 나오는 구성이 여기 해당한다.
///
/// <see cref="SuddenEventManager"/>는 이 인터페이스 뒤도 들여다보지 않는다 —
/// 수집 통로가 늘었을 뿐, 매니저가 어떤 이벤트가 있는지 모른다는 원칙은 그대로다.
/// </summary>
public interface ISuddenEventProvider
{
    /// <summary>
    /// 자기가 가진 이벤트를 <paramref name="into"/>에 <b>추가</b>한다.
    /// <b>비우지 말 것</b> — 매니저가 컴포넌트형 이벤트로 풀을 먼저 채운 뒤 제공자들이 같은 풀에 얹는 순서라,
    /// 여기서 Clear()하면 앞서 수집된 이벤트(괴한·먹통 등)가 조용히 사라진다.
    /// </summary>
    void CollectEvents(List<ISuddenEvent> into);
}
