using UnityEngine;

/// <summary>
/// 전자기기 먹통의 클라이언트 표현 — 먹통 이벤트의 플래그(<see cref="DeviceBlackoutEvent.IsCommsBlackout"/>)를 구독해
/// 시야 제한(화면 어둡게)과 통신 차단(<see cref="VivoxManager.SetCommsJammed"/>)을 켜고 끈다. (GDD 6-4/4-4, #106)
/// 서버·원격 클라·오프라인 모든 피어에서 각자 자기 화면·자기 무전을 처리한다
/// (플래그 변화는 <see cref="DeviceBlackoutEvent.OnCommsBlackoutChanged"/>가 전 피어에서 발행한다).
/// </summary>
public class DeviceBlackoutView : MonoBehaviour
{
    [Header("참조 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private DeviceBlackoutEvent m_blackout;
    [SerializeField] private VivoxManager m_vivox;

    [Header("시야 제한 오버레이")]
    [Tooltip("먹통 중 화면을 덮는 어둠의 불투명도 (0=효과 없음, 1=완전 암전). 시야를 '제한'하되 완전히 가리지는 않게")]
    [SerializeField, Range(0f, 1f)] private float m_overlayAlpha = 0.75f;
    [Tooltip("오버레이 위에 표시할 안내 문구")]
    [SerializeField] private string m_overlayLabel = "⚠ 전자기기 먹통 — 시야·통신 장애";

    private bool m_blackoutActive;
    private Texture2D m_overlayTexture;
    private GUIStyle m_labelStyle;

    private void Start()
    {
        if (m_blackout == null)
            m_blackout = FindFirstObjectByType<DeviceBlackoutEvent>();
        if (m_vivox == null)
            m_vivox = FindFirstObjectByType<VivoxManager>();

        if (m_blackout == null)
        {
            Debug.LogWarning("DeviceBlackoutView: DeviceBlackoutEvent를 찾지 못해 먹통 표현이 동작하지 않는다", this);
            return;
        }

        m_blackout.OnCommsBlackoutChanged += HandleBlackoutChanged;
        // 늦게 붙었을 때(이미 먹통 진행 중) 현재 상태를 즉시 반영
        HandleBlackoutChanged(m_blackout.IsCommsBlackout);
    }

    private void OnDestroy()
    {
        if (m_blackout != null)
            m_blackout.OnCommsBlackoutChanged -= HandleBlackoutChanged;

        if (m_overlayTexture != null)
            Destroy(m_overlayTexture);
    }

    private void HandleBlackoutChanged(bool active)
    {
        m_blackoutActive = active;
        // 통신 차단은 이 피어의 무전(VivoxManager)에 위임 — 없으면(본부 단독·미설정) 건너뛴다
        if (m_vivox != null)
            m_vivox.SetCommsJammed(active);
    }

    private void OnGUI()
    {
        if (!m_blackoutActive || m_overlayAlpha <= 0f)
            return;

        EnsureGuiResources();

        // 화면 전체를 반투명 어둠으로 덮어 시야를 제한한다
        Color previous = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, m_overlayAlpha);
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), m_overlayTexture);
        GUI.color = previous;

        if (!string.IsNullOrEmpty(m_overlayLabel))
            GUI.Label(new Rect(0f, Screen.height * 0.5f - 16f, Screen.width, 32f), m_overlayLabel, m_labelStyle);
    }

    private void EnsureGuiResources()
    {
        if (m_overlayTexture == null)
        {
            m_overlayTexture = new Texture2D(1, 1);
            m_overlayTexture.SetPixel(0, 0, Color.white); // 색은 GUI.color로 입힌다
            m_overlayTexture.Apply();
        }

        if (m_labelStyle == null)
        {
            m_labelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 20,
                fontStyle = FontStyle.Bold
            };
            m_labelStyle.normal.textColor = new Color(1f, 0.4f, 0.4f);
        }
    }
}
