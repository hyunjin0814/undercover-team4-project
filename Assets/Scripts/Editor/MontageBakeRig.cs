using UnityEditor;
using UnityEngine;

/// <summary>
/// 몽타주 레이어를 찍는 렌더 리그 — 마네킹·카메라·조명을 세워 두고 레이어를 한 장씩 렌더한다.
/// 굽기와 해상도 비교 시트가 함께 쓰며, 해상도는 리그마다 고정이다.
/// </summary>
public sealed class MontageBakeRig : System.IDisposable
{
    private enum EBodyState
    {
        Original,
        Flat,
        Occluder,
        Custom,
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

    public Transform Head => m_head;

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

        m_root = new GameObject("~MontageBakeRig") { hideFlags = HideFlags.HideAndDontSave };
        m_root.transform.position = new Vector3(0f, -10000f, 0f);

        m_subject = (GameObject)PrefabUtility.InstantiatePrefab(subjectPrefab, m_root.transform);
        m_subject.transform.localPosition = Vector3.zero;
        m_subject.transform.localRotation = Quaternion.identity;

        m_head = ResolveHead(m_subject.transform);
        if (m_head == null)
            return;

        m_bodyRenderers = m_subject.GetComponentsInChildren<Renderer>(true);
        m_originalMaterials = new Material[m_bodyRenderers.Length][];
        for (int i = 0; i < m_bodyRenderers.Length; i++)
            m_originalMaterials[i] = m_bodyRenderers[i] != null ? m_bodyRenderers[i].sharedMaterials : null;

        if (flatMaterial != null)
        {
            m_occluderMaterial = new Material(flatMaterial) { hideFlags = HideFlags.HideAndDontSave };
            if (m_occluderMaterial.HasProperty("_BaseColor"))
                m_occluderMaterial.SetColor("_BaseColor", Color.black);
            if (m_occluderMaterial.HasProperty("_Color"))
                m_occluderMaterial.SetColor("_Color", Color.black);
        }

        m_camera = CreateCamera(orthoSize, headOffset, cameraDistance, yaw);
        m_light = CreateLight();
    }

    public int BodyCount => m_subject.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;

    /// <summary>바디 변형 중 하나만 남긴다 — SciFi 프리팹처럼 통짜 바디가 여러 벌 들어 있는 대상용.</summary>
    public bool SelectBody(int index)
    {
        SkinnedMeshRenderer[] bodies = m_subject.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (index < 0 || index >= bodies.Length)
        {
            Debug.LogError($"MontageBakeRig: 바디 인덱스 {index}가 범위를 벗어났다 (바디 {bodies.Length}종)");
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
        return Whiten(RenderPixels());
    }

    /// <summary>알파만 남기고 RGB를 순백으로 만든다.</summary>
    private static Color[] Whiten(Color[] pixels)
    {
        var result = new Color[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
            result[i] = new Color(1f, 1f, 1f, pixels[i].a);
        return result;
    }

    /// <summary>대상 프리팹을 그대로 한 장 렌더한다. bodyMaterial을 주면 바디 머티리얼을 바꿔 찍는다.</summary>
    public Color[] RenderSubject(Material bodyMaterial = null)
    {
        if (bodyMaterial != null)
        {
            foreach (Renderer renderer in m_bodyRenderers)
            {
                if (renderer != null)
                    ApplyMaterial(renderer, bodyMaterial);
            }
            m_bodyState = EBodyState.Custom;
        }
        else
        {
            SetBodyState(EBodyState.Original);
        }

        m_light.enabled = true;
        return RenderPixels();
    }

    /// <summary>프롭 레이어 한 장을 렌더한다 — 실루엣(흰색) 또는 실제 색으로 찍고, occluder가 있으면 가림막으로 함께 찍는다.</summary>
    public Color[] RenderProp(GameObject propPrefab, Color color, bool silhouette, GameObject occluderPrefab = null)
    {
        BeginPropPass();

        GameObject occluder = occluderPrefab != null ? InstantiateProp(occluderPrefab, m_occluderMaterial) : null;

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

    /// <summary>프롭 렌더 상태로 전환한다 — 바디를 검정으로 남겨 가림막으로 쓴다.</summary>
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
            Debug.LogError("MontageBakeRig: 평면 머티리얼이 없으면 실루엣을 오려낼 수 없다 — 흰색 URP/Unlit 머티리얼을 지정할 것");
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
                ApplyMaterial(renderer, state == EBodyState.Flat ? m_flatMaterial : m_occluderMaterial);
        }
        m_bodyState = state;
    }

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
        camera.enabled = false;

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
        go.transform.rotation = Quaternion.LookRotation(-m_subject.transform.forward + Vector3.down * 0.35f);

        Light light = go.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        return light;
    }

    /// <summary>한 장을 렌더해 픽셀로 돌려준다. 흰·검정 배경 두 번 렌더로 알파를 역산한다.</summary>
    private Color[] RenderPixels()
    {
        var rt = new RenderTexture(m_resolution, m_resolution, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
        Texture2D onWhite = Capture(rt, Color.white);
        Texture2D onBlack = Capture(rt, Color.black);

        Color[] white = onWhite.GetPixels();
        Color[] black = onBlack.GetPixels();
        var pixels = new Color[white.Length];

        for (int i = 0; i < pixels.Length; i++)
        {
            float alpha = 1f - ((white[i].r - black[i].r) + (white[i].g - black[i].g) + (white[i].b - black[i].b)) / 3f;
            pixels[i] = alpha <= 0.004f
                ? Color.clear
                : new Color(black[i].r / alpha, black[i].g / alpha, black[i].b / alpha, Mathf.Clamp01(alpha));
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

    private static float Luminance(Color color) => 0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b;

    /// <summary>흰색 마스크 렌더에서 바디에 가려지지 않은 부분만 오려낸다.</summary>
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

            result[i] = color != null
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
