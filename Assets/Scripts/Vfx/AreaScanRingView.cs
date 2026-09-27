using System.Collections;
using UnityEngine;

/// <summary>
/// 구역 스캔 결과를 사용 지점에서 퍼지는 초록/빨강 LineRenderer 링으로 보여 주는 로컬 연출.
/// 반경·색은 AreaScanner의 판정 값을 그대로 받는다.
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class AreaScanRingView : MonoBehaviour
{
    [Tooltip("링 두께(m)")]
    [SerializeField]
    private float m_lineWidth = 0.15f;

    [Tooltip("세그먼트 수 — 원의 매끄러움")]
    [Range(8, 128)]
    [SerializeField]
    private int m_segments = 64;

    [Tooltip("0 → 반경까지 퍼지는 데 걸리는 시간(초) — '서서히 퍼지는' 속도")]
    [SerializeField]
    private float m_expandSeconds = 1.2f;

    [Tooltip("퍼짐 진행 곡선 — 기본은 초반 빠르게 시작해 끝에서 감속한다")]
    [SerializeField]
    private AnimationCurve m_expandEase = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Tooltip("다 퍼진 뒤 완전히 사라지기까지 걸리는 시간(초)")]
    [SerializeField]
    private float m_fadeSeconds = 0.6f;

    [Tooltip("바닥에 파묻히지 않게 링을 띄우는 높이(m)")]
    [SerializeField]
    private float m_groundOffset = 0.05f;

    private LineRenderer m_line;

    private void Awake()
    {
        m_line = GetComponent<LineRenderer>();
        m_line.useWorldSpace = true;
        m_line.loop = true;
        m_line.numCapVertices = 0;
        m_line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        m_line.receiveShadows = false;
    }

    /// <summary>링 재생을 시작한다 — <see cref="AreaScanner"/>가 인스턴스화 직후 즉시 호출한다.</summary>
    public void Play(Vector3 center, float radius, Color color)
    {
        m_line.widthMultiplier = m_lineWidth;
        m_line.startColor = color;
        m_line.endColor = color;

        StartCoroutine(PlayRoutine(center + Vector3.up * m_groundOffset, radius, color));
    }

    private IEnumerator PlayRoutine(Vector3 center, float radius, Color color)
    {
        float elapsed = 0f;
        while (elapsed < m_expandSeconds)
        {
            elapsed += Time.deltaTime;
            float progress = m_expandEase.Evaluate(Mathf.Clamp01(elapsed / m_expandSeconds));
            DrawCircle(center, radius * progress);
            yield return null;
        }

        DrawCircle(center, radius);

        float fadeElapsed = 0f;
        while (fadeElapsed < m_fadeSeconds)
        {
            fadeElapsed += Time.deltaTime;
            Color faded = color;
            faded.a = color.a * (1f - Mathf.Clamp01(fadeElapsed / m_fadeSeconds));
            m_line.startColor = faded;
            m_line.endColor = faded;
            yield return null;
        }

        Destroy(gameObject);
    }

    private void DrawCircle(Vector3 center, float radius)
    {
        if (m_line.positionCount != m_segments)
            m_line.positionCount = m_segments;

        for (int i = 0; i < m_segments; i++)
        {
            float angle = i * Mathf.PI * 2f / m_segments;
            Vector3 point = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            m_line.SetPosition(i, point);
        }
    }
}
