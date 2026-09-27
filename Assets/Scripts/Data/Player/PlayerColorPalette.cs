using UnityEngine;

/// <summary>
/// 고를 수 있는 로봇 색 팔레트 SO. 인덱스가 동기화 값이므로 새 색은 항상 끝에 추가한다.
/// </summary>
[CreateAssetMenu(fileName = "PlayerColors", menuName = "Scriptable Objects/PlayerColorPalette")]
public class PlayerColorPalette : ScriptableObject
{
    [Tooltip("고를 수 있는 색 — 인덱스가 곧 동기화 값이라 순서를 바꾸지 말 것")]
    [SerializeField] private Color[] m_colors;

    public int Count => m_colors != null ? m_colors.Length : 0;

    /// <summary>인덱스의 색을 돌려준다. 범위 밖이면 잘라 쓰고, 비어 있으면 흰색이다.</summary>
    public Color Get(int index)
    {
        if (Count == 0)
            return Color.white;

        return m_colors[Mathf.Clamp(index, 0, Count - 1)];
    }
}
