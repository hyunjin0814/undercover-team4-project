using System;
using UnityEngine;

/// <summary>
/// 일회성 이펙트 카탈로그 SO — EEffect별 프리팹·수명·사전 생성 개수.
/// </summary>
[CreateAssetMenu(fileName = "EffectLibrary", menuName = "Scriptable Objects/EffectLibrary")]
public class EffectLibrary : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        [Tooltip("이 항목을 가리키는 키 — 코드에서 App.Game.Effect.Play(Id, ...)로 부른다")]
        public EEffect Id;

        [Tooltip("재생할 프리팹. 비우면 이 이펙트는 조용히 무동작한다")]
        public GameObject Prefab;

        [Tooltip("재생 시작 후 이 시간(초)이 지나면 풀에 반납한다. 파티클 지속 시간보다 넉넉히 잡을 것")]
        [Min(0.05f)]
        public float LifetimeSeconds = 1f;

        [Tooltip("시작 시 미리 만들어 둘 개수. 첫 재생의 인스턴스화 끊김을 없앤다 — 0이면 필요할 때 만든다")]
        [Min(0)]
        public int Prewarm;
    }

    [Tooltip("이펙트 목록. 같은 Id가 둘 이상이면 먼저 오는 것만 쓰이고 매니저가 경고한다")]
    [SerializeField] private Entry[] m_entries;

    public Entry[] Entries => m_entries ?? Array.Empty<Entry>();
}
