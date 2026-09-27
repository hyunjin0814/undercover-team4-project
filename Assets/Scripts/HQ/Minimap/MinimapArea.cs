using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 맵 씬의 미니맵 월드 영역과 항공뷰 스프라이트의 단일 출처. 본부·휴대용 MinimapViewer가 함께 읽는다.
/// </summary>
public class MinimapArea : MonoBehaviour
{
    public static MinimapArea Current { get; private set; }

    [Header("맵이 덮는 월드 영역")]
    [SerializeField] private float m_worldCenterX;
    [SerializeField] private float m_worldCenterZ;
    [SerializeField] private float m_worldSizeX = 100f;
    [SerializeField] private float m_worldSizeZ = 100f;

    [SerializeField] private Sprite m_aerialSprite;

    public float WorldCenterX => m_worldCenterX;
    public float WorldCenterZ => m_worldCenterZ;
    public float WorldSizeX => m_worldSizeX;
    public float WorldSizeZ => m_worldSizeZ;
    public Sprite AerialSprite => m_aerialSprite;

    private void OnEnable()
    {
        if (Current != null && Current != this)
            Debug.LogWarning($"[미니맵 영역] 씬에 MinimapArea가 둘 이상 있다 — {name}이 이긴다", this);

        Current = this;
    }

    private void OnDisable()
    {
        if (Current == this)
            Current = null;
    }

    public void ApplyTo(Image mapImage, out float centerX, out float centerZ, out float sizeX, out float sizeZ)
    {
        centerX = m_worldCenterX;
        centerZ = m_worldCenterZ;
        sizeX = m_worldSizeX;
        sizeZ = m_worldSizeZ;

        if (mapImage != null && m_aerialSprite != null)
            mapImage.sprite = m_aerialSprite;
    }
}
