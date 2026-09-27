using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 폭탄 위치 HUD — 화면 안이면 폭탄 위에 마커를, 밖이면 가장자리에 방향 표시를 그린다.
/// 임시로 OnGUI를 쓴다.
/// </summary>
[RequireComponent(typeof(BombDevice))]
public class BombLocatorHud : MonoBehaviour
{
    [Tooltip("마커를 띄울 폭탄 위쪽 높이(m)")]
    [SerializeField]
    private float m_markerHeight = 0.7f;

    [Tooltip("화면 밖일 때 가장자리에서 띄우는 여백(px)")]
    [SerializeField]
    private float m_edgeMargin = 56f;

    [Tooltip("남은 시간이 이 값(초) 이하면 경고색으로 바뀐다 — BombTimerView와 같은 기준")]
    [SerializeField]
    private float m_warnThreshold = 10f;

    [SerializeField]
    private Color m_normalColor = new Color(1f, 0.72f, 0.15f, 0.95f);

    [SerializeField]
    private Color m_warnColor = new Color(1f, 0.28f, 0.22f, 0.95f);

    private const int k_drawDepth = -90;
    private const float k_markerSize = 22f;
    private const float k_labelWidth = 150f;
    private const float k_labelHeight = 20f;

    private BombDevice m_device;
    private GUIStyle m_labelStyle;
    private string m_offScreenArrow = "";

    private static Camera s_viewCamera;

    private static Camera ViewCamera
    {
        get
        {
            if (s_viewCamera != null)
                return s_viewCamera;

            NetworkManager nm = NetworkManager.Singleton;
            if (nm != null && nm.LocalClient != null && nm.LocalClient.PlayerObject != null)
                s_viewCamera = nm.LocalClient.PlayerObject.GetComponentInChildren<Camera>();

            if (s_viewCamera == null)
                s_viewCamera = Camera.main;

            return s_viewCamera;
        }
    }

    private void Awake()
    {
        m_device = GetComponent<BombDevice>();
    }

    private void OnGUI()
    {
        if (m_device == null || !m_device.IsCountingDown)
            return;

        Camera cam = ViewCamera;
        if (cam == null)
            return;

        EnsureStyle();

        float remaining = m_device.RemainingSeconds;
        Color color = remaining <= m_warnThreshold ? m_warnColor : m_normalColor;
        float distance = Vector3.Distance(cam.transform.position, transform.position);

        Vector3 screen = cam.WorldToScreenPoint(transform.position + Vector3.up * m_markerHeight);

        bool behind = screen.z <= 0f;
        if (behind)
        {
            screen.x = Screen.width - screen.x;
            screen.y = Screen.height - screen.y;
        }

        Vector2 point = new Vector2(screen.x, Screen.height - screen.y);

        bool onScreen = !behind
            && point.x >= 0f && point.x <= Screen.width
            && point.y >= 0f && point.y <= Screen.height;

        if (!onScreen)
        {
            Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            Vector2 dir = point - center;
            if (dir.sqrMagnitude < 0.01f)
                dir = Vector2.up;
            dir.Normalize();

            float halfW = Screen.width * 0.5f - m_edgeMargin;
            float halfH = Screen.height * 0.5f - m_edgeMargin;
            float scale = Mathf.Min(
                Mathf.Abs(dir.x) > 0.0001f ? halfW / Mathf.Abs(dir.x) : float.MaxValue,
                Mathf.Abs(dir.y) > 0.0001f ? halfH / Mathf.Abs(dir.y) : float.MaxValue);
            point = center + dir * scale;
            m_offScreenArrow = ArrowFor(dir);
        }

        int previousDepth = GUI.depth;
        GUI.depth = k_drawDepth;
        DrawMarker(point, color, onScreen);
        DrawLabel(point, color, distance, remaining, onScreen);
        GUI.depth = previousDepth;
    }

    private static void DrawMarker(Vector2 point, Color color, bool onScreen)
    {
        Color previous = GUI.color;
        GUI.color = color;

        Rect rect = new Rect(point.x - k_markerSize * 0.5f, point.y - k_markerSize * 0.5f, k_markerSize, k_markerSize);
        if (onScreen)
        {
            const float t = 2f;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - t, rect.width, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, t, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMax - t, rect.y, t, rect.height), Texture2D.whiteTexture);
        }
        else
        {
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
        }

        GUI.color = previous;
    }

    private void DrawLabel(Vector2 point, Color color, float distance, float remaining, bool onScreen)
    {
        int minutes = (int)(remaining / 60f);
        int seconds = (int)(remaining % 60f);
        string text = string.Format("폭탄 {0:F0}m · {1}:{2:00}{3}",
            distance, minutes, seconds, onScreen ? "" : " " + m_offScreenArrow);

        Rect rect = new Rect(point.x - k_labelWidth * 0.5f, point.y + k_markerSize * 0.5f + 2f, k_labelWidth, k_labelHeight);

        Color previous = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;

        m_labelStyle.normal.textColor = color;
        GUI.Label(rect, text, m_labelStyle);
    }

    /// <summary>화면 밖 방향을 8방향 화살표 문자로 바꾼다.</summary>
    private static string ArrowFor(Vector2 guiDirection)
    {
        float angle = Mathf.Atan2(-guiDirection.y, guiDirection.x) * Mathf.Rad2Deg;
        if (angle < 0f)
            angle += 360f;

        int sector = Mathf.RoundToInt(angle / 45f) % 8;
        switch (sector)
        {
            case 0: return "→";
            case 1: return "↗";
            case 2: return "↑";
            case 3: return "↖";
            case 4: return "←";
            case 5: return "↙";
            case 6: return "↓";
            default: return "↘";
        }
    }

    private void EnsureStyle()
    {
        if (m_labelStyle != null)
            return;

        m_labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
        };
    }
}
