/// <summary>
/// 라운드 날씨 1종 표식 — 준비 단계에 라운드당 한 번 뽑히고 ServerReset으로만 해제된다.
/// 주기 추첨 후보에서는 빠진다.
/// </summary>
public interface IRoundWeather : ISuddenEvent
{
    WeatherKind Kind { get; }
}
