using UnityEngine;

/// <summary>
/// [테스트용] 대상 머리 위에 카메라를 향하는 월드 텍스트를 띄운다. 데모 빌드 전 제거할 것.
/// </summary>
public class TestWorldLabel : MonoBehaviour
{
    [Header("표시 텍스트")]
    [SerializeField] private string m_text = "LABEL";

    [SerializeField] private Color m_color = Color.yellow;

    [Header("대상 피벗 기준 높이(m)")]
    [SerializeField] private float m_heightOffset = 2.5f;

    [Header("글자 크기")]
    [SerializeField] private float m_characterSize = 0.2f;
    [SerializeField] private int m_fontSize = 64;

    private TextMesh m_textMesh;
    private Transform m_cam;

    private void Start()
    {
        BuildLabel();
    }

    public void Configure(string text, Color color)
    {
        m_text = text;
        m_color = color;
        if (m_textMesh != null)
        {
            m_textMesh.text = text;
            m_textMesh.color = color;
        }
    }

    private void BuildLabel()
    {
        var go = new GameObject("TestWorldLabel_Text");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0f, m_heightOffset, 0f);

        m_textMesh = go.AddComponent<TextMesh>();
        m_textMesh.text = m_text;
        m_textMesh.color = m_color;
        m_textMesh.anchor = TextAnchor.LowerCenter;
        m_textMesh.alignment = TextAlignment.Center;
        m_textMesh.characterSize = m_characterSize;
        m_textMesh.fontSize = m_fontSize;

        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font != null)
        {
            m_textMesh.font = font;
            go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        }
    }

    private void LateUpdate()
    {
        if (m_textMesh == null)
            return;

        if (m_cam == null && Camera.main != null)
            m_cam = Camera.main.transform;
        if (m_cam == null)
            return;

        m_textMesh.transform.rotation = m_cam.rotation;
    }
}
