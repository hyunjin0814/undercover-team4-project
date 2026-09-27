using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 한 부위의 색 선택 칸들을 팔레트 길이만큼 만들고, 고르면 착용 정보에 저장한다.
/// </summary>
public class PlayerColorPickerView : MonoBehaviour
{
    [Tooltip("이 줄이 칠하는 부위")]
    [SerializeField] private EBodyPart m_part;

    [Tooltip("색 팔레트 — 로비 초상·Player 프리팹과 같은 에셋을 물릴 것")]
    [SerializeField] private PlayerColorPalette m_palette;

    [Tooltip("칸을 넣을 부모 (Layout Group)")]
    [SerializeField] private RectTransform m_container;

    [SerializeField] private PlayerColorSwatchView m_swatchPrefab;

    private readonly List<PlayerColorSwatchView> m_swatches = new List<PlayerColorSwatchView>();

    private void Awake()
    {
        if (m_palette == null || m_container == null || m_swatchPrefab == null)
        {
            Debug.LogWarning($"[{nameof(PlayerColorPickerView)}] 배선이 빠졌습니다 (#432)", this);
            enabled = false;
            return;
        }

        Build();
    }

    private void OnEnable()
    {
        CosmeticLoadout.OnPlayerColorChanged += HandleColorChanged;
        RefreshSelection();
    }

    private void OnDisable() => CosmeticLoadout.OnPlayerColorChanged -= HandleColorChanged;

    private void Build()
    {
        for (int i = 0; i < m_palette.Count; i++)
        {
            int index = i;
            PlayerColorSwatchView swatch = Instantiate(m_swatchPrefab, m_container);
            swatch.name = $"Swatch {index}";
            swatch.Bind(m_palette.Get(index), () => CosmeticLoadout.SetPlayerColor(m_part, index));
            m_swatches.Add(swatch);
        }

        RefreshSelection();
    }

    private void HandleColorChanged(EBodyPart part)
    {
        if (part == m_part)
            RefreshSelection();
    }

    private void RefreshSelection()
    {
        int selected = CosmeticLoadout.GetPlayerColor(m_part);

        for (int i = 0; i < m_swatches.Count; i++)
            m_swatches[i].SetSelected(i == selected);
    }
}
