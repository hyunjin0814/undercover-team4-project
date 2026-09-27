/// <summary>
/// 돌발 이벤트 1종의 인터페이스 — SuddenEventManager가 서버 권위로 발생·틱·정리한다.
/// 상태 전파(NetworkObject 또는 NetworkVariable)는 구현체가 스스로 책임진다.
/// </summary>
public interface ISuddenEvent
{
    string DisplayName { get; }

    bool IsActive { get; }

    bool AnnounceOnBegin => true;

    string NoticeKey => null;

    /// <summary>지금 발생 가능한지 — 선행 조건(예: 현장 플레이어 존재) 검사. 서버(또는 오프라인)에서만 호출된다.</summary>
    bool CanTrigger();

    /// <summary>강제 발동 시 누적 조건을 스스로 채울 수 있으면 채우고 true를 돌려준다. 기본은 거절.</summary>
    bool ServerPrepareForceTrigger() => false;

    /// <summary>발생 — 서버(또는 오프라인)에서 호출. 효과를 시작한다.</summary>
    void ServerBegin();

    /// <summary>진행 틱 — 활성 중 매 프레임 서버(또는 오프라인)에서 호출. 지속·자동 해제를 관리한다.</summary>
    void ServerTick();

    /// <summary>강제 정리 — 라운드 종료 등으로 즉시 끝내야 할 때 호출. 스폰물 디스폰·효과 해제를 되돌린다.</summary>
    void ServerReset();
}
