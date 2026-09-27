using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 세션 끊김 사유를 보관했다가 타이틀 복귀·로딩 종료 후 토스트로 띄운다.
/// </summary>
public class ConnectionLostToastView : MonoBehaviour
{
    private const string k_titleTable = "TitleTable";
    private const string k_prefix = "Title.ConnectionLost.";

    [Tooltip("알림이 떠 있는 시간(초)")]
    [SerializeField]
    private float m_toastSeconds = 5f;

    private SessionManager Session => App.Net.Session;
    private EConnectionLostReason m_pending;

    private void Start()
    {
        if (Session != null)
            Session.OnConnectionLost += HandleConnectionLost;
        else
            Debug.LogWarning(
                "ConnectionLostToastView: SessionManager가 없어 드롭 토스트를 걸 수 없다",
                this
            );
    }

    private void OnDestroy()
    {
        if (Session != null)
            Session.OnConnectionLost -= HandleConnectionLost;
    }

    private void HandleConnectionLost(EConnectionLostReason reason) => m_pending = reason;

    private void Update()
    {
        if (m_pending == EConnectionLostReason.None)
            return;
        if (App.CurrentScene != EScene.Title)
            return;
        if (App.UI.Toast == null)
            return;
        if (App.UI.Loading != null && App.UI.Loading.IsBusy)
            return;

        var message = new LocalizedString(k_titleTable, k_prefix + m_pending);
        App.UI.Toast.Show(message, m_toastSeconds);
        m_pending = EConnectionLostReason.None;
    }
}
