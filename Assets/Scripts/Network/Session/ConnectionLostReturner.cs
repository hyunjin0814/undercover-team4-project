using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 호스트 이탈·세션 삭제로 세션이 끊기면 어느 씬에서든 타이틀로 되돌린다(상주 오브젝트).
/// </summary>
public class ConnectionLostReturner : MonoBehaviour
{
    private SessionManager Session => App.Net.Session;

    private bool m_returning;

    private void Start()
    {
        if (Session != null)
            Session.OnConnectionLost += HandleConnectionLost;
        else
            Debug.LogWarning(
                "ConnectionLostReturner: SessionManager가 없어 드롭 복귀를 걸 수 없다",
                this
            );
    }

    private void OnDestroy()
    {
        if (Session != null)
            Session.OnConnectionLost -= HandleConnectionLost;
    }

    private void HandleConnectionLost(EConnectionLostReason reason)
    {
        if (m_returning)
            return;
        m_returning = true;
        ReturnToTitleAsync().Forget();
    }

    private async UniTaskVoid ReturnToTitleAsync()
    {
        try
        {
            await SessionFlow.WaitForNetworkShutdownAsync();
            if (App.CurrentScene != EScene.Title)
                App.LoadScene(EScene.Title);
        }
        finally
        {
            m_returning = false;
        }
    }
}
