using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// App.LoadScene의 실제 구현 — 세션 중이면 NGO 씬 동기화, 아니면 로컬로 비동기 로드한다.
/// 직접 호출하지 말고 App.LoadScene을 쓸 것.
/// </summary>
public static class AppHelper
{
    private const float k_activationReadyProgress = 0.9f;

    private const int k_firstRenderFrames = 2;

    private const float k_networkLoadTimeoutSeconds = 30f;

    private const string k_defaultGameScene = "Map_Apocalypse";

    private const string k_tutorialScene = "Tutorial";

    /// <summary>EScene → 실제 씬 이름. 빌드 인덱스에 결합하지 않는다 (NGO도 이름 기반 로드).</summary>
    public static string ToSceneName(EScene scene) =>
        scene switch
        {
            EScene.Title => "Title Scene",
            EScene.Lobby => "Lobby",
            EScene.Shop => "Shop",
            EScene.Game => App.Game.MapSelection?.SelectedSceneName ?? k_defaultGameScene,
            EScene.Tutorial => k_tutorialScene,
            _ => null,
        };

    private static EScene FromSceneName(string sceneName) =>
        sceneName switch
        {
            "Title Scene" => EScene.Title,
            "Lobby" => EScene.Lobby,
            "Shop" => EScene.Shop,
            "Tutorial" => EScene.Game,
            _ => App.SceneFlow.Game != null ? EScene.Game : EScene.None,
        };

    /// <summary>씬을 비동기로 로드하고 진행률(0~1)을 onProgress로 보고한다.</summary>
    internal static async UniTask LoadSceneAsync(
        EScene scene,
        CancellationToken token,
        Action<float> onProgress = null
    )
    {
        string sceneName = ToSceneName(scene);
        if (sceneName == null)
        {
            Debug.LogError($"[AppHelper] 로드할 수 없는 씬: {scene}");
            return;
        }

        NetworkManager net = NetworkManager.Singleton;

        if (net != null && net.IsListening)
        {
            if (!net.IsServer)
            {
                Debug.LogError($"[AppHelper] 세션 중 씬 전환은 서버만 할 수 있습니다: {scene}");
                return;
            }

            await LoadViaNetworkAsync(net, sceneName, token, onProgress);
            return;
        }

        await LoadLocalAsync(sceneName, token, onProgress);
    }

    private static async UniTask LoadLocalAsync(
        string sceneName,
        CancellationToken token,
        Action<float> onProgress
    )
    {
        AsyncOperation op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
        if (op == null)
        {
            Debug.LogError($"[AppHelper] 씬 로드를 시작하지 못했습니다: {sceneName}");
            return;
        }

        op.allowSceneActivation = false;

        while (op.progress < k_activationReadyProgress)
        {
            onProgress?.Invoke(op.progress / k_activationReadyProgress);
            await UniTask.Yield(PlayerLoopTiming.Update, token);
        }

        onProgress?.Invoke(1f);

        op.allowSceneActivation = true;
        await op.ToUniTask(cancellationToken: token);

        await UniTask.DelayFrame(k_firstRenderFrames, PlayerLoopTiming.Update, token);
    }

    private static async UniTask LoadViaNetworkAsync(
        NetworkManager net,
        string sceneName,
        CancellationToken token,
        Action<float> onProgress
    )
    {
        bool localLoaded = false;

        AsyncOperation localOp = null;

        void HandleLoad(
            ulong clientId,
            string loadedScene,
            LoadSceneMode loadMode,
            AsyncOperation operation
        )
        {
            if (clientId == net.LocalClientId && loadedScene == sceneName)
                localOp = operation;
        }

        void HandleLoadComplete(ulong clientId, string loadedScene, LoadSceneMode mode)
        {
            if (clientId == net.LocalClientId && loadedScene == sceneName)
                localLoaded = true;
        }

        net.SceneManager.OnLoad += HandleLoad;
        net.SceneManager.OnLoadComplete += HandleLoadComplete;
        try
        {
            SceneEventProgressStatus status = net.SceneManager.LoadScene(
                sceneName,
                LoadSceneMode.Single
            );
            if (status != SceneEventProgressStatus.Started)
            {
                Debug.LogError($"[AppHelper] NGO 씬 로드 실패: {sceneName} ({status})");
                return;
            }

            float deadline = Time.realtimeSinceStartup + k_networkLoadTimeoutSeconds;
            while (!localLoaded && net.IsListening && Time.realtimeSinceStartup < deadline)
            {
                if (localOp != null)
                    onProgress?.Invoke(localOp.progress);
                await UniTask.Yield(PlayerLoopTiming.Update, token);
            }

            if (localLoaded)
                onProgress?.Invoke(1f);
            else
                Debug.LogWarning(
                    $"[AppHelper] NGO 씬 로드 완료를 확인하지 못했습니다: {sceneName} — 그대로 진행"
                );
        }
        finally
        {
            if (net.SceneManager != null)
            {
                net.SceneManager.OnLoad -= HandleLoad;
                net.SceneManager.OnLoadComplete -= HandleLoadComplete;
            }
        }

        await UniTask.DelayFrame(k_firstRenderFrames, PlayerLoopTiming.Update, token);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void HookSceneLoaded()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InitCurrentScene()
    {
        if (App.CurrentScene == EScene.None)
            App.NotifySceneLoaded(FromSceneName(SceneManager.GetActiveScene().name));
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Single)
            return;

        App.NotifySceneLoaded(FromSceneName(scene.name));
    }
}
