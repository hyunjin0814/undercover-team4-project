using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NPC 외형(바디 모델·특징 축 프로필)을 서버가 정해 인덱스로 동기화하고, 각 피어가 같은 외형을 적용한다.
/// 오프라인 Play에서는 로컬에서 바로 적용한다.
/// </summary>
public class NpcAppearance : NetworkBehaviour, IAppearanceProfileSource
{
    private const int k_unassigned = -1;

    [Header("외형 후보 (바디 변형)")]
    [Tooltip(
        "바디 변형들이 자식으로 붙어 있는 컨테이너(보통 Model). 이 아래 SkinnedMeshRenderer를 가진 자식들을 바디 후보로 보고, 인덱스 순서대로 하나만 활성화한다. 비우면 이 오브젝트에서 탐색"
    )]
    [SerializeField]
    private Transform m_modelRoot;

    [Header("외형 특징 축 (#74)")]
    [Tooltip("축별 옵션 정의. 비워두면 특징 축 적용은 건너뛴다 (모델 교체만 동작)")]
    [SerializeField]
    private AppearanceDatabase m_appearanceDatabase;

    [Tooltip(
        "프롭(머리카락·수염·액세서리)을 붙일 머리 앵커. 비우면 휴머노이드 Head 본을 자동 탐색한다"
    )]
    [SerializeField]
    private Transform m_headAnchor;

    [Tooltip("MaterialOverride 옵션을 적용할 머티리얼 슬롯 — Synty 캐릭터는 아틀라스 1장이라 0")]
    [SerializeField]
    private int m_overrideMaterialSlot = 0;

    [Tooltip("프롭 틴트에 사용할 셰이더 프로퍼티 이름 (Synty/URP 셰이더는 _BaseColor)")]
    [SerializeField]
    private string m_colorPropertyName = "_BaseColor";

    [Tooltip("머리 프롭의 머리색 틴트에 쓰는 셰이더 프로퍼티. 베이스 머티리얼의 셰이더와 짝이다 — 마스크를 쓰는 Synty 캐릭터 셰이더면 _Hair_Color, 마스크 없는 URP/Lit이면 _BaseColor.\n마스크 채널(_Hair_Color)은 마스크 텍스처가 그린 팩의 UV에서만 먹는다. 어휘에 다른 팩 부착물이 섞이면 그 메시에서 틴트가 통째로 무시되므로(#619 — docs §13-13) 지금은 마스크를 안 쓰는 쪽으로 간다")]
    [SerializeField]
    private string m_hairColorPropertyName = "_BaseColor";

    [Tooltip(
        "머리 프롭에 깔 밝은 중립 베이스 머티리얼. 프롭 기본 아틀라스가 어두워 곱셈 틴트하면 밝은 머리색(금발·은발)이 탁해지므로, 머리 프롭 머티리얼을 이 중립 머티리얼로 교체한 뒤 HairColor를 틴트한다. 비우면 원본에 그대로 틴트(기존 동작)"
    )]
    [SerializeField]
    private Material m_hairBaseMaterial;

    [Tooltip("피부색 틴트에 쓰는 셰이더 프로퍼티 (Synty Generic 셰이더는 _Skin_Color)")]
    [SerializeField]
    private string m_skinColorPropertyName = "_Skin_Color";

    private readonly NetworkVariable<int> m_modelIndex = new NetworkVariable<int>(k_unassigned);

    private readonly NetworkVariable<AppearanceProfile> m_syncedProfile =
        new NetworkVariable<AppearanceProfile>(AppearanceProfile.Unassigned);

    private AppearanceProfile m_appliedProfile = AppearanceProfile.Unassigned;

    private readonly GameObject[] m_axisProps = new GameObject[AppearanceProfile.k_axisCount];

    private SkinnedMeshRenderer[] m_bodyVariants;

    private SkinnedMeshRenderer m_activeBody;

    private SkinnedMeshRenderer[] BodyVariants
    {
        get
        {
            if (m_bodyVariants == null)
            {
                Transform root = m_modelRoot != null ? m_modelRoot : transform;
                m_bodyVariants = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            }
            return m_bodyVariants;
        }
    }

    public AppearanceProfile Profile => m_appliedProfile;

    public override void OnNetworkSpawn()
    {
        m_modelIndex.OnValueChanged += HandleModelIndexChanged;
        m_syncedProfile.OnValueChanged += HandleProfileChanged;

        if (IsServer && m_modelIndex.Value == k_unassigned && BodyVariants.Length > 0)
            m_modelIndex.Value = Random.Range(0, BodyVariants.Length);

        ApplyModel(m_modelIndex.Value);

        if (m_syncedProfile.Value.IsAssigned)
            ApplyProfile(m_syncedProfile.Value);
    }

    public override void OnNetworkDespawn()
    {
        m_modelIndex.OnValueChanged -= HandleModelIndexChanged;
        m_syncedProfile.OnValueChanged -= HandleProfileChanged;
    }

    private void Start()
    {
        if (!IsSpawned && BodyVariants.Length > 0)
            ApplyModel(Random.Range(0, BodyVariants.Length));
    }

    /// <summary>외형 특징 조합을 배정한다. 세션에서는 서버 전용, 오프라인에서는 바로 적용한다.</summary>
    public void SetProfile(AppearanceProfile profile)
    {
        if (IsSpawned)
        {
            if (!IsServer)
            {
                Debug.LogWarning(
                    "NpcAppearance: 외형 특징 배정은 서버 권위 — 클라이언트 호출 무시",
                    this
                );
                return;
            }
            m_syncedProfile.Value = profile;
        }
        else
        {
            ApplyProfile(profile);
        }
    }

    private void HandleModelIndexChanged(int previous, int current)
    {
        ApplyModel(current);

        if (m_appliedProfile.IsAssigned)
            ApplyProfile(m_appliedProfile);
    }

    private void HandleProfileChanged(AppearanceProfile previous, AppearanceProfile current)
    {
        ApplyProfile(current);
    }

    /// <summary>바디를 다시 뽑는다. allowFemale이 false면 여성 바디를 제외한다. 서버 전용.</summary>
    public void ServerPickBody(bool allowFemale)
    {
        SkinnedMeshRenderer[] variants = BodyVariants;
        if (variants.Length == 0 || (IsSpawned && !IsServer))
            return;

        var candidates = new List<int>(variants.Length);
        for (int i = 0; i < variants.Length; i++)
        {
            if (variants[i] == null)
                continue;
            if (allowFemale || !IsFemaleBody(variants[i]))
                candidates.Add(i);
        }

        int picked = candidates.Count > 0
            ? candidates[Random.Range(0, candidates.Count)]
            : Random.Range(0, variants.Length);

        if (IsSpawned)
            m_modelIndex.Value = picked;
        else
            ApplyModel(picked);
    }

    private static bool IsFemaleBody(SkinnedMeshRenderer body) =>
        body.name.IndexOf("Female", System.StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>바디 변형 중 index번만 활성화하고 나머지는 끈다.</summary>
    private void ApplyModel(int index)
    {
        SkinnedMeshRenderer[] variants = BodyVariants;
        if (variants == null || index < 0 || index >= variants.Length)
            return;

        for (int i = 0; i < variants.Length; i++)
        {
            if (variants[i] != null)
                variants[i].gameObject.SetActive(i == index);
        }
        m_activeBody = variants[index];
    }

    /// <summary>축별 옵션의 시각 리소스를 데이터베이스에서 찾아 이 NPC에 입힌다.</summary>
    private void ApplyProfile(AppearanceProfile profile)
    {
        m_appliedProfile = profile;
        if (!profile.IsAssigned || m_appearanceDatabase == null)
            return;

        ApplyPropAxis(AppearanceAxis.HairStyle, profile);
        ApplyPropAxis(AppearanceAxis.FacialHair, profile);
        ApplyPropAxis(AppearanceAxis.Headwear, profile);
        ApplyPropAxis(AppearanceAxis.Eyewear, profile);

        TintPropAxis(AppearanceAxis.HairColor, AppearanceAxis.HairStyle, profile, m_hairColorPropertyName);

        ApplySkinColor(AppearanceAxis.SkinColor, profile);
    }

    /// <summary>프롭 축 하나를 적용한다 — 기존 프롭을 제거하고 새 프롭을 옵션 색으로 틴트해 붙인다.</summary>
    private void ApplyPropAxis(AppearanceAxis axis, in AppearanceProfile profile)
    {
        AppearanceDatabase.AppearanceOption option = m_appearanceDatabase.GetOption(
            axis,
            profile.GetIndex(axis)
        );
        int slot = (int)axis;

        if (m_axisProps[slot] != null)
            Destroy(m_axisProps[slot]);
        m_axisProps[slot] = null;

        ulong seed = (IsSpawned ? NetworkObjectId : (ulong)GetInstanceID()) * 31UL + (ulong)axis;
        GameObject prefab = option?.PickProp(seed);
        if (prefab == null)
            return;

        Transform anchor = ResolveHeadAnchor();
        if (anchor == null)
        {
            Debug.LogWarning($"[NpcAppearance] 머리 앵커를 찾지 못해 {axis} 프롭 부착 생략", this);
            return;
        }

        GameObject prop = Instantiate(prefab, anchor, false);
        if (axis == AppearanceAxis.HairStyle && m_hairBaseMaterial != null)
            ApplyBaseMaterial(prop, m_hairBaseMaterial);
        TintRenderers(prop, option.Color);
        m_axisProps[slot] = prop;
    }

    /// <summary>프롭의 모든 렌더러 머티리얼을 중립 베이스로 교체한다.</summary>
    private static void ApplyBaseMaterial(GameObject prop, Material baseMaterial)
    {
        foreach (Renderer renderer in prop.GetComponentsInChildren<Renderer>())
        {
            Material[] mats = renderer.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
                mats[i] = baseMaterial;
            renderer.sharedMaterials = mats;
        }
    }

    /// <summary>프롭 부착 지점. 인스펙터 지정 → 휴머노이드 Head 본 → 이름 검색 순으로 찾는다.</summary>
    private Transform ResolveHeadAnchor()
    {
        if (m_headAnchor != null)
            return m_headAnchor;

        Animator animator = GetComponentInChildren<Animator>();
        if (animator != null && animator.isHuman)
            m_headAnchor = animator.GetBoneTransform(HumanBodyBones.Head);

        if (m_headAnchor == null)
            m_headAnchor = FindChildByName(transform, "Head");

        return m_headAnchor;
    }

    /// <summary>색 전용 축을 다른 축의 프롭에 틴트한다(예: 머리색 → 머리 프롭). 프롭이 없으면 건너뛴다.</summary>
    private void TintPropAxis(
        AppearanceAxis colorAxis,
        AppearanceAxis targetPropAxis,
        in AppearanceProfile profile,
        string propertyName
    )
    {
        GameObject prop = m_axisProps[(int)targetPropAxis];
        if (prop == null)
            return;

        AppearanceDatabase.AppearanceOption option = m_appearanceDatabase.GetOption(
            colorAxis,
            profile.GetIndex(colorAxis)
        );
        if (option != null)
            TintRenderers(prop, option.Color, propertyName);
    }

    /// <summary>프롭의 모든 렌더러에 옵션 색을 틴트한다 — 공유 머티리얼은 건드리지 않는다.</summary>
    private void TintRenderers(GameObject prop, Color color) =>
        TintRenderers(prop, color, m_colorPropertyName);

    /// <summary>지정한 셰이더 프로퍼티로 프롭 렌더러를 틴트한다 (렌더러별 MaterialPropertyBlock — 공유 머티리얼 불변).</summary>
    private void TintRenderers(GameObject prop, Color color, string propertyName)
    {
        var block = new MaterialPropertyBlock();
        foreach (Renderer renderer in prop.GetComponentsInChildren<Renderer>())
        {
            renderer.GetPropertyBlock(block);
            block.SetColor(propertyName, color);
            renderer.SetPropertyBlock(block);
        }
    }

    /// <summary>피부색을 바디 렌더러에 적용한다(머티리얼 교체 또는 _Skin_Color).</summary>
    private void ApplySkinColor(AppearanceAxis axis, in AppearanceProfile profile)
    {
        AppearanceDatabase.AppearanceOption option = m_appearanceDatabase.GetOption(
            axis,
            profile.GetIndex(axis)
        );
        if (option == null)
            return;

        SkinnedMeshRenderer body =
            m_activeBody != null ? m_activeBody : GetComponentInChildren<SkinnedMeshRenderer>();
        if (body == null)
            return;

        if (option.MaterialOverride != null)
        {
            Material[] materials = body.sharedMaterials;
            int slot = Mathf.Clamp(m_overrideMaterialSlot, 0, materials.Length - 1);
            materials[slot] = option.MaterialOverride;
            body.sharedMaterials = materials;
            return;
        }

        var block = new MaterialPropertyBlock();
        body.GetPropertyBlock(block);
        block.SetColor(m_skinColorPropertyName, option.Color);
        body.SetPropertyBlock(block);
    }

    private static Transform FindChildByName(Transform root, string name)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>())
        {
            if (child.name.Contains(name))
                return child;
        }
        return null;
    }
}
