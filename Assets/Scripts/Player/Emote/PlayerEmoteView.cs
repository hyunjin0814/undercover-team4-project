using System;
using UnityEngine;

/// <summary>
/// 감정표현 재생 상태 변경을 구독해 애니메이터 파라미터와 이모지 표시에 반영한다(전 피어).
/// </summary>
[RequireComponent(typeof(PlayerEmote))]
public class PlayerEmoteView : MonoBehaviour
{
    private static readonly int s_emoteHash = Animator.StringToHash("Emote");
    private static readonly int s_emoteIndexHash = Animator.StringToHash("EmoteIndex");

    [Tooltip("비우면 자식에서 자동으로 찾는다")]
    [SerializeField]
    private Animator m_animator;

    private PlayerEmote m_emote;
    private PlayerLook m_look;

    public event Action<EmoteDefinition> OnEmoteVisualChanged;

    private void Awake()
    {
        m_emote = GetComponent<PlayerEmote>();
        m_look = GetComponent<PlayerLook>();

        if (m_animator == null)
            m_animator = GetComponentInChildren<Animator>();
    }

    private void OnEnable()
    {
        m_emote.OnActiveEmoteChanged += HandleActiveEmoteChanged;
        Apply(m_emote.ActiveEmote);
    }

    private void OnDisable()
    {
        m_emote.OnActiveEmoteChanged -= HandleActiveEmoteChanged;
        Apply(PlayerEmote.k_none);
    }

    private void HandleActiveEmoteChanged(sbyte index) => Apply(index);

    private void Apply(sbyte index)
    {
        EmoteCatalog catalog = m_emote.Catalog;
        EmoteDefinition definition = catalog != null ? catalog.Get(index) : null;
        bool active = definition != null;

        if (m_animator != null)
        {
            m_animator.SetInteger(s_emoteIndexHash, active ? index : PlayerEmote.k_none);
            m_animator.SetBool(s_emoteHash, active && definition.Clip != null);
        }

        if (m_look != null)
            m_look.SetEmoteView(active);

        OnEmoteVisualChanged?.Invoke(definition);
    }
}
