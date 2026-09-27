/// <summary>
/// 먹통 중 사용 불가 아이템이 공용으로 쓰는 차단 게이트 — DeviceBlackoutEvent 조회·캐시·재해석을 맡는다.
/// </summary>
public class DeviceBlackoutGate
{
    private DeviceBlackoutEvent m_blackout;

    public bool IsActive
    {
        get
        {
            if (m_blackout == null)
                m_blackout = App.Game.SuddenEvent?.GetEvent<DeviceBlackoutEvent>();
            return m_blackout != null && m_blackout.IsCommsBlackout;
        }
    }
}
