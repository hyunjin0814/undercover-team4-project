#if UNITY_EDITOR
using Cysharp.Threading.Tasks;
using Unity.Multiplayer.PlayMode;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 게임 맵 씬을 직접 Play할 때 로컬 호스트를 띄우고 라운드를 자동 시작하는 에디터 전용 도구.
/// MPPM 클론에서는 '클라이언트 참가' 버튼만 띄운다.
/// </summary>
public class DevAutoHost : MonoBehaviour
{
    private void Start()
    {
        if (!CurrentPlayer.IsMainEditor)
            return;

        AutoHostAsync().Forget();
    }

    private async UniTaskVoid AutoHostAsync()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogWarning("[DevAutoHost] NetworkManager가 없어 자동 호스트를 건너뛴다", this);
            return;
        }

        if (nm.IsListening)
            return;

        await UniTask.NextFrame(this.GetCancellationTokenOnDestroy());

        if (App.Net.Session != null)
        {
            ConnectionApprovalGate.StampLocalPayload(nm);
            App.Net.Session.Approval.Install(nm);
        }

        nm.StartHost();

        await UniTask.NextFrame(this.GetCancellationTokenOnDestroy());

        App.Game.ReadyGate?.ReportSelfReady();

        App.Game.Round?.BeginRoundPreparation();
        Debug.Log("[DevAutoHost] 로컬 호스트 + 라운드 자동 준비 시작 (게임 씬 직접 Play)");
    }

    private void OnGUI()
    {
        if (CurrentPlayer.IsMainEditor)
            return;

        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.IsListening)
            return;

        GUILayout.BeginArea(new Rect(10, 10, 260, 60));
        if (GUILayout.Button("클라이언트로 참가 (127.0.0.1)"))
        {
            ConnectionApprovalGate.StampLocalPayload(nm);
            nm.StartClient();
        }
        GUILayout.EndArea();
    }
}
#endif
