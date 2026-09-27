using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// UI 버튼 클릭음 — 누른 위치 아래가 버튼이면 소리를 낸다.
/// AppBootstrap에 상주해 버튼별 배선 없이 모든 씬에 적용된다.
/// </summary>
public class UiClickSound : MonoBehaviour
{
    [Tooltip("낼 소리. None이면 클릭음이 나지 않는다")]
    [SerializeField] private EAudioClip m_clip = EAudioClip.UiClick;

    private readonly List<RaycastResult> m_hits = new();

    private void Update()
    {
        if (m_clip == EAudioClip.None)
            return;

        Pointer pointer = Pointer.current;
        if (pointer == null || !pointer.press.wasPressedThisFrame)
            return;

        EventSystem events = EventSystem.current;
        if (events == null)
            return;

        var data = new PointerEventData(events) { position = pointer.position.ReadValue() };
        m_hits.Clear();
        events.RaycastAll(data, m_hits);
        if (m_hits.Count == 0)
            return;

        Selectable target = m_hits[0].gameObject.GetComponentInParent<Selectable>();
        if (target == null || !target.IsInteractable())
            return;

        if (target is not Button && target is not Toggle)
            return;

        App.Sound?.PlaySfx2D(m_clip);
    }
}
