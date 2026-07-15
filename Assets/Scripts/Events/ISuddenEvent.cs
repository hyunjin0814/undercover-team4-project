/// <summary>
/// 돌발 이벤트 1종 — 프레임워크(<see cref="SuddenEventManager"/>)가 서버 권위로 발생·틱시킨다. (GDD 6-4, #106)
/// 구현체는 서버(또는 오프라인)에서만 상태를 바꾸며, 클라이언트는 표현만 한다. (#56 패턴)
///
/// 전 클라 전파는 <b>구현체가 스스로 책임진다</b> — 스폰형은 자기 NetworkObject로, 전역형은 자기 NetworkVariable로
/// 전파한다(전역형 예: <see cref="DeviceBlackoutEvent"/>). 매니저는 이 인터페이스 뒤를 들여다보지 않으므로
/// 이벤트별 상태를 매니저에 얹지 말 것 — 그러면 프레임워크가 특정 이벤트를 알게 된다.
/// </summary>
public interface ISuddenEvent
{
    /// <summary>사람이 읽는 이벤트 이름 — 로그·HUD 알림용.</summary>
    string DisplayName { get; }

    /// <summary>현재 진행 중인지 — 진행 중이면 프레임워크가 재발생시키지 않고 <see cref="ServerTick"/>만 돌린다.</summary>
    bool IsActive { get; }

    /// <summary>지금 발생 가능한지 — 선행 조건(예: 현장 플레이어 존재) 검사. 서버(또는 오프라인)에서만 호출된다.</summary>
    bool CanTrigger();

    /// <summary>발생 — 서버(또는 오프라인)에서 호출. 효과를 시작한다.</summary>
    void ServerBegin();

    /// <summary>진행 틱 — 활성 중 매 프레임 서버(또는 오프라인)에서 호출. 지속·자동 해제를 관리한다.</summary>
    void ServerTick();

    /// <summary>강제 정리 — 라운드 종료 등으로 즉시 끝내야 할 때 호출. 스폰물 디스폰·효과 해제를 되돌린다.</summary>
    void ServerReset();
}
