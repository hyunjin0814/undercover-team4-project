using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 감정표현 전체 목록 SO. 리스트 인덱스가 재생 동기화의 네트워크 계약이라 배포 후 순서를 바꾸지 않는다.
/// 로비 저장은 id를 쓰며, IndexOf가 둘을 잇는다.
/// </summary>
[CreateAssetMenu(fileName = "EmoteCatalog", menuName = "Scriptable Objects/Emote Catalog")]
public class EmoteCatalog : ScriptableObject
{
    [Tooltip("인덱스가 네트워크 계약 — 배포 후에는 순서를 바꾸지 말 것. 추가는 뒤에")]
    [SerializeField]
    private EmoteDefinition[] m_emotes;

    private Dictionary<string, int> m_indexById;

    public int Count => m_emotes?.Length ?? 0;

    public bool IsValidIndex(int index) => index >= 0 && index < Count && m_emotes[index] != null;

    /// <summary>인덱스로 정의를 얻는다 — 범위 밖이거나 비워 둔 자리면 null.</summary>
    public EmoteDefinition Get(int index) => IsValidIndex(index) ? m_emotes[index] : null;

    /// <summary>id로 인덱스를 찾는다 — 없으면 -1. 로비 구성(id)을 재생(인덱스)으로 잇는 지점.</summary>
    public int IndexOf(string id)
    {
        if (string.IsNullOrEmpty(id) || Count == 0)
            return -1;

        if (m_indexById == null)
        {
            m_indexById = new Dictionary<string, int>(Count);
            for (int i = 0; i < m_emotes.Length; i++)
            {
                if (m_emotes[i] == null || string.IsNullOrEmpty(m_emotes[i].Id))
                    continue;

                if (!m_indexById.ContainsKey(m_emotes[i].Id))
                    m_indexById.Add(m_emotes[i].Id, i);
            }
        }

        return m_indexById.TryGetValue(id, out int index) ? index : -1;
    }
}
