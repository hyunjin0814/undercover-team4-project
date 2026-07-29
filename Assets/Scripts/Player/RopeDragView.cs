using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 밧줄 표현 — 플레이어 손과 묶인 NPC들을 잇는 선 + 바닥 먼지. <b>순수 로컬 연출</b>이라 선을 동기화하지
/// 않고, 각 피어가 동기화된 양 끝점(<see cref="PlayerEscorter.GetTetheredNpc"/>)을 보고 스스로 그린다.
/// 전 피어에서 돈다 — 남이 끌고 가는 모습도 보여야 한다. 선 시작점은 3인칭 손 앵커
/// (<see cref="PlayerHeldItemView.HandAnchor"/>), 없으면 몸통 높이로 대체. (#269)
/// 밧줄 1개당 NPC 1명이라 선도 대상 수만큼 그린다 — 표시 인스턴스는 슬롯 단위로 풀링한다. (#390)
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

    // 밧줄 하나분의 표시 — 선·먼지와 매듭 뼈 캐시. 슬롯 단위로 재사용한다(끌 때마다 생성/파괴하지 않는다).
    private class RopeVisual
    {
        public LineRenderer Line;
        public GameObject Dust;

        // 이 캐시가 가리키는 대상 — 슬롯에 다른 NPC가 들어오면 뼈를 다시 잡는다.
        // 뼈를 못 찾은 NPC는 Anchor를 null로 캐시해 재검색을 막는다.
        public Transform KnotSource;
        public Transform KnotAnchor;
    }

    private PlayerEscorter m_escorter;
    private PlayerHeldItemView m_heldItemView;

    private readonly List<RopeVisual> m_visuals = new List<RopeVisual>();

    private void Awake()
    {
        m_escorter = GetComponent<PlayerEscorter>();
        m_heldItemView = GetComponent<PlayerHeldItemView>(); // 없는 구성(테스트 등)이면 null
    }

    // 끌기 위치는 서버가 Update에서 갱신하고 플레이어도 Update에서 움직인다 —
    // 선을 LateUpdate에서 그려야 이번 프레임의 최종 위치를 잇는다(한 프레임 늦게 따라붙지 않는다).
    private void LateUpdate()
    {
        // 끌고 있는 동안만이 아니라 '묶여 있는 동안' 내내 그린다 — 놓기(E)는 끌기를 멈출 뿐
        // 줄을 푸는 게 아니다. 실제로 풀리면(밧줄 좌클릭 풀기·인계 판정·방치 탈주) 연결이 끊긴다. (#369)
        int count = m_escorter.TetheredCount;
        Vector3 handPoint = HandPoint;

        for (int i = 0; i < count; i++)
        {
            NpcController npc = m_escorter.GetTetheredNpc(i);

            // 아직 참조가 안 풀리는 대상(스폰 전·파괴 직후)은 이번 프레임만 건너뛴다
            if (npc == null)
            {
                HideVisual(i);
                continue;
            }

            RopeVisual visual = EnsureVisual(i);
            if (visual == null)
                return; // 머티리얼이 없어 그릴 수 없다 — Build가 컴포넌트를 스스로 껐다

            visual.Line.enabled = true;
            DrawRope(visual, handPoint, KnotPoint(visual, npc.transform), npc.RopeLength);

            // 먼지는 실제로 끌고 있을 때만 — 세워 둔 대상 발밑에서 먼지가 계속 일면 안 된다
            if (visual.Dust != null)
            {
                if (visual.Dust.activeSelf != npc.IsRoped)
                    visual.Dust.SetActive(npc.IsRoped);
                visual.Dust.transform.position = npc.transform.position;
            }
        }

        // 줄이 줄어들면 남는 슬롯은 꺼 둔다 — 파괴하지 않고 다음 끌기에 재사용한다
        for (int i = count; i < m_visuals.Count; i++)
            HideVisual(i);
    }

    private void OnDisable()
    {
        for (int i = 0; i < m_visuals.Count; i++)
            HideVisual(i);
    }

    // NPC 쪽 매듭점 — 몸통 뼈가 있으면 그 위치(눕든 서든 몸을 따라간다), 없으면 루트+대체 높이. (#369)
    private Vector3 KnotPoint(RopeVisual visual, Transform tethered)
    {
        if (tethered != visual.KnotSource)
        {
            visual.KnotSource = tethered;
            Animator animator = tethered.GetComponentInChildren<Animator>();
            visual.KnotAnchor = animator != null && animator.isHuman
                ? animator.GetBoneTransform(HumanBodyBones.Chest)
                : null;
        }

        return visual.KnotAnchor != null
            ? visual.KnotAnchor.position
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
    private void DrawRope(RopeVisual visual, Vector3 handPoint, Vector3 knotPoint, float ropeLength)
    {
        LineRenderer line = visual.Line;
        float distance = Vector3.Distance(handPoint, knotPoint);
        float slack = 1f - Mathf.Clamp01(distance / Mathf.Max(0.01f, ropeLength));
        float sag = m_maxSag * slack;

        if (line.positionCount != m_segments + 1)
            line.positionCount = m_segments + 1;

        for (int i = 0; i <= m_segments; i++)
        {
            float t = (float)i / m_segments;
            Vector3 point = Vector3.Lerp(handPoint, knotPoint, t);
            point.y -= sag * Mathf.Sin(t * Mathf.PI); // 양 끝 0, 가운데 최대로 처진다
            line.SetPosition(i, point);
        }
    }

    private void HideVisual(int index)
    {
        if (index >= m_visuals.Count)
            return;

        RopeVisual visual = m_visuals[index];
        if (visual.Line != null)
            visual.Line.enabled = false;
        if (visual.Dust != null && visual.Dust.activeSelf)
            visual.Dust.SetActive(false);
    }

    // 슬롯의 표시 인스턴스를 필요할 때 한 번만 만든다 — 끌지 않는 플레이어는 비용이 0이다.
    private RopeVisual EnsureVisual(int index)
    {
        while (m_visuals.Count <= index)
        {
            RopeVisual built = Build();
            if (built == null)
                return null;
            m_visuals.Add(built);
        }

        return m_visuals[index];
    }

    private RopeVisual Build()
    {
        if (m_ropeMaterial == null)
        {
            enabled = false; // 머티리얼 없이는 그릴 수 없다 — 매 프레임 헛돌지 않게 스스로 꺼진다
            Debug.LogWarning($"[RopeDragView] 밧줄 선 머티리얼이 없어 표시를 끈다. {name} 프리팹에 지정할 것", this);
            return null;
        }

        GameObject ropeObject = new GameObject("RopeLine");
        ropeObject.transform.SetParent(transform, false);

        var visual = new RopeVisual();
        visual.Line = ropeObject.AddComponent<LineRenderer>();
        visual.Line.useWorldSpace = true; // 양 끝이 서로 다른 오브젝트라 월드 좌표로 그린다
        visual.Line.sharedMaterial = m_ropeMaterial;
        visual.Line.widthMultiplier = m_ropeWidth;
        visual.Line.numCapVertices = 2;
        visual.Line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        visual.Line.receiveShadows = false;
        visual.Line.enabled = false;

        if (m_dustPrefab != null)
        {
            visual.Dust = Instantiate(m_dustPrefab); // 월드에 독립 — 부모를 따라 회전하면 먼지가 같이 돌아버린다
            visual.Dust.SetActive(false);
        }

        return visual;
    }

    private void OnDestroy()
    {
        foreach (RopeVisual visual in m_visuals)
            if (visual.Dust != null)
                Destroy(visual.Dust);
    }
}
