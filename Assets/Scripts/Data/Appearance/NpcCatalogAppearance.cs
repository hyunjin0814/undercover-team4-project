using Unity.Netcode;
using UnityEngine;

/// <summary>
/// SciFi 경로 NPC 외형 — 통짜 바디 중 하나를 토글하고 그 모델의 고정 몽타주 조합을 노출한다.
/// 모델 인덱스는 서버 권위 NetworkVariable로 동기화한다.
/// </summary>
public class NpcCatalogAppearance : NetworkBehaviour, IAppearanceProfileSource
{
    private const int k_unassigned = -1;

    [Tooltip("바디 변형이 자식으로 붙은 컨테이너(보통 Model). 비우면 이 오브젝트에서 탐색")]
    [SerializeField] private Transform m_modelRoot;

    [Tooltip("모델 인덱스 → 고정 외형 조합. 배열 순서가 바디 토글 순서와 일치해야 함")]
    [SerializeField] private AppearanceModelCatalog m_catalog;
    public AppearanceModelCatalog Catalog => m_catalog;

    private readonly NetworkVariable<int> m_modelIndex = new NetworkVariable<int>(k_unassigned);
    public int ModelIndex => m_modelIndex.Value;

    private SkinnedMeshRenderer[] m_bodyVariants;
    private AppearanceProfile m_profile = AppearanceProfile.Unassigned;

    public AppearanceProfile Profile => m_profile;

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

    public override void OnNetworkSpawn()
    {
        m_modelIndex.OnValueChanged += HandleModelIndexChanged;

        if (IsServer && m_modelIndex.Value == k_unassigned && BodyVariants.Length > 0)
            m_modelIndex.Value = Random.Range(0, BodyVariants.Length);

        Apply(m_modelIndex.Value);
    }

    public override void OnNetworkDespawn()
    {
        m_modelIndex.OnValueChanged -= HandleModelIndexChanged;
    }

    private void Start()
    {
        if (!IsSpawned && BodyVariants.Length > 0)
            Apply(Random.Range(0, BodyVariants.Length));
    }

    /// <summary>서버 전용: 모델 인덱스를 직접 지정(배정기 통합용). 오프라인은 즉시 적용.</summary>
    public void SetModelIndex(int index)
    {
        if (IsSpawned)
        {
            if (!IsServer) return;
            m_modelIndex.Value = index;
        }
        else Apply(index);
    }

    private void HandleModelIndexChanged(int previous, int current) => Apply(current);

    private void Apply(int index)
    {
        SkinnedMeshRenderer[] variants = BodyVariants;
        if (variants == null || index < 0 || index >= variants.Length)
            return;

        for (int i = 0; i < variants.Length; i++)
            if (variants[i] != null)
                variants[i].gameObject.SetActive(i == index);

        m_profile = m_catalog != null ? m_catalog.GetProfile(index) : AppearanceProfile.Unassigned;
    }
}
