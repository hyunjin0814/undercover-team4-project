using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 미니맵에 표시할 대상에 붙이는 마커 — 활성화되면 레지스트리에 등록되고 아이콘·범위 표시 설정을 가진다.
/// </summary>
public class MinimapTarget : MonoBehaviour
{
    public static readonly List<MinimapTarget> ActiveTargets = new();

    [Tooltip("휴대용 미니맵의 카테고리 필터가 이 값으로 대상을 거른다 (#835)")]
    [SerializeField] private EMinimapMarker m_category = EMinimapMarker.None;

    [SerializeField] private Sprite m_iconSprite;
    [SerializeField] private Color m_iconColor = Color.blue;

    [Tooltip("아이콘 한 변의 크기(px). 0이면 아이콘 프리팹 크기를 그대로 쓴다")]
    [Min(0f)]
    [SerializeField] private float m_iconSize = 0f;

    [Tooltip("아이콘 회전(도, 반시계). 대상의 월드 회전과 무관한 고정 각도")]
    [SerializeField] private float m_iconAngle = 0f;

    [Tooltip("켜면 대상이 바라보는 방향으로 아이콘이 돌아간다 — 위 각도는 스프라이트가 위를 보게 맞추는 보정으로 쓰인다")]
    [SerializeField] private bool m_iconFollowsFacing = false;

    [Header("범위 오버레이 (#610)")]
    [Tooltip("이 대상이 덮는 월드 반경(m). 0이면 점만 찍는다 — 폭발 반경처럼 '얼마나 넓게'를 알려야 할 때만 채운다")]
    [Min(0f)]
    [SerializeField] private float m_areaRadius = 0f;

    [Tooltip("범위 오버레이 색 — 알파를 낮게 둬야 밑의 맵이 비친다")]
    [SerializeField] private Color m_areaColor = new Color(1f, 0.25f, 0.25f, 0.25f);

    public EMinimapMarker Category => m_category;

    public Sprite IconSprite => m_iconSprite;

    public Color IconColor
    {
        get => m_iconColor;
        set => m_iconColor = value;
    }

    public string IconLabel { get; set; }

    public float IconSize
    {
        get => m_iconSize;
        set => m_iconSize = value;
    }

    public float IconAngle
    {
        get => m_iconAngle;
        set => m_iconAngle = value;
    }

    public bool IconFollowsFacing => m_iconFollowsFacing;

    public float AreaRadius
    {
        get => m_areaRadius;
        set => m_areaRadius = value;
    }

    public Color AreaColor
    {
        get => m_areaColor;
        set => m_areaColor = value;
    }

    private NetworkObject m_networkObject;

    public bool IsLocalPlayer =>
        m_networkObject == null || !m_networkObject.IsSpawned || m_networkObject.IsOwner;

    private void OnEnable()
    {
        m_networkObject = GetComponentInParent<NetworkObject>();
        ActiveTargets.Add(this);
    }

    private void OnDisable() => ActiveTargets.Remove(this);
}
