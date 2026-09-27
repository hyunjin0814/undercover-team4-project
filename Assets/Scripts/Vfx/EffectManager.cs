using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 일회성 이펙트 로컬 재생과 오브젝트 풀링. App.Game.Effect로 접근한다.
/// 사운드와 네트워크는 다루지 않으며, 인게임 씬에만 있다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class EffectManager : CommonManagerBase
{
    [Tooltip("이펙트 카탈로그 — 비우면 모든 재생 요청이 무동작한다")]
    [SerializeField] private EffectLibrary m_library;

    private class Pooled
    {
        public GameObject Instance;
        public ParticleSystem Particles;
    }

    private struct Playing
    {
        public EEffect Id;
        public Pooled Item;
        public float ReturnTime;
    }

    private readonly Dictionary<EEffect, EffectLibrary.Entry> m_entries = new();
    private readonly Dictionary<EEffect, Stack<Pooled>> m_pools = new();
    private readonly List<Playing> m_playing = new();

    private readonly HashSet<EEffect> m_warned = new();

    protected override void Awake()
    {
        base.Awake();

        BuildIndex();
        PrewarmAll();
    }

    /// <summary>이펙트를 한 번 재생한다. 어느 피어에서 불러도 그 피어에서만 보인다(로컬 연출).</summary>
    public void Play(EEffect id, Vector3 position, Vector3 normal)
    {
        if (id == EEffect.None)
            return;

        if (!m_entries.TryGetValue(id, out EffectLibrary.Entry entry))
        {
            WarnOnce(id, $"카탈로그에 {id} 항목이 없다");
            return;
        }

        if (entry.Prefab == null)
        {
            WarnOnce(id, $"{id} 항목에 프리팹이 배정되지 않았다");
            return;
        }

        Pooled item = Rent(id, entry.Prefab);
        item.Instance.transform.SetPositionAndRotation(position, RotationFor(normal));
        item.Instance.SetActive(true);

        if (item.Particles != null)
        {
            item.Particles.Clear(true);
            item.Particles.Play(true);
        }

        m_playing.Add(new Playing
        {
            Id = id,
            Item = item,
            ReturnTime = Time.time + entry.LifetimeSeconds,
        });
    }

    private void Update()
    {
        float now = Time.time;

        for (int i = m_playing.Count - 1; i >= 0; i--)
        {
            if (now < m_playing[i].ReturnTime)
                continue;

            Return(m_playing[i]);
            m_playing.RemoveAt(i);
        }
    }

    protected override void OnDestroy()
    {
        m_playing.Clear();
        m_pools.Clear();
        base.OnDestroy();
    }

    private void BuildIndex()
    {
        if (m_library == null)
        {
            Debug.LogWarning($"[EffectManager] 이펙트 카탈로그가 배정되지 않았다 — 모든 연출이 나오지 않는다. {name}에 지정할 것", this);
            return;
        }

        foreach (EffectLibrary.Entry entry in m_library.Entries)
        {
            if (entry == null || entry.Id == EEffect.None)
                continue;

            if (!m_entries.TryAdd(entry.Id, entry))
                Debug.LogError($"[EffectManager] 카탈로그에 {entry.Id}가 중복 등록됐다 — 먼저 오는 항목만 쓰인다", m_library);
        }
    }

    private void PrewarmAll()
    {
        foreach (EffectLibrary.Entry entry in m_entries.Values)
        {
            if (entry.Prefab == null || entry.Prewarm <= 0)
                continue;

            Stack<Pooled> pool = PoolFor(entry.Id);
            for (int i = 0; i < entry.Prewarm; i++)
                pool.Push(Create(entry.Prefab));
        }
    }

    private Pooled Rent(EEffect id, GameObject prefab)
    {
        Stack<Pooled> pool = PoolFor(id);

        while (pool.Count > 0)
        {
            Pooled item = pool.Pop();
            if (item.Instance != null)
                return item;
        }

        return Create(prefab);
    }

    private void Return(Playing playing)
    {
        if (playing.Item.Instance == null)
            return;

        playing.Item.Instance.SetActive(false);
        PoolFor(playing.Id).Push(playing.Item);
    }

    private Stack<Pooled> PoolFor(EEffect id)
    {
        if (!m_pools.TryGetValue(id, out Stack<Pooled> pool))
        {
            pool = new Stack<Pooled>();
            m_pools[id] = pool;
        }

        return pool;
    }

    private Pooled Create(GameObject prefab)
    {
        GameObject instance = Instantiate(prefab, transform);
        instance.SetActive(false);

        return new Pooled
        {
            Instance = instance,
            Particles = instance.GetComponentInChildren<ParticleSystem>(true),
        };
    }

    private static Quaternion RotationFor(Vector3 normal) =>
        normal.sqrMagnitude < 0.0001f ? Quaternion.identity : Quaternion.LookRotation(normal);

    private void WarnOnce(EEffect id, string reason)
    {
        if (m_warned.Add(id))
            Debug.LogWarning($"[EffectManager] {reason} — 해당 연출을 건너뛴다", this);
    }
}
