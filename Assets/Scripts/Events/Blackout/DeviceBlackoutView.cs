using UnityEngine;

/// <summary>
/// 먹통 플래그를 구독해 각 피어의 무전 음성 왜곡을 켜고 끈다.
/// </summary>
public class DeviceBlackoutView : MonoBehaviour
{
    [Header("참조 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private DeviceBlackoutEvent m_blackout;

    private VivoxManager Vivox => App.Net.Vivox;

    private void Start()
    {
        if (m_blackout == null)
            m_blackout = App.Game.SuddenEvent?.GetEvent<DeviceBlackoutEvent>();

        if (m_blackout == null)
        {
            Debug.LogWarning("DeviceBlackoutView: DeviceBlackoutEvent를 찾지 못해 먹통 표현이 동작하지 않는다", this);
            return;
        }

        m_blackout.OnCommsBlackoutChanged += HandleBlackoutChanged;
        HandleBlackoutChanged(m_blackout.IsCommsBlackout);
    }

    private void OnDestroy()
    {
        if (m_blackout != null)
            m_blackout.OnCommsBlackoutChanged -= HandleBlackoutChanged;
    }

    private void HandleBlackoutChanged(bool active)
    {
        if (Vivox != null)
            Vivox.SetVoiceDistorted(active);
    }
}
