using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 본부 설치형 아이템의 공통 기반 — 상점 구매 시 배달이 서버에서 SetInstalled로 켠다(GDD 8-4).
/// 미설치 상태에서는 모델·콜라이더가 꺼져 있다. 설치 여부는 서버 권위로 동기화한다.
/// </summary>
public abstract class InstallableItem : NetworkBehaviour, IInteractable
{
    [Header("설치 (상점 #182 접합점)")]
    [Tooltip("상점 식별자 — ShopDelivery가 이 값으로 팀 구매 목록과 대조한다. None이면 사도 배달되지 않는다")]
    [SerializeField] private EInstallable m_id = EInstallable.None;

    [Tooltip("라운드 시작 시의 설치 상태. 정식 흐름에서는 꺼 둘 것 — 구매가 켜 준다")]
    [SerializeField] private bool m_installedOnStart;

    private readonly NetworkVariable<bool> m_installedSynced = new NetworkVariable<bool>();
    private bool m_installed;

    private Renderer[] m_renderers;
    private Collider[] m_colliders;

    private bool m_notifiedInstalled;

    public EInstallable Id => m_id;

    public bool IsInstalled => IsSpawned && !IsServer ? m_installedSynced.Value : m_installed;

    protected void Awake()
    {
        m_renderers = GetComponentsInChildren<Renderer>(true);
        m_colliders = GetComponentsInChildren<Collider>(true);

        ApplyVisibility(false);

        OnInstallableAwake();
    }

    public sealed override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            SetInstalled(m_installedOnStart);

            if (m_id == EInstallable.None)
                Debug.LogWarning($"[설치형] {name}: 상점 식별자가 None — 구매해도 배달되지 않는다", this);
        }

        m_installedSynced.OnValueChanged += HandleInstalledChanged;
        ApplyInstalled();

        OnInstallableSpawn();
    }

    public sealed override void OnNetworkDespawn()
    {
        m_installedSynced.OnValueChanged -= HandleInstalledChanged;

        OnInstallableDespawn();
    }

    /// <summary>Awake 시점 초기화 — 컴포넌트 캐시 등. 스폰 전에 불리므로 네트워크 상태를 읽지 말 것.</summary>
    protected virtual void OnInstallableAwake() { }

    /// <summary>스폰 시점 초기화 — 이벤트 구독 등. 설치 상태는 이미 확정돼 있다.</summary>
    protected virtual void OnInstallableSpawn() { }

    /// <summary>디스폰 정리 — <see cref="OnInstallableSpawn"/>에서 건 구독을 해제한다.</summary>
    protected virtual void OnInstallableDespawn() { }

    /// <summary>설치 상태가 바뀐 순간 호출되는 훅. 상태성 표시에만 쓰고 일회성 연출에는 쓰지 말 것.</summary>
    protected virtual void OnInstalledChanged(bool installed) { }

    /// <summary>E 상호작용 본체 — 설치된 상태에서만 불린다(미설치 게이트는 베이스가 걸었다).</summary>
    protected abstract void OnInteract(GameObject interactor);

    /// <summary>설치 상태를 바꾼다. 서버(또는 오프라인) 전용.</summary>
    public void SetInstalled(bool installed)
    {
        if (IsSpawned && !IsServer)
            return;

        m_installed = installed;
        if (IsSpawned && IsServer)
            m_installedSynced.Value = installed;

        ApplyInstalled();
    }

    private void HandleInstalledChanged(bool previous, bool current) => ApplyInstalled();

    private void ApplyInstalled()
    {
        bool installed = IsInstalled;

        ApplyVisibility(installed);

        if (m_notifiedInstalled == installed)
            return;

        m_notifiedInstalled = installed;
        OnInstalledChanged(installed);
    }

    private void ApplyVisibility(bool visible)
    {
        foreach (Renderer itemRenderer in m_renderers)
        {
            if (itemRenderer != null)
                itemRenderer.enabled = visible;
        }

        foreach (Collider itemCollider in m_colliders)
        {
            if (itemCollider != null)
                itemCollider.enabled = visible;
        }
    }

    /// <summary>설치된 경우에만 상호작용 가능하다. 파생은 조건을 좁힐 때만 base와 함께 재정의한다.</summary>
    public virtual bool CanInteract(GameObject interactor) => IsInstalled;

    /// <summary>미설치 게이트를 거친 뒤 OnInteract로 넘긴다.</summary>
    public void Interact(GameObject interactor)
    {
        if (!IsInstalled)
        {
            Debug.Log($"[설치형] {name}: 아직 설치되지 않음 (상점에서 구매 필요)");
            return;
        }

        OnInteract(interactor);
    }
}
