using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 감정표현 재생 중에만 머리 위에 이모지 아이콘을 빌보드로 띄운다(전 피어).
/// </summary>
public class EmoteBubbleView : MonoBehaviour
{
    [Tooltip("이모지를 그릴 Image — 감정표현이 없을 때는 이 오브젝트를 끈다")]
    [SerializeField]
    private Image m_icon;

    [Tooltip("카메라를 향해 돌릴 루트. 비우면 아이콘의 부모를 쓴다")]
    [SerializeField]
    private Transform m_billboardRoot;

    [SerializeField]
    private PlayerEmoteView m_emoteView;

    private Transform m_camera;

    private void Awake()
    {
        if (m_emoteView == null)
            m_emoteView = GetComponentInParent<PlayerEmoteView>();

        if (m_billboardRoot == null && m_icon != null)
            m_billboardRoot = m_icon.transform.parent;

        Hide();
    }

    private void OnEnable()
    {
        if (m_emoteView != null)
            m_emoteView.OnEmoteVisualChanged += HandleEmoteVisualChanged;
    }

    private void OnDisable()
    {
        if (m_emoteView != null)
            m_emoteView.OnEmoteVisualChanged -= HandleEmoteVisualChanged;

        Hide();
    }

    private void LateUpdate()
    {
        if (m_billboardRoot == null || m_icon == null || !m_icon.gameObject.activeSelf)
            return;

        if (m_camera == null)
        {
            if (Camera.main == null)
                return;

            m_camera = Camera.main.transform;
        }

        m_billboardRoot.forward = m_camera.forward;
    }

    private void HandleEmoteVisualChanged(EmoteDefinition definition)
    {
        if (definition == null || definition.BubbleSprite == null)
        {
            Hide();
            return;
        }

        if (m_icon == null)
            return;

        m_icon.sprite = definition.BubbleSprite;
        m_icon.gameObject.SetActive(true);
    }

    private void Hide()
    {
        if (m_icon != null)
            m_icon.gameObject.SetActive(false);
    }
}
