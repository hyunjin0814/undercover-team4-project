using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 씬별 BGM 전환과 크로스페이드를 담당한다. SoundManager와 같은 오브젝트에 붙어 함께 상주한다.
/// 씬이 바뀌어도 같은 곡이면 다시 시작하지 않는다.
/// </summary>
public class BgmPlayer : MonoBehaviour
{
    [Tooltip("곡을 갈아탈 때 겹쳐 넘기는 시간(초). 0이면 즉시 바뀐다")]
    [Min(0f)]
    [SerializeField] private float m_fadeSeconds = 1.2f;

    private readonly Dictionary<EBgm, AudioLibrary.BgmEntry> m_entries = new();
    private readonly Dictionary<EScene, EBgm> m_sceneBgm = new();

    private readonly HashSet<EBgm> m_warned = new();

    private AudioSource[] m_sources;
    private int m_active = -1;

    private float[] m_gain;
    private float[] m_gainTarget;
    private float[] m_volume;

    public EBgm Current { get; private set; } = EBgm.None;

    /// <summary>SoundManager가 Awake에서 카탈로그를 넘겨 초기화한다.</summary>
    public void Initialize(AudioLibrary library)
    {
        BuildSources();
        BuildIndex(library);
    }

    private void Start()
    {
        App.OnSceneLoaded += HandleSceneLoaded;
        ApplyScene(App.CurrentScene);
    }

    private void OnDestroy() => App.OnSceneLoaded -= HandleSceneLoaded;

    /// <summary>곡을 재생한다. 같은 곡이면 그대로 두고, 다른 곡이면 크로스페이드로 넘긴다.</summary>
    public void Play(EBgm id)
    {
        if (id == Current)
            return;

        if (id == EBgm.None)
        {
            Stop();
            return;
        }

        if (!m_entries.TryGetValue(id, out AudioLibrary.BgmEntry entry))
        {
            WarnOnce(id, $"카탈로그에 BGM {id} 항목이 없다");
            return;
        }

        if (entry.Clip == null)
        {
            Stop();
            Current = id;
            return;
        }

        if (m_sources == null)
            return;

        int next = m_active == 0 ? 1 : 0;

        m_sources[next].clip = entry.Clip;
        m_sources[next].volume = 0f;
        m_sources[next].Play();

        m_volume[next] = entry.Volume;
        m_gain[next] = 0f;
        m_gainTarget[next] = 1f;

        if (m_active >= 0)
            m_gainTarget[m_active] = 0f;

        m_active = next;
        Current = id;
    }

    /// <summary>곡을 끈다 — 페이드 아웃 후 정지한다.</summary>
    public void Stop()
    {
        if (m_active >= 0)
            m_gainTarget[m_active] = 0f;

        m_active = -1;
        Current = EBgm.None;
    }

    private void Update()
    {
        if (m_sources == null)
            return;

        float step = m_fadeSeconds > 0f
            ? Time.unscaledDeltaTime / m_fadeSeconds
            : 1f;

        for (int i = 0; i < m_sources.Length; i++)
        {
            if (Mathf.Approximately(m_gain[i], m_gainTarget[i]))
                continue;

            m_gain[i] = Mathf.MoveTowards(m_gain[i], m_gainTarget[i], step);
            ApplyVolume(i);

            if (m_gain[i] <= 0f && m_sources[i].isPlaying)
                m_sources[i].Stop();
        }
    }

    /// <summary>설정의 배경음 음량을 지금 재생 중인 곡에 다시 적용한다.</summary>
    public void ApplyVolume()
    {
        if (m_sources == null)
            return;

        for (int i = 0; i < m_sources.Length; i++)
            ApplyVolume(i);
    }

    private void ApplyVolume(int index) =>
        m_sources[index].volume = m_gain[index] * m_volume[index] * GameSettings.BgmVolume;

    private void HandleSceneLoaded(EScene scene) => ApplyScene(scene);

    private void ApplyScene(EScene scene)
    {
        Play(m_sceneBgm.TryGetValue(scene, out EBgm bgm) ? bgm : EBgm.None);
    }

    private void BuildIndex(AudioLibrary library)
    {
        if (library == null)
            return;

        foreach (AudioLibrary.BgmEntry entry in library.BgmEntries)
        {
            if (entry == null || entry.Id == EBgm.None)
                continue;

            if (!m_entries.TryAdd(entry.Id, entry))
                Debug.LogError($"[BgmPlayer] 카탈로그에 BGM {entry.Id}가 중복 등록됐다 — 먼저 오는 항목만 쓰인다", library);
        }

        foreach (AudioLibrary.SceneBgmEntry entry in library.SceneBgmEntries)
        {
            if (entry == null || entry.Scene == EScene.None)
                continue;

            if (!m_sceneBgm.TryAdd(entry.Scene, entry.Bgm))
                Debug.LogError($"[BgmPlayer] 씬 표에 {entry.Scene}이 중복 등록됐다 — 먼저 오는 항목만 쓰인다", library);
        }
    }

    private void BuildSources()
    {
        m_sources = new AudioSource[2];
        m_gain = new float[2];
        m_gainTarget = new float[2];
        m_volume = new float[2];

        for (int i = 0; i < m_sources.Length; i++)
        {
            var host = new GameObject($"BgmSource_{i}");
            host.transform.SetParent(transform, false);

            AudioSource source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = 0f;
            source.volume = 0f;
            m_sources[i] = source;
        }
    }

    private void WarnOnce(EBgm id, string reason)
    {
        if (m_warned.Add(id))
            Debug.LogWarning($"[BgmPlayer] {reason} — 해당 BGM을 건너뛴다", this);
    }
}
