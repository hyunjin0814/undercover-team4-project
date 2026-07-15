using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 화면 중앙 크로스헤어. HUD 프리팹에 부착되며 로컬 싱글턴으로 접근한다. (#184)
/// 레이캐스트가 카메라 정중앙에서 나가므로(PlayerInteractor) 화면 중앙 고정 = 조준점.
/// 네트워크 무관 — 각 클라이언트 로컬 UI.
/// </summary>
public class CrosshairUI : MonoBehaviour
{
    [SerializeField] private Image m_crosshairImage;
    [Tooltip("기본 크로스헤어 색")]
    [SerializeField] private Color m_defaultColor = Color.white;
    [Tooltip("상호작용 가능한 대상 조준 시 색")]
    [SerializeField] private Color m_interactableColor = new Color(1f, 0.85f, 0.2f);

    public static CrosshairUI Instance { get; private set; }

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>조준 대상의 상호작용 가능 여부에 따라 크로스헤어 색을 바꾼다.</summary>
    public void SetInteractable(bool interactable)
    {
        if (m_crosshairImage != null)
            m_crosshairImage.color = interactable ? m_interactableColor : m_defaultColor;
    }
}
