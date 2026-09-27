using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 감정표현 1종의 정의 — 재생할 클립과 머리 위에 띄울 말풍선 스프라이트.
/// 둘의 유무 조합으로 댄스/이모지/둘 다를 표현한다.
/// </summary>
[CreateAssetMenu(fileName = "Emote", menuName = "Scriptable Objects/Emote Definition")]
public class EmoteDefinition : ScriptableObject
{
    [Tooltip("안정적 키 — 로비 구성 저장에 쓴다. 영숫자·밑줄만. '|'는 저장 구분자라 쓸 수 없다")]
    [SerializeField]
    private string m_id;

    [Tooltip("휠·로비 목록에 표시할 이름 — EmoteTable의 Emote.Name.<Id>")]
    [SerializeField]
    private LocalizedString m_displayName;

    [Tooltip("휠 칸 아이콘")]
    [SerializeField]
    private Sprite m_icon;

    [Tooltip("재생할 애니메이션. 비우면 애니메이션 없이 이모지만 뜬다")]
    [SerializeField]
    private AnimationClip m_clip;

    [Tooltip("켜면 취소할 때까지 무한 반복, 끄면 클립 1회 후 자동 종료")]
    [SerializeField]
    private bool m_loop = true;

    [Tooltip("머리 위에 띄울 아이콘. 비우면 표시하지 않는다")]
    [SerializeField]
    private Sprite m_bubbleSprite;

    public string Id => m_id;

    public LocalizedString DisplayName => m_displayName;

    public Sprite Icon => m_icon;
    public AnimationClip Clip => m_clip;
    public bool Loop => m_loop;
    public Sprite BubbleSprite => m_bubbleSprite;

    public float DurationSeconds => m_loop || m_clip == null ? 0f : m_clip.length;
}
