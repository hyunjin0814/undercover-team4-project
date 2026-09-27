using UnityEngine;

/// <summary>
/// [임시] 현재 씬 이름을 화면 좌상단에 표시한다. 정식 UI가 나오면 제거한다.
/// </summary>
public class SceneIndicatorHud : MonoBehaviour
{
    private static string Label(EScene scene) =>
        scene switch
        {
            EScene.Title => "메인 메뉴",
            EScene.Lobby => "로비 (대기)",
            EScene.Shop => "상점 (준비)",
            EScene.Game => "게임 (라운드)",
            _ => "…",
        };

    private void OnGUI()
    {
        const float width = 180f;
        const float height = 26f;
        Rect rect = new Rect(12f, 12f, width, height);

        Color prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = prev;

        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 14,
            fontStyle = FontStyle.Bold,
        };
        style.normal.textColor = Color.white;

        GUI.Label(rect, $"현재 씬: {Label(App.CurrentScene)}", style);
    }
}
