using EPOOutline;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 조준 피드백 — 오너 전용, 순수 로컬 비주얼(네트워크 동기화 없음). (#184)
/// "지금 실제로 할 수 있는 행동이 있는 대상"에만 윤곽선을 표시한다:
///   - 장착 아이템이 대상에 사용 가능(ItemBase.CanTarget) → 아이템별 색 (우선)
///   - E 상호작용 가능(IInteractable.CanInteract) → 기본색
/// 조준 유지 중에도 NPC 상태·배터리·장착 아이템이 변하므로 매 프레임 재평가한다.
/// Outlinable은 첫 조준 시 런타임 부착 후 캐시(비활성 유지)되므로 대상 프리팹 사전 작업이 필요 없다.
/// </summary>
[RequireComponent(typeof(PlayerInteractor))]
public class InteractionFeedback : NetworkBehaviour
{
    [Header("HUD")]
    [Tooltip("씬에 HUD가 없으면 오너 스폰 시 이 프리팹을 생성한다")]
    [SerializeField] private GameObject m_hudPrefab;

    [Header("아웃라인 (EPO)")]
    [Tooltip("E 상호작용 대상의 기본 윤곽선 색 — 아이템 사용 대상은 ItemBase.TargetOutlineColor를 쓴다")]
    [SerializeField] private Color m_outlineColor = new Color(1f, 0.85f, 0.2f, 1f);

    private PlayerInteractor m_interactor;
    private PlayerItemUser m_itemUser;
    private Outlinable m_currentOutlinable;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        if (CrosshairUI.Instance == null && m_hudPrefab != null)
            Instantiate(m_hudPrefab);

        m_interactor = GetComponent<PlayerInteractor>();
        m_itemUser = GetComponent<PlayerItemUser>(); // 없는 구성(테스트 등)이면 null
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner) return;

        SetOutlined(null, Color.clear);
    }

    private void Update() // 비오너는 OnNetworkSpawn에서 비활성화되므로 오너만 돈다
    {
        Refresh();
    }

    private void Refresh()
    {
        GameObject aimTarget = m_interactor.CurrentTarget;
        IInteractable interactable = m_interactor.CurrentInteractable;

        // 윤곽선 적용 루트를 먼저 해석 — IInteractable 컴포넌트가 붙은 루트 기준(자식 콜라이더 대응),
        // 아이템 경로만 가능하고 IInteractable이 없는 대상이면 조준 대상 자체.
        GameObject root = interactable is Component component ? component.gameObject : aimTarget;

        // 서버 거리 검증과 동일 공식(AimOrigin→대상 루트 중심)으로 재확인 — 레이캐스트는 콜라이더
        // '표면'까지의 거리라서 임계점에서 "윤곽선은 뜨는데 서버가 거부"하는 불일치가 생긴다 (#184)
        bool inRange = false;
        if (root != null)
        {
            float range = m_interactor.Range;
            inRange = (root.transform.position - m_interactor.AimOrigin.position).sqrMagnitude
                <= range * range;
        }

        // ① 아이템 경로 — 장착 아이템이 이 대상에 실제로 사용 가능한가 (아이템별 색, 우선)
        //    색이 "어떤 키가 먹히는지" 안내 역할을 하도록 아이템 경로를 우선한다
        ItemBase equipped = m_itemUser != null ? m_itemUser.EquippedItem : null;
        bool itemUsable = inRange && equipped != null && equipped.CanTarget(aimTarget);

        // ② E 상호작용 경로 — 지금 상태에서 E가 실제로 동작하는가 (기본색)
        bool interactUsable = inRange && !itemUsable
            && interactable != null && interactable.CanInteract(gameObject);

        if (itemUsable || interactUsable)
        {
            SetOutlined(root, itemUsable ? equipped.TargetOutlineColor : m_outlineColor);
            CrosshairUI.Instance?.SetInteractable(true);
        }
        else
        {
            SetOutlined(null, Color.clear);
            CrosshairUI.Instance?.SetInteractable(false);
        }
    }

    private void SetOutlined(GameObject root, Color color)
    {
        // 같은 대상이면 색만 갱신 (아이템 스왑 대응) — 껐다 켜는 낭비 방지
        if (m_currentOutlinable != null && root == m_currentOutlinable.gameObject)
        {
            m_currentOutlinable.OutlineParameters.Color = color;
            return;
        }

        if (m_currentOutlinable != null) // 이전 대상 끄기 (파괴됐으면 이미 null)
            m_currentOutlinable.enabled = false;
        m_currentOutlinable = null;

        if (root == null) return;

        var outlinable = root.GetComponent<Outlinable>();
        if (outlinable == null)
        {
            // 런타임 AddComponent는 Reset()이 호출되지 않으므로 렌더러 수집을 직접 한다
            outlinable = root.AddComponent<Outlinable>();
            outlinable.AddAllChildRenderersToRenderingList(
                RenderersAddingMode.MeshRenderer | RenderersAddingMode.SkinnedMeshRenderer);
        }

        outlinable.OutlineParameters.Color = color;
        outlinable.enabled = true;
        m_currentOutlinable = outlinable;
    }
}
