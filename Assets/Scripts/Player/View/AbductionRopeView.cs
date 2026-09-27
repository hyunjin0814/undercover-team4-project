using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 납치 호송 중 캐리어 NPC의 손과 끌려가는 플레이어를 잇는 밧줄을 각 피어가 로컬로 그린다.
/// 캐리어 참조는 PlayerPenaltyView의 NetworkVariable에서 읽는다.
/// </summary>
[RequireComponent(typeof(PlayerPenaltyView))]
public class AbductionRopeView : MonoBehaviour
{
    [Header("밧줄 선")]
    [Tooltip("밧줄 선 머티리얼 — 비우면 표시를 끈다")]
    [SerializeField] private Material m_ropeMaterial;

    [Tooltip("밧줄 굵기(m)")]
    [SerializeField] private float m_ropeWidth = 0.035f;

    [Range(2, 32)]
    [Tooltip("선 분할 수 — 늘어짐 곡선의 부드러움. 2면 직선이다")]
    [SerializeField] private int m_segments = 12;

    [Tooltip("완전히 늘어졌을 때 가운데가 처지는 최대 깊이(m). 팽팽해질수록 0에 가까워진다")]
    [SerializeField] private float m_maxSag = 0.25f;

    [Tooltip("팽팽함 판정 기준 길이(m) — 이 거리에 가까울수록 늘어짐이 얕아진다")]
    [SerializeField] private float m_ropeLength = 1.4f;

    [Tooltip("손 뼈를 못 찾는 NPC의 대체 높이(m) — 루트(발밑) 기준")]
    [SerializeField] private float m_handHeight = 1.1f;

    [Tooltip("몸통 뼈를 못 찾을 때 내 몸의 대체 매듭 높이(m) — 루트(발밑) 기준")]
    [SerializeField] private float m_knotHeight = 0.5f;

    private class RopeVisual
    {
        public LineRenderer Line;
        public Transform HandSource;
        public Transform HandAnchor;
    }

    private PlayerPenaltyView m_penaltyView;
    private readonly List<RopeVisual> m_visuals = new();

    private bool m_selfKnotResolved;
    private Transform m_selfKnotAnchor;

    private void Awake()
    {
        m_penaltyView = GetComponent<PlayerPenaltyView>();
    }

    private void LateUpdate()
    {
        NpcController carrierA = ResolveAbductionCarrier(m_penaltyView.CarrierA);
        if (carrierA == null)
        {
            HideAll();
            return;
        }

        NpcController carrierB = ResolveAbductionCarrier(m_penaltyView.CarrierB);

        Vector3 knot = KnotPoint();

        int count = 0;
        DrawTo(count++, carrierA, knot);
        if (carrierB != null && carrierB != carrierA)
            DrawTo(count++, carrierB, knot);

        for (int i = count; i < m_visuals.Count; i++)
            HideVisual(i);
    }

    private void OnDisable() => HideAll();

    private static NpcController ResolveAbductionCarrier(NpcController carrier) =>
        carrier != null && carrier.Penalty.IsAbductionDuty ? carrier : null;

    private void DrawTo(int index, NpcController carrier, Vector3 knot)
    {
        RopeVisual visual = EnsureVisual(index);
        if (visual == null)
            return;

        Vector3 hand = HandPoint(visual, carrier.transform);
        visual.Line.enabled = true;

        float distance = Vector3.Distance(hand, knot);
        float slack = 1f - Mathf.Clamp01(distance / Mathf.Max(0.01f, m_ropeLength));
        float sag = m_maxSag * slack;

        if (visual.Line.positionCount != m_segments + 1)
            visual.Line.positionCount = m_segments + 1;

        for (int i = 0; i <= m_segments; i++)
        {
            float t = (float)i / m_segments;
            Vector3 point = Vector3.Lerp(hand, knot, t);
            point.y -= sag * Mathf.Sin(t * Mathf.PI);
            visual.Line.SetPosition(i, point);
        }
    }

    private Vector3 HandPoint(RopeVisual visual, Transform carrier)
    {
        if (carrier != visual.HandSource)
        {
            visual.HandSource = carrier;
            Animator animator = carrier.GetComponentInChildren<Animator>();
            visual.HandAnchor = animator != null && animator.isHuman
                ? animator.GetBoneTransform(HumanBodyBones.RightHand)
                : null;
        }

        return visual.HandAnchor != null
            ? visual.HandAnchor.position
            : carrier.position + Vector3.up * m_handHeight;
    }

    private Vector3 KnotPoint()
    {
        if (!m_selfKnotResolved)
        {
            m_selfKnotResolved = true;
            Animator animator = GetComponentInChildren<Animator>();
            m_selfKnotAnchor = animator != null && animator.isHuman
                ? animator.GetBoneTransform(HumanBodyBones.Chest)
                : null;
        }

        return m_selfKnotAnchor != null
            ? m_selfKnotAnchor.position
            : transform.position + Vector3.up * m_knotHeight;
    }

    private void HideAll()
    {
        for (int i = 0; i < m_visuals.Count; i++)
            HideVisual(i);
    }

    private void HideVisual(int index)
    {
        if (index >= m_visuals.Count)
            return;

        if (m_visuals[index].Line != null)
            m_visuals[index].Line.enabled = false;
    }

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
            enabled = false;
            Debug.LogWarning(
                $"[AbductionRopeView] 밧줄 선 머티리얼이 없어 표시를 끈다. {name} 프리팹에 지정할 것", this);
            return null;
        }

        GameObject ropeObject = new GameObject("AbductionRopeLine");
        ropeObject.transform.SetParent(transform, false);

        var visual = new RopeVisual();
        visual.Line = ropeObject.AddComponent<LineRenderer>();
        visual.Line.useWorldSpace = true;
        visual.Line.sharedMaterial = m_ropeMaterial;
        visual.Line.widthMultiplier = m_ropeWidth;
        visual.Line.numCapVertices = 2;
        visual.Line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        visual.Line.receiveShadows = true;
        visual.Line.generateLightingData = true;
        visual.Line.enabled = false;

        return visual;
    }
}
