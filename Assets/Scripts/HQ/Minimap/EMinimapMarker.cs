using System;

/// <summary>
/// 미니맵 표시 대상 분류 플래그 — 휴대용 미니맵이 일부만 골라 보여준다.
/// </summary>
[Flags]
public enum EMinimapMarker
{
    None = 0,
    Player = 1 << 0,
    Hq = 1 << 1,
    Event = 1 << 2,
    Criminal = 1 << 3,
    Door = 1 << 4,
    Cctv = 1 << 5,
}
