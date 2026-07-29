using UnityEngine;

/// <summary>
/// 밧줄 표현 — 플레이어 손과 묶인 NPC를 잇는 선 + 바닥 먼지. <b>순수 로컬 연출</b>이라 선을 동기화하지
/// 않고, 각 피어가 동기화된 양 끝점(<see cref="PlayerEscorter.TetheredNpcTransform"/>)을 보고 스스로 그린다.
/// 전 피어에서 돈다 — 남이 끌고 가는 모습도 보여야 한다. 선 시작점은 3인칭 손 앵커
/// (<see cref="PlayerHeldItemView.HandAnchor"/>), 없으면 몸통 높이로 대체. (#269)
/// </summary>
[RequireComponent(typeof(PlayerEscorter))]
public class RopeDragView : MonoBehaviour
{
    [Header("밧줄 선")]
    [Tooltip("밧줄 선 머티리얼 — 비우면 선을 그리지 않는다")]
    [SerializeField] private Material m_ropeMaterial;

    [Tooltip("밧줄 굵기(m)")]
    [SerializeField] private float m_ropeWidth = 0.035f;

    [Tooltip("선 분할 수 — 늘어짐 곡선의 부드러움. 2면 직선이다")]
    [Range(2, 32)]
    [SerializeField] private int m_segments = 12;

    [Tooltip("완전히 늘어졌을 때 가운데가 처지는 최대 깊이(m). 팽팽해질수록 0에 가까워진다")]
    [SerializeField] private float m_maxSag = 0.3f;

    [Tooltip("손 앵커가 없을 때 쓰는 대체 시작 높이(m) — 플레이어 발밑 기준")]
    [SerializeField] private float m_fallbackHandHeight = 1.1f;

    [Tooltip("몸통 뼈를 못 찾는 NPC의 대체 매듭 높이(m) — 루트(발밑) 기준")]
    [SerializeField] private float m_npcKnotHeight = 0.25f;

    [Header("먼지")]
    [Tooltip("끌리는 몸 아래에 따라다니는 먼지 파티클 프리팹 — 비우면 먼지 없이 선만 그린다")]
    [SerializeField] private GameObject m_dustPrefab;

    private PlayerEscorter m_escorter;
    private PlayerCarrier m_carrier; // 기능 정지 동료 운반 — 같은 밧줄이라 같은 선을 그린다 (#365)
    private PlayerHeldItemView m_heldItemView;

    // 표시용 인스턴스 — 첫 끌기에 만들고 이후 껐다 켠다(끌 때마다 생성/파괴하지 않는다).
    private LineRenderer m_rope;
    private GameObject m_dust;

    // NPC 몸통 뼈 캐시 — 대상이 바뀔 때만 다시 잡는다. 뼈를 못 찾은 NPC는 null로 캐시해 재검색을 막는다.
    private Transform m_knotAnchorSource;
    private Transform m_knotAnchor;

    private void Awake()
    {
        m_escorter = GetComponent<PlayerEscorter>();
        m_carrier = GetComponent<PlayerCarrier>();
        m_heldItemView = GetComponent<PlayerHeldItemView>(); // 없는 구성(테스트 등)이면 null
    }

    // 끌기 위치는 서버가 Update에서 갱신하고 플레이어도 Update에서 움직인다 —
    // 선을 LateUpdate에서 그려야 이번 프레임의 최종 위치를 잇는다(한 프레임 늦게 따라붙지 않는다).
    private void LateUpdate()
    {
        // 끌고 있는 동안만이 아니라 '묶여 있는 동안' 내내 그린다 — 놓기(E)는 끌기를 멈출 뿐
        // 줄을 푸는 게 아니다. 실제로 풀리면(밧줄 좌클릭 풀기·인계 판정·방치 탈주) 연결이 끊긴다. (#369)
        // 동료 운반(#365)도 같은 줄이다 — 둘은 동시에 성립하지 않으므로 있는 쪽을 그린다.
        // 운반에는 '묶어만 둔' 상태가 없어(내려놓으면 줄이 풀린다) 끌고 있는 동안만 이어진다.
        bool carryingPlayer = m_carrier != null && m_carrier.IsCarrying;
        Transform tethered = carryingPlayer
            ? m_carrier.CarriedTransform
            : m_escorter.TetheredNpcTransform;

        if (tethered == null)
        {
            SetVisible(false);
            return;
        }

        SetVisible(true);
        DrawRope(HandPoint, KnotPoint(tethered));

        // 먼지는 실제로 끌고 있을 때만 — 세워 둔 대상 발밑에서 먼지가 계속 일면 안 된다
        if (m_dust != null)
        {
            bool dragging = carryingPlayer || m_escorter.IsDragging;
            if (m_dust.activeSelf != dragging)
                m_dust.SetActive(dragging);
            m_dust.transform.position = tethered.position;
        }
    }

    private void OnDisable() => SetVisible(false);

    // NPC 쪽 매듭점 — 몸통 뼈가 있으면 그 위치(눕든 서든 몸을 따라간다), 없으면 루트+대체 높이. (#369)
    private Vector3 KnotPoint(Transform tethered)
    {
        if (tethered != m_knotAnchorSource)
        {
            m_knotAnchorSource = tethered;
            Animator animator = tethered.GetComponentInChildren<Animator>();
            m_knotAnchor = animator != null && animator.isHuman
                ? animator.GetBoneTransform(HumanBodyBones.Chest)
                : null;
        }

        return m_knotAnchor != null
            ? m_knotAnchor.position
            : tethered.position + Vector3.up * m_npcKnotHeight;
    }

    private Vector3 HandPoint
    {
        get
        {
            Transform anchor = m_heldItemView != null ? m_heldItemView.HandAnchor : null;
            return anchor != null ? anchor.position : transform.position + Vector3.up * m_fallbackHandHeight;
        }
    }

    // 두 끝점을 잇되 가운데를 아래로 늘어뜨린다. 늘어짐은 밧줄이 팽팽할수록(길이에 가까울수록) 얕아진다 —
    // 멈춰 있으면 축 처지고, 끌기 시작하면 팽팽해지는 변화가 "당기고 있다"를 보여준다.
    private void DrawRope(Vector3 handPoint, Vector3 knotPoint)
    {
        if (m_rope == null)
            return;

        float distance = Vector3.Distance(handPoint, knotPoint);
        float slack = 1f - Mathf.Clamp01(distance / Mathf.Max(0.01f, m_escorter.RopeLength));
        float sag = m_maxSag * slack;

        if (m_rope.positionCount != m_segments + 1)
            m_rope.positionCount = m_segments + 1;

        for (int i = 0; i <= m_segments; i++)
        {
            float t = (float)i / m_segments;
            Vector3 point = Vector3.Lerp(handPoint, knotPoint, t);
            point.y -= sag * Mathf.Sin(t * Mathf.PI); // 양 끝 0, 가운데 최대로 처진다
            m_rope.SetPosition(i, point);
        }
    }

    private void SetVisible(bool visible)
    {
        if (visible && m_rope == null)
            Build();

        if (m_rope != null)
            m_rope.enabled = visible;

        // 먼지 켜기는 LateUpdate가 끌기 여부로 따로 판단한다 — 여기서는 끄기만 보장한다
        if (!visible && m_dust != null && m_dust.activeSelf)
            m_dust.SetActive(false);
    }

    // 선·먼지 인스턴스를 첫 끌기 때 한 번만 만든다 — 끌지 않는 플레이어는 비용이 0이다.
    private void Build()
    {
        if (m_ropeMaterial == null)
        {
            enabled = false; // 머티리얼 없이는 그릴 수 없다 — 매 프레임 헛돌지 않게 스스로 꺼진다
            Debug.LogWarning($"[RopeDragView] 밧줄 선 머티리얼이 없어 표시를 끈다. {name} 프리팹에 지정할 것", this);
            return;
        }

        GameObject ropeObject = new GameObject("RopeLine");
        ropeObject.transform.SetParent(transform, false);

        m_rope = ropeObject.AddComponent<LineRenderer>();
        m_rope.useWorldSpace = true; // 양 끝이 서로 다른 오브젝트라 월드 좌표로 그린다
        m_rope.sharedMaterial = m_ropeMaterial;
        m_rope.widthMultiplier = m_ropeWidth;
        m_rope.numCapVertices = 2;
        m_rope.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        m_rope.receiveShadows = false;
        m_rope.enabled = false;

        if (m_dustPrefab != null)
        {
            m_dust = Instantiate(m_dustPrefab); // 월드에 독립 — 부모를 따라 회전하면 먼지가 같이 돌아버린다
            m_dust.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        if (m_dust != null)
            Destroy(m_dust);
    }
}
