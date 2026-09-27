using Cysharp.Threading.Tasks;

/// <summary>
/// 튜토리얼 출입 단일 정적 진입점 — 들어가기·나가기·권유 여부 플래그를 관리한다.
/// </summary>
public static class TutorialFlow
{
    private const string k_offeredPrefKey = "tutorial.offered";

    public static bool WasOffered => UnityEngine.PlayerPrefs.GetInt(k_offeredPrefKey, 0) == 1;

    /// <summary>권했다고 기록한다 — 안내를 띄운 순간과 튜토리얼에 들어간 순간 양쪽에서 부른다.</summary>
    public static void MarkOffered()
    {
        UnityEngine.PlayerPrefs.SetInt(k_offeredPrefKey, 1);
        UnityEngine.PlayerPrefs.Save();
    }

    /// <summary>튜토리얼로 들어간다 — 타이틀의 [튜토리얼] 버튼과 첫 접속 안내가 부른다.</summary>
    public static void Enter()
    {
        MarkOffered();
        App.LoadScene(EScene.Tutorial);
    }

    /// <summary>튜토리얼에서 타이틀로 나간다(SessionFlow.LeaveToMainAsync 재사용).</summary>
    public static void Exit() => SessionFlow.LeaveToMainAsync().Forget();
}
