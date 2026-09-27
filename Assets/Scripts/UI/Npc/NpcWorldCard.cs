using UnityEngine;

/// <summary>
/// NPC 머리 위 월드공간 표시물의 공통 베이스 — 빌보드·누운 자세 보정·표시 토글을 맡는다.
/// 무엇을 보여줄지는 파생(ScanInfoView·NpcHealthBarView)이 정한다.
/// </summary>
public abstract class NpcWorldCard : MonoBehaviour
{
    [Header("빌보드")]
    [Tooltip("켜져 있는 동안 카메라를 향하도록 회전한다. 끄면 프리팹의 고정 방향을 유지")]
    [SerializeField]
    private bool m_billboard = true;

    [Header("누운 자세 위치")]
    [Tooltip(
        "NPC가 누워 있을 때(기절·밧줄 끌림) 위치(NPC 루트 로컬) — 머리가 -Z라 표시도 머리 쪽으로 밀린다. "
            + "서 있을 때 위치는 프리팹 값을 그대로 쓴다"
    )]
    [SerializeField]
    private Vector3 m_proneOffset = new Vector3(0.1f, 0.72f, -0.4f);

    private Transform m_cameraTransform;
    private RectTransform m_rect;

    private NpcAnimationDriver m_driver;

    private Vector3 m_standOffset;

    protected virtual void Awake()
    {
        m_rect = transform as RectTransform;
        if (m_rect != null)
            m_standOffset = m_rect.anchoredPosition3D;

        m_driver = GetComponentInParent<NpcAnimationDriver>(true);
    }

    protected virtual void OnEnable()
    {
        if (m_driver == null)
            return;

        m_driver.OnProneChanged += ApplyProne;

        ApplyProne(m_driver.IsProne);
    }

    protected virtual void OnDisable()
    {
        if (m_driver != null)
            m_driver.OnProneChanged -= ApplyProne;
    }

    /// <summary>빌보드가 바라볼 로컬 플레이어 카메라를 지정한다.</summary>
    public void SetCamera(Transform cameraTransform)
    {
        if (cameraTransform != null)
            m_cameraTransform = cameraTransform;
    }

    /// <summary>표시물을 끈다.</summary>
    public void Hide() => SetCardActive(false);

    protected void SetCardActive(bool active)
    {
        if (gameObject.activeSelf != active)
            gameObject.SetActive(active);
    }

    private void ApplyProne(bool prone)
    {
        if (m_rect == null)
            return;

        m_rect.anchoredPosition3D = prone ? m_proneOffset : m_standOffset;
    }

    protected virtual void LateUpdate()
    {
        if (!m_billboard)
            return;

        if (m_cameraTransform == null)
        {
            Camera camera = Camera.main;
            if (camera == null)
                return;

            m_cameraTransform = camera.transform;
        }

        transform.rotation = Quaternion.LookRotation(m_cameraTransform.forward, m_cameraTransform.up);
    }
}
