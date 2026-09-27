using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 게임 맵 씬 매니저 — 씬 준비 완료 대기와 준비 보고를 담당한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class InGameManager : SceneManagerBase
{
    private const float k_localReadyTimeoutSeconds = 20f;

    /// <summary>로컬 준비가 끝나면 SceneReadyGate에 보고하고 곧바로 로딩 화면을 내린다(전원을 기다리지 않는다).</summary>
    public override async UniTask WaitUntilReadyAsync(CancellationToken token)
    {
        await WaitUntilLocallyReadyAsync(
            token, IsLocallyReady, k_localReadyTimeoutSeconds, "로컬 준비");

        App.Game.ReadyGate?.ReportSelfReady();
    }

    private static bool IsLocallyReady()
    {
        NetworkManager net = NetworkManager.Singleton;

        if (net != null && net.IsListening && !net.IsServer)
            return net.LocalClient != null && net.LocalClient.PlayerObject != null;

        NpcSpawner spawner = App.Game.NpcSpawner;
        return spawner == null || spawner.IsSpawnCompleted;
    }
}
