using UnityEngine;

/// <summary>
/// Title 씬 매니저 — 세션 생성·참가 화면을 담당한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class TitleManager : SceneManagerBase
{
    /// <summary>세션 생성 직후 호스트가 Lobby 씬을 로드한다.</summary>
    public void StartGame() => MoveToNextScene(EScene.Lobby);
}
