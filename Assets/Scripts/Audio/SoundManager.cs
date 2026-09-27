using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 효과음 재생 매니저 — 3D/2D 원샷과 2D 루프를 풀로 재생한다. App.Sound로 접근한다.
/// BGM은 같은 오브젝트의 BgmPlayer가 맡고, 효과음 음량은 재생 지점에서 곱한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
[RequireComponent(typeof(BgmPlayer))]
public class SoundManager : CommonManagerBase
{
    [Tooltip("오디오 카탈로그 — 비우면 모든 재생 요청이 무동작한다")]
    [SerializeField] private AudioLibrary m_library;

    [Tooltip("동시 재생 가능한 효과음 개수. 모자라면 가장 오래 재생 중인 소리를 끊고 재사용한다")]
    [Min(1)]
    [SerializeField] private int m_sourceCount = 16;

    private readonly Dictionary<EAudioClip, AudioLibrary.Entry> m_entries = new();

    private AudioSource[] m_sources;

    private float[] m_startTimes;

    private AudioSource m_loopSource;
    private EAudioClip m_loopId = EAudioClip.None;

    private AudioSource m_ambientSource;
    private EAudioClip m_ambientId = EAudioClip.None;

    private readonly HashSet<EAudioClip> m_warned = new();

    public BgmPlayer Bgm { get; private set; }

    protected override void Awake()
    {
        base.Awake();

        BuildIndex();
        BuildSources();

        Bgm = GetComponent<BgmPlayer>();
        Bgm.Initialize(m_library);

        GameSettings.OnSfxVolumeChanged += HandleSfxVolumeChanged;
    }

    protected override void OnDestroy()
    {
        GameSettings.OnSfxVolumeChanged -= HandleSfxVolumeChanged;
        base.OnDestroy();
    }

    /// <summary>이 항목을 현재 효과음 설정으로 재생할 때의 음량을 돌려준다.</summary>
    public static float SfxVolumeOf(AudioLibrary.Entry entry) =>
        entry == null ? 0f : entry.Volume * GameSettings.SfxVolume;

    private void HandleSfxVolumeChanged(float _)
    {
        if (m_loopSource != null && m_loopId != EAudioClip.None
            && m_entries.TryGetValue(m_loopId, out AudioLibrary.Entry loop))
        {
            m_loopSource.volume = SfxVolumeOf(loop);
        }

        if (m_ambientSource != null && m_ambientId != EAudioClip.None
            && m_entries.TryGetValue(m_ambientId, out AudioLibrary.Entry ambient))
        {
            m_ambientSource.volume = SfxVolumeOf(ambient);
        }
    }

    /// <summary>효과음을 지정한 월드 좌표에서 한 번 재생한다(로컬 재생).</summary>
    public void PlaySfxAt(EAudioClip id, Vector3 position, float volumeScale = 1f)
    {
        if (id == EAudioClip.None)
            return;

        if (!m_entries.TryGetValue(id, out AudioLibrary.Entry entry))
        {
            WarnOnce(id, $"카탈로그에 {id} 항목이 없다");
            return;
        }

        if (entry.Clip == null)
        {
            WarnOnce(id, $"{id} 항목에 클립이 배정되지 않았다");
            return;
        }

        AudioSource source = RentSource();
        if (source == null)
            return;

        source.transform.position = position;
        source.spatialBlend = 1f;
        source.clip = entry.Clip;
        source.volume = Mathf.Clamp01(SfxVolumeOf(entry) * volumeScale);
        ApplyStartOffset(source, entry);
        source.minDistance = entry.MinDistance;
        source.maxDistance = Mathf.Max(entry.MaxDistance, entry.MinDistance + 0.1f);
        source.Play();
    }

    /// <summary>위치 없이(2D) 효과음을 한 번 재생한다 — 본인에게만 들려야 하는 UI·확인음용.</summary>
    public void PlaySfx2D(EAudioClip id)
    {
        if (id == EAudioClip.None)
            return;

        if (!m_entries.TryGetValue(id, out AudioLibrary.Entry entry))
        {
            WarnOnce(id, $"카탈로그에 {id} 항목이 없다");
            return;
        }

        if (entry.Clip == null)
        {
            WarnOnce(id, $"{id} 항목에 클립이 배정되지 않았다");
            return;
        }

        AudioSource source = RentSource();
        if (source == null)
            return;

        source.spatialBlend = 0f;
        source.clip = entry.Clip;
        source.volume = SfxVolumeOf(entry);
        ApplyStartOffset(source, entry);
        source.Play();
    }

    /// <summary>채널링처럼 지속되는 2D 루프음을 건다. 같은 소리가 이미 돌고 있으면 유지한다.</summary>
    public void PlayLoop2D(EAudioClip id)
    {
        if (id == EAudioClip.None)
        {
            StopLoop2D();
            return;
        }

        if (m_loopId == id)
            return;

        if (!m_entries.TryGetValue(id, out AudioLibrary.Entry entry))
        {
            WarnOnce(id, $"카탈로그에 {id} 항목이 없다");
            return;
        }

        if (entry.Clip == null)
        {
            StopLoop2D();
            return;
        }

        if (m_loopSource == null)
            return;

        m_loopSource.clip = entry.Clip;
        m_loopSource.volume = SfxVolumeOf(entry);
        m_loopSource.Play();
        m_loopId = id;
    }

    /// <summary>2D 루프를 끊는다 — 채널링이 완료·취소·중단 중 무엇으로 끝나도 불러야 한다.</summary>
    public void StopLoop2D()
    {
        if (m_loopId == EAudioClip.None)
            return;

        if (m_loopSource != null)
        {
            m_loopSource.Stop();
            m_loopSource.clip = null;
        }

        m_loopId = EAudioClip.None;
    }

    /// <summary>비·눈 같은 환경음 2D 루프를 건다. 채널링 루프와 슬롯이 분리돼 있다.</summary>
    public void PlayAmbient2D(EAudioClip id)
    {
        if (id == EAudioClip.None)
        {
            StopAmbient2D();
            return;
        }

        if (m_ambientId == id)
            return;

        if (!m_entries.TryGetValue(id, out AudioLibrary.Entry entry))
        {
            WarnOnce(id, $"카탈로그에 {id} 항목이 없다");
            return;
        }

        if (entry.Clip == null)
        {
            WarnOnce(id, $"{id} 항목에 클립이 배정되지 않았다");
            StopAmbient2D();
            return;
        }

        if (m_ambientSource == null)
            return;

        m_ambientSource.clip = entry.Clip;
        m_ambientSource.volume = SfxVolumeOf(entry);
        m_ambientSource.Play();
        m_ambientId = id;
    }

    /// <summary>환경음 루프를 끊는다 — 날씨가 그치거나 뷰가 사라질 때 부른다.</summary>
    public void StopAmbient2D(EAudioClip id = EAudioClip.None)
    {
        if (m_ambientId == EAudioClip.None || (id != EAudioClip.None && m_ambientId != id))
            return;

        if (m_ambientSource != null)
        {
            m_ambientSource.Stop();
            m_ambientSource.clip = null;
        }

        m_ambientId = EAudioClip.None;
    }

    /// <summary>카탈로그 항목을 돌려준다(없으면 null) — 자체 AudioSource로 루프를 틀 때 쓴다.</summary>
    public AudioLibrary.Entry GetSfxEntry(EAudioClip id) =>
        m_entries.TryGetValue(id, out AudioLibrary.Entry entry) ? entry : null;

    private static void ApplyStartOffset(AudioSource source, AudioLibrary.Entry entry)
    {
        source.time = entry.StartOffset <= 0f
            ? 0f
            : Mathf.Min(entry.StartOffset, Mathf.Max(0f, entry.Clip.length - 0.05f));
    }

    private void BuildIndex()
    {
        if (m_library == null)
        {
            Debug.LogWarning($"[SoundManager] 오디오 카탈로그가 배정되지 않았다 — 모든 소리가 나지 않는다. {name}에 지정할 것", this);
            return;
        }

        foreach (AudioLibrary.Entry entry in m_library.Entries)
        {
            if (entry == null || entry.Id == EAudioClip.None)
                continue;

            if (!m_entries.TryAdd(entry.Id, entry))
                Debug.LogError($"[SoundManager] 카탈로그에 {entry.Id}가 중복 등록됐다 — 먼저 오는 항목만 쓰인다", m_library);
        }
    }

    private void BuildSources()
    {
        m_sources = new AudioSource[m_sourceCount];
        m_startTimes = new float[m_sourceCount];

        for (int i = 0; i < m_sourceCount; i++)
        {
            var host = new GameObject($"SfxSource_{i}");
            host.transform.SetParent(transform, false);

            AudioSource source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            m_sources[i] = source;
        }

        BuildLoopSource();
    }

    private void BuildLoopSource()
    {
        m_loopSource = BuildLoopSource("Loop2DSource");
        m_ambientSource = BuildLoopSource("Ambient2DSource");
    }

    private AudioSource BuildLoopSource(string label)
    {
        var host = new GameObject(label);
        host.transform.SetParent(transform, false);

        AudioSource source = host.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;
        source.spatialBlend = 0f;
        return source;
    }

    private AudioSource RentSource()
    {
        if (m_sources == null || m_sources.Length == 0)
            return null;

        int oldest = 0;
        for (int i = 0; i < m_sources.Length; i++)
        {
            if (!m_sources[i].isPlaying)
            {
                m_startTimes[i] = Time.time;
                return m_sources[i];
            }

            if (m_startTimes[i] < m_startTimes[oldest])
                oldest = i;
        }

        m_startTimes[oldest] = Time.time;
        return m_sources[oldest];
    }

    private void WarnOnce(EAudioClip id, string reason)
    {
        if (m_warned.Add(id))
            Debug.LogWarning($"[SoundManager] {reason} — 해당 소리를 건너뛴다", this);
    }
}
