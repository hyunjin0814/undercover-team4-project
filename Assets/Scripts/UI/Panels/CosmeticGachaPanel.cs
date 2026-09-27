using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.UI;

/// <summary>
/// 치장 뽑기 릴 연출 — 아이콘이 흘러가다 감속해 이미 정해진 당첨이 가운데에 멈춘다.
/// 칸을 재활용하며, ESC 스택에 쌓지 않고 스스로 닫힌다.
/// </summary>
public class CosmeticGachaPanel : PanelBase
{
    [Header("배선")]
    [Tooltip("치장 카탈로그 — 자판기와 같은 에셋을 물릴 것")]
    [SerializeField] private AccessoryCatalog m_catalog;

    [Tooltip("칸이 놓이는 띠 — 잘리는 창(RectMask2D)의 자식이어야 한다")]
    [SerializeField] private RectTransform m_strip;

    [Tooltip("가운데 당첨 칸 테두리 — 멈추는 순간 색이 바뀐다")]
    [SerializeField] private Graphic[] m_frame;

    [Tooltip("결과 문구")]
    [SerializeField] private TMP_Text m_resultLabel;

    [Header("릴")]
    [Tooltip("한 칸의 한 변(px)")]
    [SerializeField] private float m_cellSize = 128f;

    [Tooltip("칸 사이 간격(px)")]
    [SerializeField] private float m_gap = 12f;

    [Tooltip("동시에 만드는 칸 수 — 홀수여야 가운데가 생긴다. 양 끝 두 칸은 잘려 보인다")]
    [SerializeField] private int m_visibleCells = 7;

    [Tooltip("멈추기까지 지나가는 칸 수 — 클수록 길게 돈다")]
    [SerializeField] private int m_scrollCells = 32;

    [Tooltip("도는 시간(초)")]
    [SerializeField] private float m_spinSeconds = 2.6f;

    [Tooltip("멈춘 뒤 결과를 보여 주는 시간(초)")]
    [SerializeField] private float m_holdSeconds = 1.8f;

    [Header("문구")]
    [Tooltip("새로 얻었을 때 — {0}에 이름이 들어간다")]
    [SerializeField] private LocalizedString m_resultFormat;

    [Tooltip("이미 가진 것이었을 때 — {0}에 이름이 들어간다")]
    [SerializeField] private LocalizedString m_duplicateFormat;

    [Header("소리")]
    [Tooltip("릴이 도는 동안 나는 소리 — 멈추는 순간 끊긴다")]
    [SerializeField] private EAudioClip m_spinSound = EAudioClip.GachaSpin;

    [Tooltip("당첨이 가운데 멈춘 순간 나는 소리")]
    [SerializeField] private EAudioClip m_revealSound = EAudioClip.GachaReveal;

    [Header("색")]
    [SerializeField] private Color m_frameIdle = new Color(1f, 1f, 1f, 0.35f);
    [Tooltip("공용 색 팔레트 — 당첨 테두리에 Highlight를 쓴다 (#951)")]
    [SerializeField] private UiColorPalette m_palette;

    public override bool CanCloseWithESC => false;
    public override bool IsStackable => false;

    private readonly List<Cell> m_cells = new List<Cell>();
    private readonly List<(EAccessorySlot Slot, int Index)> m_sequence =
        new List<(EAccessorySlot Slot, int Index)>();
    private readonly List<(EAccessorySlot Slot, int Index)> m_pool =
        new List<(EAccessorySlot Slot, int Index)>();

    private bool m_spinning;

    private AudioSource m_spinSource;

    public bool IsSpinning => m_spinning;

    public float SpinSeconds => m_spinSeconds;

    private float Pitch => m_cellSize + m_gap;
    private int Center => VisibleCells / 2;

    private int VisibleCells => Mathf.Max(3, m_visibleCells | 1);
    private int ScrollCells => Mathf.Max(1, m_scrollCells);

    protected override void Awake()
    {
        base.Awake();

        m_spinSource = gameObject.AddComponent<AudioSource>();
        m_spinSource.playOnAwake = false;
        m_spinSource.loop = true;
        m_spinSource.spatialBlend = 0f;
    }

    /// <summary>결과가 가운데에 멈추도록 릴을 돌린다. gained가 false면 중복 환급 문구를 띄운다.</summary>
    public void Play(EAccessorySlot slot, int index, bool gained)
    {
        if (m_spinning || m_catalog == null || m_strip == null)
            return;

        BuildPool();
        if (m_pool.Count == 0)
            return;

        BuildSequence(slot, index);
        EnsureCells();
        SetFrameColor(m_frameIdle);

        if (m_resultLabel != null)
            m_resultLabel.text = string.Empty;

        OpenPanel();
        SpinAsync(slot, index, gained).Forget();
    }

    private void BuildPool()
    {
        if (m_pool.Count > 0)
            return;

        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
        {
            int count = m_catalog.CountOf(slot);
            for (int i = 1; i < count; i++)
                if (m_catalog.Get(slot, i) != null)
                    m_pool.Add((slot, i));
        }
    }

    private void BuildSequence(EAccessorySlot slot, int index)
    {
        m_sequence.Clear();
        int length = ScrollCells + VisibleCells;
        for (int i = 0; i < length; i++)
            m_sequence.Add(m_pool[UnityEngine.Random.Range(0, m_pool.Count)]);

        m_sequence[ScrollCells + Center] = (slot, index);
    }

    private void EnsureCells()
    {
        while (m_cells.Count < VisibleCells)
            m_cells.Add(CreateCell(m_cells.Count));

        for (int i = 0; i < m_cells.Count; i++)
            m_cells[i].Root.gameObject.SetActive(i < VisibleCells);
    }

    private Cell CreateCell(int order)
    {
        var root = new GameObject($"Cell {order}", typeof(RectTransform)).GetComponent<RectTransform>();
        root.SetParent(m_strip, false);
        root.anchorMin = new Vector2(0.5f, 0.5f);
        root.anchorMax = new Vector2(0.5f, 0.5f);
        root.pivot = new Vector2(0.5f, 0.5f);
        root.sizeDelta = new Vector2(m_cellSize, m_cellSize);

        var icon = new GameObject("Icon", typeof(RectTransform)).AddComponent<Image>();
        var iconRect = icon.rectTransform;
        iconRect.SetParent(root, false);
        iconRect.anchorMin = Vector2.zero;
        iconRect.anchorMax = Vector2.one;
        iconRect.offsetMin = Vector2.zero;
        iconRect.offsetMax = Vector2.zero;
        icon.preserveAspect = true;
        icon.raycastTarget = false;

        var label = new GameObject("Name", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
        var labelRect = label.rectTransform;
        labelRect.SetParent(root, false);
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;
        label.alignment = TextAlignmentOptions.Center;
        label.fontSize = 16f;
        label.raycastTarget = false;
        labelRect.SetAsFirstSibling();

        return new Cell { Root = root, Icon = icon, Label = label };
    }

    private async UniTaskVoid SpinAsync(EAccessorySlot slot, int index, bool gained)
    {
        m_spinning = true;
        PlaySpinSound();
        try
        {
            float elapsed = 0f;
            while (elapsed < m_spinSeconds)
            {
                await UniTask.NextFrame(destroyCancellationToken);
                elapsed += Time.unscaledDeltaTime;

                float t = Mathf.Clamp01(elapsed / m_spinSeconds);
                Layout(ScrollCells * (1f - Mathf.Pow(1f - t, 3f)));
            }

            Layout(ScrollCells);
            StopSpinSound();
            App.Sound?.PlaySfx2D(m_revealSound);
            SetFrameColor(FrameWinColor);
            ShowResult(slot, index, gained);

            await UniTask.Delay(
                TimeSpan.FromSeconds(m_holdSeconds),
                DelayType.UnscaledDeltaTime,
                cancellationToken: destroyCancellationToken
            );
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            StopSpinSound();
            m_spinning = false;

            if (m_panelRoot != null)
                ClosePanel();
        }
    }

    private void Layout(float passed)
    {
        int start = Mathf.FloorToInt(passed);
        float fraction = passed - start;

        for (int i = 0; i < VisibleCells; i++)
        {
            Cell cell = m_cells[i];
            cell.Root.anchoredPosition = new Vector2((i - Center - fraction) * Pitch, 0f);

            int at = Mathf.Clamp(start + i, 0, m_sequence.Count - 1);
            (EAccessorySlot Slot, int Index) shown = m_sequence[at];
            Sprite icon = m_catalog.IconOf(shown.Slot, shown.Index);

            cell.Icon.sprite = icon;
            cell.Icon.enabled = icon != null;
            cell.Label.text = icon != null ? string.Empty : CosmeticNames.Of(m_catalog.Get(shown.Slot, shown.Index));
        }
    }

    private void ShowResult(EAccessorySlot slot, int index, bool gained)
    {
        if (m_resultLabel == null)
            return;

        LocalizedString format = gained ? m_resultFormat : m_duplicateFormat;
        format.Arguments = new object[] { CosmeticNames.Of(m_catalog.Get(slot, index)) };
        m_resultLabel.text = format.GetLocalizedString();
    }

    private void PlaySpinSound()
    {
        AudioLibrary.Entry entry = App.Sound?.GetSfxEntry(m_spinSound);
        if (m_spinSource == null || entry?.Clip == null)
            return;

        m_spinSource.clip = entry.Clip;
        m_spinSource.volume = SoundManager.SfxVolumeOf(entry);
        m_spinSource.time = Mathf.Clamp(entry.StartOffset, 0f, Mathf.Max(0f, entry.Clip.length - 0.05f));
        m_spinSource.Play();
    }

    private void StopSpinSound()
    {
        if (m_spinSource != null && m_spinSource.isPlaying)
            m_spinSource.Stop();
    }

    private void SetFrameColor(Color color)
    {
        if (m_frame == null)
            return;

        foreach (Graphic bar in m_frame)
            if (bar != null)
                bar.color = color;
    }

    private class Cell
    {
        public RectTransform Root;
        public Image Icon;
        public TextMeshProUGUI Label;
    }

    private Color FrameWinColor
    {
        get
        {
            if (m_palette != null)
                return m_palette.Highlight;

            Debug.LogWarning("CosmeticGachaPanel: 색 팔레트가 연결되지 않았다", this);
            return Color.white;
        }
    }
}
