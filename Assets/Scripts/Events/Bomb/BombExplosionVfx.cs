using UnityEngine;

/// <summary>
/// 폭발 지점에 독립 스폰되는 일회용 이펙트 — 점광원 섬광을 감쇠시키고 끝나면 스스로 정리한다.
/// </summary>
public class BombExplosionVfx : MonoBehaviour
{
    [Tooltip("폭발 순간의 점광원 — 비우면 자식에서 찾는다")]
    [SerializeField]
    private Light m_flash;

    [Tooltip("섬광 최대 밝기")]
    [SerializeField]
    private float m_flashIntensity = 40f;

    [Tooltip("섬광이 0까지 잦아드는 시간(초)")]
    [SerializeField]
    private float m_flashSeconds = 0.4f;

    [Tooltip("이 시간(초) 뒤 오브젝트를 정리한다 — 가장 긴 파티클 수명보다 길게 잡을 것")]
    [SerializeField]
    private float m_lifetimeSeconds = 5f;

    private float m_elapsed;

    private void Awake()
    {
        if (m_flash == null)
            m_flash = GetComponentInChildren<Light>(true);

        if (m_flash != null)
            m_flash.intensity = m_flashIntensity;
    }

    private void Update()
    {
        m_elapsed += Time.deltaTime;

        if (m_flash != null && m_flashSeconds > 0f)
        {
            float t = Mathf.Clamp01(m_elapsed / m_flashSeconds);
            float falloff = (1f - t) * (1f - t);
            m_flash.intensity = m_flashIntensity * falloff;
            if (t >= 1f)
                m_flash.enabled = false;
        }

        if (m_elapsed >= m_lifetimeSeconds)
            Destroy(gameObject);
    }
}
