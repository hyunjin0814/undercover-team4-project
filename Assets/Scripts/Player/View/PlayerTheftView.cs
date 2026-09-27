using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 소지품을 털렸을 때 본인에게만 토스트 알림을 띄운다.
/// </summary>
public class PlayerTheftView : NetworkBehaviour
{
    [Tooltip("털렸을 때 띄울 문구 — HudTable/Hud.Pickpocket.Stolen")]
    [SerializeField]
    private LocalizedString m_stolenToast;

    [Tooltip("문구가 떠 있는 시간(초). 지나면 저절로 사라진다")]
    [Min(0.5f)]
    [SerializeField]
    private float m_toastSeconds = 3f;

    /// <summary>서버 전용 — 물건을 실제로 뺏겼을 때만 부른다(빈손이면 알릴 것이 없다).</summary>
    public void ShowStolen()
    {
        if (IsSpawned)
            ShowStolenRpc();
        else
            ShowLocal();
    }

    [Rpc(SendTo.Owner)]
    private void ShowStolenRpc() => ShowLocal();

    /// <summary>이 피어에서 바로 도난 알림을 띄운다(쓰러진 몸처럼 대상을 직접 지정한 경우).</summary>
    internal void ShowStolenLocal() => ShowLocal();

    private void ShowLocal() => App.UI.Toast?.Show(m_stolenToast, m_toastSeconds);
}
