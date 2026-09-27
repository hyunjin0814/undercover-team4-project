using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 켜져 있는 동안 몸 여기저기에서 전기 아크를 간헐적으로 튀기는 로컬 연출(NPC·플레이어 공용).
/// </summary>
public class ShockArcEmitter : MonoBehaviour
{
    [Tooltip("전기 아크 파티클 프리팹. 비우면 아무것도 나오지 않는다(연출은 선택 사항)")]
    [SerializeField] private GameObject m_arcPrefab;

    [Tooltip("아크 하나가 살아 있는 시간(초) — 프리팹의 파티클 수명 + 트레일 수명보다 길게 잡을 것")]
    [SerializeField] private float m_arcLifetime = 0.45f;

    [Tooltip("아크가 튀는 간격의 최솟값(초). 짧을수록 급박해 보인다")]
    [SerializeField] private float m_minInterval = 0.12f;

    [SerializeField] private float m_maxInterval = 0.26f;

    [Tooltip("잦아드는 구간에서 간격에 곱하는 배수 — 클수록 성겨진다")]
    [SerializeField] private float m_calmIntervalScale = 2.5f;

    public const float k_calmTailSeconds = 1.2f;

    private readonly List<Renderer> m_bodyRenderers = new List<Renderer>();

    private readonly List<Renderer> m_visibleRenderers = new List<Renderer>();

    private bool m_emitting;
    private bool m_calm;
    private float m_nextBurstTime;

    private void Awake()
    {
        foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
        {
            if (renderer is ParticleSystemRenderer || renderer is TrailRenderer)
                continue;
            m_bodyRenderers.Add(renderer);
        }
    }

    /// <summary>방출 온/오프. 켜는 순간 첫 아크가 즉시 나간다 — 맞은 티가 나야 하므로 간격을 기다리지 않는다.</summary>
    public void SetEmitting(bool emitting)
    {
        if (emitting == m_emitting)
            return;

        m_emitting = emitting;
        if (!emitting)
            return;

        RefreshVisibleRenderers();
        m_nextBurstTime = Time.time;
    }

    /// <summary>잦아들기 시작 — 간격만 벌어지고 멈추지는 않는다. 완전히 끊으면 이미 깨어난 것처럼 보인다.</summary>
    public void SetCalm(bool calm) => m_calm = calm;

    private void Update()
    {
        if (!m_emitting || m_arcPrefab == null || Time.time < m_nextBurstTime)
            return;

        GameObject arc = Instantiate(m_arcPrefab, RandomBodyPoint(), Quaternion.identity, transform);
        Destroy(arc, m_arcLifetime);

        float interval = Random.Range(m_minInterval, m_maxInterval);
        m_nextBurstTime = Time.time + (m_calm ? interval * m_calmIntervalScale : interval);
    }

    /// <summary>지금 켜져 있는 렌더러만 추린다 — 방출 시작 시점에 한 번 돈다.</summary>
    private void RefreshVisibleRenderers()
    {
        m_visibleRenderers.Clear();
        for (int i = 0; i < m_bodyRenderers.Count; i++)
        {
            Renderer renderer = m_bodyRenderers[i];
            if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                m_visibleRenderers.Add(renderer);
        }
    }

    private Vector3 RandomBodyPoint()
    {
        if (m_visibleRenderers.Count == 0)
            return transform.position + Vector3.up * 0.5f;

        Bounds bounds = m_visibleRenderers[Random.Range(0, m_visibleRenderers.Count)].bounds;
        return new Vector3(
            Random.Range(bounds.min.x, bounds.max.x),
            Random.Range(bounds.min.y, bounds.max.y),
            Random.Range(bounds.min.z, bounds.max.z)
        );
    }
}
