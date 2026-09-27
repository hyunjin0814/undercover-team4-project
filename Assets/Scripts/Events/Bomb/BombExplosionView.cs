using UnityEngine;

/// <summary>
/// BombDevice.OnExploded를 구독해 각 피어가 폭심에 이펙트를 띄우고 폭발음을 낸다.
/// </summary>
public class BombExplosionView : MonoBehaviour
{
    [Tooltip("폭발 지점에 스폰할 이펙트 — 비워두면 소리만 난다")]
    [SerializeField]
    private BombExplosionVfx m_explosionVfx;

    [Tooltip("폭발음 — 카탈로그에 클립이 없으면 조용히 무음")]
    [SerializeField]
    private EAudioClip m_explosionSound = EAudioClip.BombExplosion;

    private BombDevice m_device;

    private void OnEnable()
    {
        m_device = GetComponentInParent<BombDevice>();
        if (m_device != null)
            m_device.OnExploded += HandleExploded;
    }

    private void OnDisable()
    {
        if (m_device != null)
            m_device.OnExploded -= HandleExploded;
    }

    private void HandleExploded()
    {
        if (m_device == null)
            return;

        SpawnVfx();

        App.Sound?.PlaySfxAt(m_explosionSound, m_device.transform.position);
    }

    private void SpawnVfx()
    {
        if (m_explosionVfx == null)
            return;

        Instantiate(m_explosionVfx, transform.position, Quaternion.identity);
    }
}
