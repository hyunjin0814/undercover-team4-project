using UnityEngine;

/// <summary>
/// 폭탄이 실제로 움직인 거리만큼 바퀴를 굴리는 로컬 연출.
/// </summary>
public class BombWheelSpin : MonoBehaviour
{
    [Tooltip("굴릴 바퀴 축들 — 각자의 로컬 X축을 중심으로 돈다 (Synty 로봇의 Wheel_F/M/B)")]
    [SerializeField]
    private Transform[] m_wheels;

    [Tooltip("바퀴 반지름(m) — 이동 거리를 회전각으로 바꾸는 기준. 작을수록 빨리 돈다")]
    [SerializeField]
    private float m_wheelRadius = 0.17f;

    private Vector3 m_lastPosition;

    private void OnEnable()
    {
        m_lastPosition = transform.position;
    }

    private void LateUpdate()
    {
        if (m_wheels == null || m_wheels.Length == 0 || m_wheelRadius <= 0.001f)
            return;

        Vector3 delta = transform.position - m_lastPosition;
        m_lastPosition = transform.position;

        float travelled = Vector3.Dot(delta, transform.forward);
        if (Mathf.Abs(travelled) < 0.0001f)
            return;

        float degrees = travelled / (2f * Mathf.PI * m_wheelRadius) * 360f;
        for (int i = 0; i < m_wheels.Length; i++)
        {
            if (m_wheels[i] != null)
                m_wheels[i].Rotate(Vector3.right, degrees, Space.Self);
        }
    }
}
