using System;
using UnityEngine;

/// <summary>
/// 효과음(EAudioClip)과 BGM(EBgm)의 클립·볼륨·시작 위치·감쇠 거리를 담는 오디오 카탈로그 SO.
/// SoundManager가 유일한 소비자다.
/// </summary>
[CreateAssetMenu(fileName = "AudioLibrary", menuName = "Scriptable Objects/AudioLibrary")]
public class AudioLibrary : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        [Tooltip("이 항목을 가리키는 키 — 코드에서 App.Sound.PlaySfxAt(Id, ...)로 부른다")]
        public EAudioClip Id;

        [Tooltip("재생할 클립. 비우면 이 소리는 조용히 무동작한다(배선만 먼저 하고 나중에 채워도 된다)")]
        public AudioClip Clip;

        [Tooltip("재생 볼륨 배율. 전역 음량(GameSettings.MasterVolume)이 위에 한 번 더 곱해진다")]
        [Range(0f, 1f)]
        public float Volume = 1f;

        [Tooltip("클립 앞을 이만큼(초) 건너뛰고 재생한다. 도입부가 여린 음원이 한 박자 늦게 들릴 때 쓴다 — 0이면 처음부터")]
        [Min(0f)]
        public float StartOffset;

        [Tooltip("이 거리(m)까지는 감쇠 없이 최대 볼륨")]
        [Min(0.1f)]
        public float MinDistance = 2f;

        [Tooltip("이 거리(m) 밖에서는 들리지 않는다")]
        [Min(1f)]
        public float MaxDistance = 25f;
    }

    [Serializable]
    public class BgmEntry
    {
        [Tooltip("이 곡을 가리키는 키 — 코드에서 App.Sound.PlayBgm(Id)로 부른다")]
        public EBgm Id;

        [Tooltip("재생할 곡. 비우면 이 BGM은 조용히 무동작한다(배선만 먼저 하고 나중에 채워도 된다)")]
        public AudioClip Clip;

        [Tooltip("재생 볼륨 배율. 효과음에 묻히지 않게 보통 효과음보다 낮게 둔다")]
        [Range(0f, 1f)]
        public float Volume = 0.5f;
    }

    [Serializable]
    public class SceneBgmEntry
    {
        [Tooltip("이 씬에 들어가면")]
        public EScene Scene;

        [Tooltip("이 곡을 튼다. None이면 그 씬에서는 BGM을 끈다")]
        public EBgm Bgm;
    }

    [Tooltip("효과음 목록. 같은 Id가 둘 이상이면 먼저 오는 것만 쓰이고 매니저가 경고한다")]
    [SerializeField] private Entry[] m_entries;

    [Tooltip("BGM 목록. 같은 Id가 둘 이상이면 먼저 오는 것만 쓰이고 매니저가 경고한다")]
    [SerializeField] private BgmEntry[] m_bgm;

    [Tooltip("씬별 BGM. 여기 없는 씬은 무음이다. 두 씬에 같은 곡을 적으면 그 사이 전환에서 곡이 이어진다")]
    [SerializeField] private SceneBgmEntry[] m_sceneBgm;

    public Entry[] Entries => m_entries ?? Array.Empty<Entry>();

    public BgmEntry[] BgmEntries => m_bgm ?? Array.Empty<BgmEntry>();

    public SceneBgmEntry[] SceneBgmEntries => m_sceneBgm ?? Array.Empty<SceneBgmEntry>();
}
