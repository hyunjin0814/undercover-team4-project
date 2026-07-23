using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 소멸 글리치 잔상 (#310) — despawn 직전 NPC의 현재 포즈를 베이크해 정지된 '홀로그램 잔상'을 남기고,
/// 짧게 플리커·지터시킨 뒤 세로로 접히며 꺼진다(TV-off). 파티클 버스트(파편)만으로는 "몸이 사라지는"
/// 그림이 안 읽혀서, 몸체 실루엣이 지직거리며 디매터리얼라이즈되는 연출을 더한다.
///
/// <b>순수 로컬 연출이다</b> — <see cref="NpcDespawnVfx.SpawnLocal"/>이 각 피어에서 NPC가 아직 존재하는
/// 순간(RPC가 despawn 메시지보다 먼저 배송)에 생성하므로, 네트워크에 실리는 것이 없다.
/// 베이크한 메시는 이 오브젝트가 소유하고 파괴 시 함께 해제한다(누수 방지). 본에 붙은 고정 메시(모자·소품)는
/// 에셋 메시를 공유 참조만 하므로 해제 대상이 아니다.
/// </summary>
public class NpcDespawnGhost : MonoBehaviour
{
    // 연출 타이밍 — 프리팹이 아니라 코드 생성이라 상수로 둔다 (튜닝이 잦아지면 NpcDespawnVfx 필드로 승격)
    private const float k_lifetimeSeconds = 0.45f;
    private const float k_collapseStartNormalized = 0.7f; // 수명의 이 지점부터 세로 붕괴 시작
    private const float k_flickerMinSeconds = 0.02f;
    private const float k_flickerMaxSeconds = 0.07f;
    private const float k_jitterAmplitude = 0.07f;

    private readonly List<Renderer> m_renderers = new List<Renderer>();
    private readonly List<Mesh> m_bakedMeshes = new List<Mesh>(); // 파괴 시 함께 해제할 베이크 산출물
    private Vector3 m_origin;
    private float m_elapsed;
    private float m_nextFlickerTime;
    private bool m_visible = true;

    /// <summary>
    /// 대상의 현재 포즈로 잔상을 생성한다 — 대상이 아직 파괴되지 않은 시점(despawn 직전)에 호출할 것.
    /// 스킨드 메시는 현재 포즈로 베이크하고, 본에 붙은 고정 메시(MeshFilter)는 공유 참조로 복사한다.
    /// </summary>
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
            skinned.BakeMesh(baked, true); // useScale — 베이크에 스케일이 포함되므로 파트 스케일은 1
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

    // 잔상 파트 하나를 루트 아래에 만든다 — 원본 렌더러와 같은 월드 포즈, 전 서브메시에 글리치 머티리얼
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

        // 플리커 — 무작위 간격으로 껐다 켠다. 켜질 때마다 수평 지터로 자리를 살짝 튼다.
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

        // 말미 세로 붕괴 — 홀로그램이 꺼지듯 납작해지며(살짝 퍼지며) 사라진다
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
        // 베이크 메시는 에셋이 아니라 런타임 산출물 — 해제하지 않으면 소멸 횟수만큼 누적된다
        for (int i = 0; i < m_bakedMeshes.Count; i++)
        {
            if (m_bakedMeshes[i] != null)
                Destroy(m_bakedMeshes[i]);
        }
    }
}
