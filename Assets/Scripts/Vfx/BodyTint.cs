using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 몸 색(_BaseColor) 오버라이드의 단일 소유자 — flash > sustained > base 우선순위로 MaterialPropertyBlock에 칠한다.
/// </summary>
public class BodyTint : MonoBehaviour
{
    private static readonly int s_baseColorId = Shader.PropertyToID("_BaseColor");

    private readonly List<Renderer> m_targets = new List<Renderer>();
    private MaterialPropertyBlock m_block;

    private Color m_flash;
    private bool m_hasFlash;
    private Color m_sustained;
    private bool m_hasSustained;
    private Color[] m_baseColors;
    private Color m_baseFallback;
    private bool m_hasBase;
    private bool m_applied;

    /// <summary>몸 렌더링을 켜고 끈다(forceRenderingOff). 색을 모르는 동안 틀린 색 노출을 막는다.</summary>
    public void SetRendering(bool on)
    {
        for (int i = 0; i < m_targets.Count; i++)
        {
            if (m_targets[i] != null)
                m_targets[i].forceRenderingOff = !on;
        }
    }

    private void Awake()
    {
        m_block = new MaterialPropertyBlock();

        foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
        {
            if (renderer is ParticleSystemRenderer || renderer is TrailRenderer)
                continue;

            Material material = renderer.sharedMaterial;
            if (material != null && material.HasProperty(s_baseColorId))
                m_targets.Add(renderer);
        }

        if (!HasTargets)
            Debug.LogWarning(
                $"BodyTint: _BaseColor를 가진 렌더러가 없다 — 이 오브젝트의 색 연출은 동작하지 않는다 ({name})",
                this
            );
    }

    public bool HasTargets => m_targets.Count > 0;

    /// <summary>순간 표시 색을 건다. 지속 표시보다 우선한다.</summary>
    public void SetFlash(Color color)
    {
        m_flash = color;
        m_hasFlash = true;
        Refresh();
    }

    /// <summary>순간 표시를 걷는다 — 지속 표시가 걸려 있으면 그쪽이 다시 드러난다.</summary>
    public void ClearFlash()
    {
        if (!m_hasFlash)
            return;

        m_hasFlash = false;
        Refresh();
    }

    /// <summary>지속 표시 색을 건다. 플래시가 없을 때만 화면에 보인다.</summary>
    public void SetSustained(Color color)
    {
        m_sustained = color;
        m_hasSustained = true;
        Refresh();
    }

    /// <summary>지속 표시를 걷는다.</summary>
    public void ClearSustained()
    {
        if (!m_hasSustained)
            return;

        m_hasSustained = false;
        Refresh();
    }

    /// <summary>밑색을 건다 — 몸 전체 한 색.</summary>
    public void SetBase(Color color) => SetBase(new[] { color }, color);

    /// <summary>부위별 밑색을 건다 — 배열 인덱스가 서브메시 번호다.</summary>
    public void SetBase(Color[] perSubmesh, Color fallback)
    {
        m_baseColors = perSubmesh;
        m_baseFallback = fallback;
        m_hasBase = perSubmesh != null && perSubmesh.Length > 0;
        Refresh();
    }

    /// <summary>밑색을 걷는다 — 머티리얼 원색으로 돌아간다.</summary>
    public void ClearBase()
    {
        if (!m_hasBase)
            return;

        m_hasBase = false;
        Refresh();
    }

    private void Refresh()
    {
        if (m_hasFlash)
        {
            ApplyUniform(m_flash);
            return;
        }

        if (m_hasSustained)
        {
            ApplyUniform(m_sustained);
            return;
        }

        if (m_hasBase)
        {
            ApplyBase();
            return;
        }

        if (!m_applied)
            return;

        m_block.Clear();
        Push(color: null, visibleOnly: false);
        m_applied = false;
    }

    private void ApplyUniform(Color color)
    {
        Push(color, visibleOnly: true);
        m_applied = true;
    }

    private void ApplyBase()
    {
        for (int i = 0; i < m_targets.Count; i++)
        {
            Renderer renderer = m_targets[i];
            if (renderer == null)
                continue;

            int slots = renderer.sharedMaterials.Length;
            if (slots <= 1)
            {
                SetBlock(renderer, m_baseFallback, slot: -1);
                continue;
            }

            for (int slot = 0; slot < slots; slot++)
            {
                Color color = slot < m_baseColors.Length ? m_baseColors[slot] : m_baseFallback;
                SetBlock(renderer, color, slot);
            }
        }

        m_applied = true;
    }

    /// <summary>모든 렌더러에 같은 블록을 민다. <paramref name="color"/>가 null이면 오버라이드를 걷는다.</summary>
    private void Push(Color? color, bool visibleOnly)
    {
        for (int i = 0; i < m_targets.Count; i++)
        {
            Renderer renderer = m_targets[i];
            if (renderer == null)
                continue;
            if (visibleOnly && (!renderer.enabled || !renderer.gameObject.activeInHierarchy))
                continue;

            int slots = renderer.sharedMaterials.Length;
            if (slots <= 1)
            {
                SetBlock(renderer, color, slot: -1);
                continue;
            }

            for (int slot = 0; slot < slots; slot++)
                SetBlock(renderer, color, slot);
        }
    }

    private void SetBlock(Renderer renderer, Color? color, int slot)
    {
        m_block.Clear();
        if (color.HasValue)
            m_block.SetColor(s_baseColorId, color.Value);

        if (slot < 0)
            renderer.SetPropertyBlock(m_block);
        else
            renderer.SetPropertyBlock(m_block, slot);
    }
}
