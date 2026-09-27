using UnityEngine;

/// <summary>
/// 제보 전화 울림 연출(벨소리·수화기 떨림)을 IsRinging 상태에 맞춰 로컬로 재생한다.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class TipCallPhoneView : MonoBehaviour
{
    [SerializeField] private TipCallPhone m_phone;
    [SerializeField] private AudioSource m_source;

    [Header("수화기 떨림")]
    [Tooltip("울릴 때 덜그럭거릴 부품 — 보통 수화기(Handle). 비우면 소리만 나고 움직임은 없다")]
    [SerializeField] private Transform m_handle;

    [Tooltip("좌우로 기우는 최대 각도(도)")]
    [SerializeField] private float m_shakeAngle = 6f;

    [Tooltip("떨림 속도 — 벨소리 리듬과 맞출 값")]
    [SerializeField] private float m_shakeSpeed = 28f;

    [Tooltip("들썩이는 높이(m)")]
    [SerializeField] private float m_shakeHeight = 0.012f;

    private Vector3 m_handleBasePosition;
    private Quaternion m_handleBaseRotation;

    private void Awake()
    {
        if (m_source == null)
            m_source = GetComponent<AudioSource>();

        if (m_handle != null)
        {
            m_handleBasePosition = m_handle.localPosition;
            m_handleBaseRotation = m_handle.localRotation;
        }
    }

    private void OnEnable()
    {
        if (m_phone != null)
            m_phone.OnRingingChanged += Refresh;

        Refresh();
    }

    private void OnDisable()
    {
        if (m_phone != null)
            m_phone.OnRingingChanged -= Refresh;

        RestoreHandle();
    }

    private void Refresh()
    {
        if (m_source == null)
            return;

        bool ringing = m_phone != null && m_phone.IsRinging;

        if (ringing && !m_source.isPlaying)
            m_source.Play();
        else if (!ringing && m_source.isPlaying)
            m_source.Stop();

        if (!ringing)
            RestoreHandle();
    }

    private void Update()
    {
        if (m_phone == null || !m_phone.IsRinging)
            return;

        if (m_handle == null)
            return;

        float t = Time.time * m_shakeSpeed;
        m_handle.localRotation = m_handleBaseRotation * Quaternion.Euler(0f, 0f, Mathf.Sin(t) * m_shakeAngle);
        m_handle.localPosition = m_handleBasePosition + new Vector3(0f, Mathf.Abs(Mathf.Sin(t)) * m_shakeHeight, 0f);
    }

    private void RestoreHandle()
    {
        if (m_handle == null)
            return;

        m_handle.localPosition = m_handleBasePosition;
        m_handle.localRotation = m_handleBaseRotation;
    }
}
