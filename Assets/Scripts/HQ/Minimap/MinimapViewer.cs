using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 월드 영역을 UI 좌표로 환산해 미니맵 위에 대상 아이콘과 범위 오버레이를 그린다.
/// 먹통 중에는 지도를 가린다.
/// </summary>
public class MinimapViewer : MonoBehaviour
{
    [Header("맵이 덮는 월드 영역")]
    [SerializeField] private float m_worldCenterX = 0f;
    [SerializeField] private float m_worldCenterZ = 0f;
    [SerializeField] private float m_worldSizeX = 100f;
    [SerializeField] private float m_worldSizeZ = 100f;

    [Header("UI 참조")]
    [SerializeField] private RectTransform m_mapRect;
    [SerializeField] private RectTransform m_iconContainer;
    [SerializeField] private Image m_iconPrefab;

    [Header("범위 오버레이 (#610)")]
    [Tooltip("이벤트 범위를 그릴 반투명 원 Image. 비우면 범위는 그리지 않는다 — 점 아이콘만 찍힌다")]
    [SerializeField] private Image m_areaPrefab;

    [Tooltip("범위 오버레이 부모. 비우면 아이콘 부모를 쓰되 맨 뒤로 보내 아이콘에 깔린다")]
    [SerializeField] private RectTransform m_areaContainer;

    [Header("먹통 차단 (#762)")]
    [Tooltip("먹통 중 지도를 덮는 판. 비우면 런타임에 검은 판을 만든다 — 프리팹 배선을 잊어도 동작한다")]
    [SerializeField] private Image m_blackoutCover;

    [Header("맵 씬 연동 (#835)")]
    [Tooltip("켜면 씬의 MinimapArea.Current에서 월드 사각형·항공뷰를 읽는다 — 위 '맵이 덮는 월드 영역' 값을 무시한다")]
    [SerializeField] private bool m_useSceneArea;

    [Tooltip("캔버스가 회전돼 있으면 그 각도만큼 모든 아이콘 회전을 보정한다(도)")]
    [SerializeField] private float m_iconAngleCorrection;

    [Header("카테고리 필터 (#835)")]
    [Tooltip("끄면 지금처럼 모든 MinimapTarget을 표시한다")]
    [SerializeField] private bool m_filterByCategory;

    [SerializeField] private EMinimapMarker m_visibleCategories = ~EMinimapMarker.None;

    [Tooltip("Player 카테고리 중 로컬 플레이어가 아닌 대상도 보일지")]
    [SerializeField] private bool m_showOtherPlayers = true;

    [Header("내 위치 추종 크롭 (#835)")]
    [Tooltip("켜면 로컬 플레이어를 중심에 두고 그 주변 m_viewRadius만 잘라 보여준다. m_mapRect는 이 프레임의 중앙 고정 자식이어야 한다(스트레치 앵커 금지)")]
    [SerializeField] private bool m_followsLocalPlayer;

    [Tooltip("정사각형 마스크 프레임 — 이 크기 기준으로 지도를 확대한다")]
    [SerializeField] private RectTransform m_viewFrame;

    [Tooltip("추종 시 보이는 반경(m) — 정사각형 한 변은 이 값의 2배에 해당한다")]
    [SerializeField] private float m_viewRadius = 30f;

    private DeviceBlackoutEvent m_blackout;
    private bool m_covered;
    private bool m_sceneAreaApplied;
    private Vector2 m_iconPrefabSize;

    private readonly Dictionary<MinimapTarget, Image> m_targetIcons = new();
    private readonly Dictionary<MinimapTarget, Image> m_targetAreas = new();

    private readonly Dictionary<MinimapTarget, TMP_Text> m_targetLabels = new();
    private readonly List<MinimapTarget> m_removeBuffer = new();

    private RectTransform AreaParent => m_areaContainer != null ? m_areaContainer : m_iconContainer;

    private void Awake()
    {
        if (m_blackoutCover != null)
            m_blackoutCover.enabled = false;

        if (m_iconPrefab != null)
            m_iconPrefabSize = m_iconPrefab.rectTransform.sizeDelta;
    }

    private void LateUpdate()
    {
        ApplySceneAreaOnce();
        ApplyBlackout(IsBlackout());

        if (m_covered)
            return;

        SyncIcons();
        ApplyFollowCrop();
        UpdatePositions();
    }

    private void ApplySceneAreaOnce()
    {
        if (m_sceneAreaApplied || !m_useSceneArea)
            return;

        MinimapArea area = MinimapArea.Current;
        if (area == null)
            return;

        Image mapImage = m_mapRect != null ? m_mapRect.GetComponent<Image>() : null;
        area.ApplyTo(mapImage, out m_worldCenterX, out m_worldCenterZ, out m_worldSizeX, out m_worldSizeZ);
        m_sceneAreaApplied = true;
    }

    private bool IsVisible(MinimapTarget target)
    {
        if (!m_filterByCategory)
            return true;

        if ((target.Category & m_visibleCategories) == EMinimapMarker.None)
            return false;

        if (!m_showOtherPlayers && target.Category.HasFlag(EMinimapMarker.Player) && !target.IsLocalPlayer)
            return false;

        return true;
    }

    private void ApplyFollowCrop()
    {
        if (!m_followsLocalPlayer || m_viewFrame == null || m_mapRect == null)
            return;

        MinimapTarget local = FindLocalPlayer();
        if (local == null)
            return;

        float frameSide = Mathf.Min(m_viewFrame.rect.width, m_viewFrame.rect.height);
        float pixelsPerMeter = frameSide / (2f * m_viewRadius);

        m_mapRect.sizeDelta = new Vector2(m_worldSizeX * pixelsPerMeter, m_worldSizeZ * pixelsPerMeter);
        m_mapRect.anchoredPosition = -WorldToMap(local.transform.position);
    }

    private MinimapTarget FindLocalPlayer()
    {
        foreach (MinimapTarget target in MinimapTarget.ActiveTargets)
        {
            if (target != null && target.Category.HasFlag(EMinimapMarker.Player) && target.IsLocalPlayer)
                return target;
        }

        return null;
    }

    private bool IsBlackout()
    {
        DeviceBlackoutEvent blackout = ResolveBlackout();
        return blackout != null && blackout.IsCommsBlackout;
    }

    private DeviceBlackoutEvent ResolveBlackout()
    {
        if (m_blackout != null)
            return m_blackout;

        SuddenEventManager manager = App.Game.SuddenEvent;
        m_blackout = manager != null ? manager.GetEvent<DeviceBlackoutEvent>() : null;
        return m_blackout;
    }

    private void ApplyBlackout(bool blackout)
    {
        if (blackout == m_covered)
            return;

        m_covered = blackout;

        Image cover = EnsureCover();
        if (cover == null)
            return;

        if (blackout)
            cover.rectTransform.SetAsLastSibling();

        cover.enabled = blackout;
    }

    private Image EnsureCover()
    {
        if (m_blackoutCover != null)
            return m_blackoutCover;

        if (m_mapRect == null)
        {
            enabled = false;
            Debug.LogWarning($"MinimapViewer: m_mapRect가 없어 먹통 차단을 만들 수 없다. {name} 프리팹에 지정할 것", this);
            return null;
        }

        var built = new GameObject("MinimapBlackoutCover", typeof(RectTransform), typeof(Image));
        RectTransform rect = built.GetComponent<RectTransform>();
        rect.SetParent(m_mapRect, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        m_blackoutCover = built.GetComponent<Image>();
        m_blackoutCover.color = Color.black;
        m_blackoutCover.raycastTarget = false;
        m_blackoutCover.enabled = false;
        return m_blackoutCover;
    }

    private void SyncIcons()
    {
        m_removeBuffer.Clear();
        foreach (var pair in m_targetIcons)
        {
            if (pair.Key == null || !MinimapTarget.ActiveTargets.Contains(pair.Key) || !IsVisible(pair.Key))
                m_removeBuffer.Add(pair.Key);
        }
        foreach (var target in m_removeBuffer) 
        {
            if (m_targetIcons[target] != null)
                Destroy(m_targetIcons[target].gameObject);
            m_targetIcons.Remove(target);
            m_targetLabels.Remove(target);

            if (m_targetAreas.TryGetValue(target, out Image area))
            {
                if (area != null)
                    Destroy(area.gameObject);
                m_targetAreas.Remove(target);
            }
        }
        foreach (var target in MinimapTarget.ActiveTargets)
        {
            if (target == null || m_targetIcons.ContainsKey(target) || !IsVisible(target))
                continue;

            Image icon = Instantiate(m_iconPrefab, m_iconContainer);

            if (target.IconSprite != null)
                icon.sprite = target.IconSprite;

            icon.color = target.IconColor;
            m_targetIcons.Add(target, icon);
            m_targetLabels.Add(target, icon.GetComponentInChildren<TMP_Text>(true));

            if (m_areaPrefab != null && target.AreaRadius > 0f)
            {
                Image area = Instantiate(m_areaPrefab, AreaParent);
                area.rectTransform.SetAsFirstSibling();
                m_targetAreas.Add(target, area);
            }
        }
    }

    private void UpdatePositions()
    {
        foreach (var pair in m_targetIcons)
        {
            RectTransform rect = pair.Value.rectTransform;
            rect.anchoredPosition = WorldToMap(pair.Key.transform.position);
            rect.sizeDelta = IconSizeOf(pair.Key);
            rect.localRotation = Quaternion.Euler(0f, 0f, IconAngleOf(pair.Key));
            pair.Value.color = pair.Key.IconColor;

            UpdateLabel(pair.Key);
        }

        foreach (var pair in m_targetAreas)
        {
            RectTransform rect = pair.Value.rectTransform;
            rect.anchoredPosition = WorldToMap(pair.Key.transform.position);
            rect.sizeDelta = WorldRadiusToMapSize(pair.Key.AreaRadius);
            pair.Value.color = pair.Key.AreaColor;
        }
    }

    private void UpdateLabel(MinimapTarget target)
    {
        if (!m_targetLabels.TryGetValue(target, out TMP_Text label) || label == null)
            return;

        string text = target.IconLabel;
        bool show = !string.IsNullOrEmpty(text);

        if (label.gameObject.activeSelf != show)
            label.gameObject.SetActive(show);

        if (!show)
            return;

        if (label.text != text)
            label.text = text;

        RectTransform rect = label.rectTransform;
        Vector3 forward = rect.forward;
        Vector3 up = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.99f
            ? m_iconContainer.up
            : Vector3.up;

        rect.rotation = Quaternion.LookRotation(forward, up);
    }

    private Vector2 IconSizeOf(MinimapTarget target)
        => target.IconSize > 0f
            ? new Vector2(target.IconSize, target.IconSize)
            : m_iconPrefabSize;

    private float IconAngleOf(MinimapTarget target)
    {
        float facing = target.IconFollowsFacing ? -target.transform.eulerAngles.y : 0f;
        return facing + target.IconAngle + m_iconAngleCorrection;
    }

    private Vector2 WorldToMap(Vector3 worldPos)
    {
        float u = (worldPos.x - m_worldCenterX) / m_worldSizeX;
        float v = (worldPos.z - m_worldCenterZ) / m_worldSizeZ;

        Vector2 mapSize = m_mapRect.rect.size;
        return new Vector2(u * mapSize.x, v * mapSize.y);
    }

    private Vector2 WorldRadiusToMapSize(float radius)
    {
        Vector2 mapSize = m_mapRect.rect.size;
        return new Vector2(
            2f * radius / m_worldSizeX * mapSize.x,
            2f * radius / m_worldSizeZ * mapSize.y);
    }
}
