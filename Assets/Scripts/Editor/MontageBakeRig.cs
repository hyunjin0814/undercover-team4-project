using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 몽타주 레이어를 찍는 렌더 리그 (#607) — 마네킹·카메라·조명을 한 벌 세워 두고 레이어를 한 장씩 뽑는다.
///
/// 굽기(<see cref="MontageLayerBaker"/>)와 해상도 비교 시트(#619)가 이 클래스를 함께 쓴다. 비교 시트는
/// "이 해상도로 구우면 이렇게 나온다"를 보여 주는 물건이라 <b>실제 굽기와 같은 코드로 찍혀야</b> 뜻이 있다 —
/// 렌더 방식이 갈리는 순간, 시트를 보고 정한 해상도가 실물과 어긋난다.
///
/// 해상도는 리그마다 고정이다. 여러 해상도를 보려면 리그를 그만큼 세운다.
/// </summary>
public sealed class MontageBakeRig : System.IDisposable
{
    /// <summary>바디를 어떻게 찍을지 — 레이어마다 바디의 역할이 다르다.</summary>
    private enum EBodyState
    {
        Original, // 프리팹 그대로 — 조명 렌더에서 이목구비를 뽑을 때
        Flat, // 평면 흰색 — 살 실루엣
        Occluder, // 평면 검정 — 프롭을 찍을 때 가림(depth)만 남긴다
        Custom, // 바깥에서 지정한 머티리얼 — 아틀라스 스왑 비교용 (#619)
    }

    private readonly GameObject m_root;
    private readonly GameObject m_subject;
    private readonly Camera m_camera;
    private readonly Light m_light;
    private readonly Transform m_head;
    private readonly int m_resolution;

    private readonly Material m_flatMaterial;
    private readonly Material m_occluderMaterial;

    private readonly Renderer[] m_bodyRenderers;
    private readonly Material[][] m_originalMaterials;

    private EBodyState m_bodyState = EBodyState.Original;

    /// <summary>프롭을 붙일 머리 본. 찾지 못했으면 리그가 쓸모없다(<see cref="IsValid"/>).</summary>
    public Transform Head => m_head;

    /// <summary>머리 본을 찾아 카메라까지 세워졌는가.</summary>
    public bool IsValid => m_head != null;

    public MontageBakeRig(
        GameObject subjectPrefab,
        Material flatMaterial,
        int resolution,
        float orthoSize,
        float headOffset,
        float cameraDistance,
        float yaw = 0f
    )
    {
        m_resolution = resolution;
        m_flatMaterial = flatMaterial;

        // DontSave — 찍는 동안만 존재하는 리그라 열려 있는 씬을 더럽히지 않는다
        m_root = new GameObject("~MontageBakeRig") { hideFlags = HideFlags.HideAndDontSave };
        m_root.transform.position = new Vector3(0f, -10000f, 0f); // 씬의 다른 것이 화면에 들어오지 않게 멀리 둔다

        m_subject = (GameObject)PrefabUtility.InstantiatePrefab(subjectPrefab, m_root.transform);
        m_subject.transform.localPosition = Vector3.zero;
        m_subject.transform.localRotation = Quaternion.identity;

        m_head = ResolveHead(m_subject.transform);
        if (m_head == null)
            return;

        // 바디 머티리얼을 갈아 끼운 뒤에도 되돌릴 수 있어야 어떤 순서로든 레이어를 찍을 수 있다
        m_bodyRenderers = m_subject.GetComponentsInChildren<Renderer>(true);
        m_originalMaterials = new Material[m_bodyRenderers.Length][];
        for (int i = 0; i < m_bodyRenderers.Length; i++)
            m_originalMaterials[i] =
                m_bodyRenderers[i] != null ? m_bodyRenderers[i].sharedMaterials : null;

        if (flatMaterial != null)
        {
            m_occluderMaterial = new Material(flatMaterial)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            // 셰이더에 따라 색 프로퍼티 이름이 갈린다 (URP는 _BaseColor, 레거시 Unlit은 _Color)
            if (m_occluderMaterial.HasProperty("_BaseColor"))
                m_occluderMaterial.SetColor("_BaseColor", Color.black);
            if (m_occluderMaterial.HasProperty("_Color"))
                m_occluderMaterial.SetColor("_Color", Color.black);
        }

        m_camera = CreateCamera(orthoSize, headOffset, cameraDistance, yaw);
        m_light = CreateLight();
    }

    /// <summary>바디 변형 수 — SciFi 프리팹의 통짜 바디 종수.</summary>
    public int BodyCount => m_subject.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;

    /// <summary>바디 변형 중 하나만 남긴다 — SciFi 프리팹처럼 통짜 바디가 여러 벌 들어 있는 대상용.</summary>
    public bool SelectBody(int index)
    {
        SkinnedMeshRenderer[] bodies = m_subject.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (index < 0 || index >= bodies.Length)
        {
            Debug.LogError(
                $"MontageBakeRig: 바디 인덱스 {index}가 범위를 벗어났다 (바디 {bodies.Length}종)"
            );
            return false;
        }

        for (int i = 0; i < bodies.Length; i++)
            bodies[i].gameObject.SetActive(i == index);
        return true;
    }

    /// <summary>살 — 평면 실루엣. 피부색 곱셈 틴트가 원본 살색·명암에 눌리면 안 되므로 순백으로 찍는다.</summary>
    public Color[] RenderBase()
    {
        SetBodyState(EBodyState.Flat);
        m_light.enabled = false;
        return RenderPixels();
    }

    /// <summary>
    /// 대상을 프리팹 그대로 한 장 — 부위를 떼어낼 수 없는 통짜 메시(SciFi)를 사람이 오려 쓰기 위한 렌더.
    /// <paramref name="bodyMaterial"/>을 주면 바디 머티리얼을 그것으로 갈아 찍는다 — 아틀라스 스왑
    /// 24종이 같은 모델을 어떻게 바꾸는지 보는 용도다 (#619).
    /// </summary>
    public Color[] RenderSubject(Material bodyMaterial = null)
    {
        if (bodyMaterial != null)
        {
            foreach (Renderer renderer in m_bodyRenderers)
            {
                if (renderer != null)
                    ApplyMaterial(renderer, bodyMaterial);
            }
            m_bodyState = EBodyState.Custom; // 다음 상태 전환에서 반드시 다시 칠하게 한다
        }
        else
        {
            SetBodyState(EBodyState.Original);
        }

        m_light.enabled = true;
        return RenderPixels();
    }

    /// <summary>
    /// 프롭 레이어 한 장 — 실루엣과 색을 따로 찍어 합친다.
    ///
    /// <paramref name="silhouette"/>면 흰색으로만 찍는다(머리스타일 — 머리색 축이 칠할 자리다). 아니면
    /// <b>실제 머티리얼에 옵션 색까지 얹어</b> 찍는다 — 노랑·검정 고글처럼 두 색으로 된 프롭을 단색 틴트로
    /// 칠하면 화면과 어긋난다. 색을 실물로 찍으면 바디(검정)와 밝기로 구분할 수 없으므로, 같은 프롭을
    /// 흰색으로 한 번 더 찍어 그것을 오려내는 마스크로 쓴다.
    ///
    /// <paramref name="occluderPrefab"/>을 주면 그 프롭을 <b>검정 가림막으로 함께 붙여</b> 찍는다.
    /// 바디를 검정으로 남기는 것과 같은 원리라, 결과에는 그 프롭에 가려지지 않고 <b>밖으로 나온 부분만</b>
    /// 남는다 — 모자 밖으로 삐져나오는 머리가 있는지 재는 데 쓴다 (#619).
    /// </summary>
    public Color[] RenderProp(
        GameObject propPrefab,
        Color color,
        bool silhouette,
        GameObject occluderPrefab = null
    )
    {
        BeginPropPass();

        GameObject occluder =
            occluderPrefab != null ? InstantiateProp(occluderPrefab, m_occluderMaterial) : null;

        Color[] lit = null;
        if (!silhouette)
        {
            GameObject prop = InstantiateProp(propPrefab, null);
            TintProp(prop, color);
            lit = RenderPixels();
            Object.DestroyImmediate(prop);
        }

        GameObject flat = InstantiateProp(propPrefab, m_flatMaterial);
        Color[] mask = RenderPixels();
        Object.DestroyImmediate(flat);

        if (occluder != null)
            Object.DestroyImmediate(occluder);

        return CutOutProp(mask, lit);
    }

    public void Dispose()
    {
        if (m_root != null)
            Object.DestroyImmediate(m_root);
        if (m_occluderMaterial != null)
            Object.DestroyImmediate(m_occluderMaterial);
    }

    /// <summary>
    /// 프롭을 찍는 상태로 — 바디를 끄지 않고 검정으로 남긴다.
    /// 끄면 머리 뒤에 가려야 할 뒷머리·모자 뒤통수까지 찍혀서 얼굴 위를 덮는다.
    /// </summary>
    private void BeginPropPass()
    {
        SetBodyState(EBodyState.Occluder);
        m_light.enabled = true;
    }

    private void SetBodyState(EBodyState state)
    {
        if (m_bodyState == state)
            return;

        if (state != EBodyState.Original && m_flatMaterial == null)
        {
            Debug.LogError(
                "MontageBakeRig: 평면 머티리얼이 없으면 실루엣을 오려낼 수 없다 — 흰색 URP/Unlit 머티리얼을 지정할 것"
            );
            return;
        }

        for (int i = 0; i < m_bodyRenderers.Length; i++)
        {
            Renderer renderer = m_bodyRenderers[i];
            if (renderer == null)
                continue;

            if (state == EBodyState.Original)
                renderer.sharedMaterials = m_originalMaterials[i];
            else
                ApplyMaterial(
                    renderer,
                    state == EBodyState.Flat ? m_flatMaterial : m_occluderMaterial
                );
        }
        m_bodyState = state;
    }

    // 실제 NPC와 같은 방식으로 붙여야 위치가 어긋나지 않는다 (NpcAppearance.ApplyPropAxis와 동일)
    private GameObject InstantiateProp(GameObject prefab, Material material)
    {
        var prop = (GameObject)PrefabUtility.InstantiatePrefab(prefab, m_head);
        prop.transform.localPosition = Vector3.zero;
        prop.transform.localRotation = Quaternion.identity;
        prop.transform.localScale = Vector3.one;

        if (material != null)
        {
            foreach (Renderer renderer in prop.GetComponentsInChildren<Renderer>(true))
                ApplyMaterial(renderer, material);
        }
        return prop;
    }

    // NpcAppearance.TintRenderers와 같은 방식 — 공유 머티리얼을 건드리지 않는다
    private static void TintProp(GameObject prop, Color color)
    {
        var block = new MaterialPropertyBlock();
        foreach (Renderer renderer in prop.GetComponentsInChildren<Renderer>(true))
        {
            renderer.GetPropertyBlock(block);
            block.SetColor("_BaseColor", color);
            renderer.SetPropertyBlock(block);
        }
    }

    private static void ApplyMaterial(Renderer renderer, Material material)
    {
        var materials = new Material[renderer.sharedMaterials.Length];
        for (int i = 0; i < materials.Length; i++)
            materials[i] = material;
        renderer.sharedMaterials = materials;
    }

    private Camera CreateCamera(float orthoSize, float headOffset, float cameraDistance, float yaw)
    {
        var go = new GameObject("~BakeCamera");
        go.transform.SetParent(m_root.transform, false);

        Camera camera = go.AddComponent<Camera>();
        camera.orthographic = true;
        camera.orthographicSize = orthoSize;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = cameraDistance * 4f;
        camera.enabled = false; // Render()로만 돈다

        // yaw는 대상 주위를 도는 각도다 — 0이 정면. 묶은 머리처럼 뒤로 넘어간 것은 정면 렌더에
        // 안 나오므로, 실물을 눈으로 분류할 때는 옆·뒤도 봐야 한다 (#619).
        Vector3 view = Quaternion.AngleAxis(yaw, Vector3.up) * m_subject.transform.forward;

        Vector3 focus = m_head.position + Vector3.up * headOffset;
        go.transform.position = focus + view * cameraDistance;
        go.transform.rotation = Quaternion.LookRotation(focus - go.transform.position, Vector3.up);
        return camera;
    }

    private Light CreateLight()
    {
        var go = new GameObject("~BakeLight");
        go.transform.SetParent(m_root.transform, false);
        go.transform.rotation = Quaternion.LookRotation(
            -m_subject.transform.forward + Vector3.down * 0.35f
        );

        Light light = go.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        return light;
    }

    /// <summary>
    /// 한 장 렌더해 픽셀로 돌려준다.
    /// 배경을 흰색·검정 두 번 찍어 알파를 역산한다 — URP는 불투명 패스가 알파를 그대로 두지 않아
    /// 투명 배경으로 한 번 찍는 방식이 파이프라인 설정에 따라 통째로 불투명하게 나온다.
    /// </summary>
    private Color[] RenderPixels()
    {
        var rt = new RenderTexture(m_resolution, m_resolution, 24, RenderTextureFormat.ARGB32)
        {
            antiAliasing = 1,
        };
        Texture2D onWhite = Capture(rt, Color.white);
        Texture2D onBlack = Capture(rt, Color.black);

        Color[] white = onWhite.GetPixels();
        Color[] black = onBlack.GetPixels();
        var pixels = new Color[white.Length];

        for (int i = 0; i < pixels.Length; i++)
        {
            float alpha =
                1f
                - (
                    (white[i].r - black[i].r)
                    + (white[i].g - black[i].g)
                    + (white[i].b - black[i].b)
                ) / 3f;
            pixels[i] =
                alpha <= 0.004f
                    ? Color.clear
                    : new Color(
                        black[i].r / alpha,
                        black[i].g / alpha,
                        black[i].b / alpha,
                        Mathf.Clamp01(alpha)
                    );
        }

        Object.DestroyImmediate(onWhite);
        Object.DestroyImmediate(onBlack);
        rt.Release();
        Object.DestroyImmediate(rt);
        return pixels;
    }

    private Texture2D Capture(RenderTexture rt, Color background)
    {
        m_camera.backgroundColor = background;
        m_camera.targetTexture = rt;
        m_camera.Render();
        m_camera.targetTexture = null;

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;
        var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
        texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        texture.Apply();
        RenderTexture.active = previous;
        return texture;
    }

    private static float Luminance(Color color) =>
        0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b;

    /// <summary>
    /// 흰색 렌더(mask)에서 <b>바디에 가려지지 않은</b> 부분만 오려낸다 — 바디는 검정으로 찍혀 오므로
    /// 밝은 픽셀이 곧 보이는 프롭이다. 머리 뒤로 넘어간 뒷머리·모자 뒤통수는 바디에 가려 걷힌다.
    /// color를 주면 그 색을, 안 주면 흰색(표시할 때 칠할 실루엣)을 쓴다.
    /// </summary>
    private static Color[] CutOutProp(Color[] mask, Color[] color)
    {
        var result = new Color[mask.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            float alpha = Luminance(mask[i]) >= 0.5f ? mask[i].a : 0f;
            if (alpha <= 0.004f)
            {
                result[i] = Color.clear;
                continue;
            }

            result[i] =
                color != null
                    ? new Color(color[i].r, color[i].g, color[i].b, alpha)
                    : new Color(1f, 1f, 1f, alpha);
        }
        return result;
    }

    private static Transform ResolveHead(Transform root)
    {
        Animator animator = root.GetComponentInChildren<Animator>();
        if (animator != null && animator.isHuman)
        {
            Transform bone = animator.GetBoneTransform(HumanBodyBones.Head);
            if (bone != null)
                return bone;
        }

        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child.name.Contains("Head"))
                return child;
        }
        return null;
    }
}
