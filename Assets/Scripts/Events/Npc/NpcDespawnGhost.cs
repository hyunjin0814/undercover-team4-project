using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// NPC 소멸 직전 포즈를 베이크한 홀로그램 잔상을 플리커시킨 뒤 세로로 접으며 끄는 로컬 연출.
/// 베이크한 메시는 파괴 시 함께 해제한다.
/// </summary>
public class NpcDespawnGhost : MonoBehaviour
{
    private const float k_lifetimeSeconds = 0.45f;
    private const float k_collapseStartNormalized = 0.7f;
    private const float k_flickerMinSeconds = 0.02f;
    private const float k_flickerMaxSeconds = 0.07f;
    private const float k_jitterAmplitude = 0.07f;

    private readonly List<Renderer> m_renderers = new List<Renderer>();
    private readonly List<Mesh> m_bakedMeshes = new List<Mesh>();
    private Vector3 m_origin;
    private float m_elapsed;
    private float m_nextFlickerTime;
    private bool m_visible = true;

    /// <summary>대상의 현재 포즈로 잔상을 생성한다. despawn 직전에 호출할 것.</summary>
    public static void Spawn(Transform source, Material material)
    {
        GameObject root = new GameObject("FX_NpcDespawnGhost");
        root.transform.SetPositionAndRotation(source.position, source.rotation);

        NpcDespawnGhost ghost = root.AddComponent<NpcDespawnGhost>();
        ghost.m_origin = source.position;
        ghost.m_nextFlickerTime = Random.Range(k_flickerMinSeconds, k_flickerMaxSeconds);

        SkinnedMeshRenderer[] skinnedRenderers = source.GetComponentsInChildren<SkinnedMeshRenderer>();
        for (int i = 0; i < skinnedRenderers.Length; i++)
        {
            SkinnedMeshRenderer skinned = skinnedRenderers[i];
            if (!skinned.enabled || !skinned.gameObject.activeInHierarchy)
                continue;

            Mesh baked = new Mesh();
            skinned.BakeMesh(baked, true);
            ghost.m_bakedMeshes.Add(baked);
            AddPart(ghost, baked, skinned.transform, Vector3.one, material);
        }

        MeshFilter[] filters = source.GetComponentsInChildren<MeshFilter>();
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter filter = filters[i];
            MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
            if (renderer == null || !renderer.enabled || filter.sharedMesh == null)
                continue;

            AddPart(ghost, filter.sharedMesh, filter.transform, filter.transform.lossyScale, material);
        }
    }

    private static void AddPart(NpcDespawnGhost ghost, Mesh mesh, Transform at, Vector3 scale, Material material)
    {
        GameObject part = new GameObject("GhostPart");
        part.transform.SetParent(ghost.transform, false);
        part.transform.SetPositionAndRotation(at.position, at.rotation);
        part.transform.localScale = scale;

        part.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer meshRenderer = part.AddComponent<MeshRenderer>();
        Material[] materials = new Material[mesh.subMeshCount];
        for (int i = 0; i < materials.Length; i++)
            materials[i] = material;
        meshRenderer.sharedMaterials = materials;
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;

        ghost.m_renderers.Add(meshRenderer);
    }

    private void Update()
    {
        m_elapsed += Time.deltaTime;
        float t = m_elapsed / k_lifetimeSeconds;
        if (t >= 1f)
        {
            Destroy(gameObject);
            return;
        }

        if (m_elapsed >= m_nextFlickerTime)
        {
            m_visible = !m_visible;
            m_nextFlickerTime = m_elapsed + Random.Range(k_flickerMinSeconds, k_flickerMaxSeconds);
            for (int i = 0; i < m_renderers.Count; i++)
                m_renderers[i].enabled = m_visible;

            if (m_visible)
            {
                Vector2 jitter = Random.insideUnitCircle * k_jitterAmplitude;
                transform.position = m_origin + new Vector3(jitter.x, 0f, jitter.y);
            }
        }

        if (t > k_collapseStartNormalized)
        {
            float collapse = (t - k_collapseStartNormalized) / (1f - k_collapseStartNormalized);
            transform.localScale = new Vector3(
                1f + collapse * 0.3f,
                Mathf.Max(1f - collapse, 0.02f),
                1f + collapse * 0.3f);
        }
    }

    private void OnDestroy()
    {
        for (int i = 0; i < m_bakedMeshes.Count; i++)
        {
            if (m_bakedMeshes[i] != null)
                Destroy(m_bakedMeshes[i]);
        }
    }
}
