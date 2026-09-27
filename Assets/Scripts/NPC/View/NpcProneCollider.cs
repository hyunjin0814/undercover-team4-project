using UnityEngine;

/// <summary>
/// NPC가 누운 자세일 때 몸통 캡슐 콜라이더도 함께 눕혀 조준 판정을 보이는 몸에 맞춘다.
/// NpcAnimationDriver.IsProne을 따라가며 모든 피어에서 같은 순간에 바뀐다.
/// </summary>
[RequireComponent(typeof(CapsuleCollider))]
[RequireComponent(typeof(NpcAnimationDriver))]
public class NpcProneCollider : MonoBehaviour
{
    private const int k_proneDirection = 2;

    [Header("누운 자세 캡슐 (#363 — 기본값은 기절 모션 실측)")]
    [Tooltip("누웠을 때 캡슐 반지름(m) — 바닥에 닿게 중심 y와 같은 값을 쓴다")]
    [SerializeField]
    private float m_proneRadius = 0.3f;

    [Tooltip("누웠을 때 캡슐 길이(m) — 머리끝~발끝. 실측 1.68m에 머리·발끝 여유를 조금 더한 값")]
    [SerializeField]
    private float m_proneHeight = 1.8f;

    [Tooltip("누웠을 때 캡슐 중심(로컬) — 머리가 -Z라 몸 중심이 뒤로 밀린다")]
    [SerializeField]
    private Vector3 m_proneCenter = new Vector3(0f, 0.3f, -0.1f);

    private CapsuleCollider m_capsule;
    private NpcAnimationDriver m_driver;

    private float m_standRadius;
    private float m_standHeight;
    private int m_standDirection;
    private Vector3 m_standCenter;

    private void Awake()
    {
        m_capsule = GetComponent<CapsuleCollider>();
        m_driver = GetComponent<NpcAnimationDriver>();

        m_standRadius = m_capsule.radius;
        m_standHeight = m_capsule.height;
        m_standDirection = m_capsule.direction;
        m_standCenter = m_capsule.center;
    }

    private void OnEnable()
    {
        m_driver.OnProneChanged += Apply;
        Apply(m_driver.IsProne);
    }

    private void OnDisable()
    {
        m_driver.OnProneChanged -= Apply;
    }

    private void Apply(bool prone)
    {
        m_capsule.radius = prone ? m_proneRadius : m_standRadius;
        m_capsule.height = prone ? m_proneHeight : m_standHeight;
        m_capsule.direction = prone ? k_proneDirection : m_standDirection;
        m_capsule.center = prone ? m_proneCenter : m_standCenter;
    }
}
