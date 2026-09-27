using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 손과 묶인 대상을 잇는 밧줄 선과 바닥 먼지를 각 피어가 로컬로 그린다.
/// 오너 1인칭 화면에서는 FP 팔의 손을 시작점으로 쓴다.
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

    [Header("1인칭 보정 (#828)")]
    [Tooltip("오너 1인칭에서 시작점을 놓을 카메라 앞 거리(m) — 1인칭 팔 카메라와 월드 카메라의 FOV가 달라 " +
        "화면 위치를 맞추려면 깊이를 새로 정해야 한다. 0 이하면 손 자체 깊이를 쓴다")]
    [SerializeField] private float m_fpStartDepth = 0.6f;

    [Tooltip("오너 1인칭 시작점의 굵기(m) — 손이 카메라에서 ~0.5m라 정상 굵기(m_ropeWidth)를 그대로 두면 " +
        "화면에서 두꺼운 띠로 잡힌다. m_ropeWidth 지점까지 짧게 테이퍼링한다")]
    [SerializeField] private float m_fpStartWidth = 0.012f;

    [Tooltip("오너 1인칭 시작점의 화면 가로 보정(뷰포트 비율, 화면 폭 기준) — 음수면 왼쪽, 양수면 " +
        "오른쪽으로 밀린다. 화면 위치 자체를 옮기는 값이라 깊이(m_fpStartDepth)와 달리 손에서 " +
        "벗어난다 — 밧줄이 손을 가리거나 시야 가장자리에서 어색할 때만 미세하게 쓸 것")]
    [SerializeField] private float m_fpStartOffsetX = 0f;

    [Header("먼지")]
    [Tooltip("끌리는 몸 아래에 따라다니는 먼지 파티클 프리팹 — 비우면 먼지 없이 선만 그린다")]
    [SerializeField] private GameObject m_dustPrefab;

    private class RopeVisual
    {
        public LineRenderer Line;
        public GameObject Dust;

        public Transform KnotSource;
        public Transform KnotAnchor;
    }

    private PlayerEscorter m_escorter;
    private PlayerCarrier m_carrier;
    private PlayerHeldItemView m_heldItemView;
    private PlayerHandView m_handView;

    private readonly List<RopeVisual> m_visuals = new List<RopeVisual>();

    private static readonly AnimationCurve s_flatWidthCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);
    private const float k_fpTaperFraction = 0.25f;
    private AnimationCurve m_fpWidthCurve;
    private float m_fpWidthCurveRatio = -1f;

    private void Awake()
    {
        m_escorter = GetComponent<PlayerEscorter>();
        m_carrier = GetComponent<PlayerCarrier>();
        m_heldItemView = GetComponent<PlayerHeldItemView>();
        m_handView = GetComponent<PlayerHandView>();
    }

    private void LateUpdate()
    {
        int count = m_escorter.TetheredCount;
        bool isFirstPerson = TryGetHandPoint(out Vector3 handPoint);

        for (int i = 0; i < count; i++)
        {
            NpcController npc = m_escorter.GetTetheredNpc(i);

            if (npc == null)
            {
                HideVisual(i);
                continue;
            }

            RopeVisual visual = EnsureVisual(i);
            if (visual == null)
                return;

            visual.Line.enabled = true;
            DrawRope(visual, handPoint, KnotPoint(visual, npc.transform), npc.Rope.RopeLength, isFirstPerson);

            if (visual.Dust != null)
            {
                if (visual.Dust.activeSelf != npc.Rope.IsRoped)
                    visual.Dust.SetActive(npc.Rope.IsRoped);
                visual.Dust.transform.position = npc.transform.position;
            }
        }

        Transform carried = m_carrier != null ? m_carrier.CarriedTransform : null;
        if (carried != null)
        {
            RopeVisual visual = EnsureVisual(count);
            if (visual == null)
                return;

            visual.Line.enabled = true;
            DrawRope(visual, handPoint, KnotPoint(visual, carried), CarriedRopeLength(carried), isFirstPerson);

            if (visual.Dust != null)
            {
                if (!visual.Dust.activeSelf)
                    visual.Dust.SetActive(true);
                visual.Dust.transform.position = carried.position;
            }

            count++;
        }

        for (int i = count; i < m_visuals.Count; i++)
            HideVisual(i);
    }

    private void OnDisable()
    {
        for (int i = 0; i < m_visuals.Count; i++)
            HideVisual(i);
    }

    private Transform m_carriedLengthSource;
    private PlayerTowedMotion m_carriedTowed;

    private const float k_fallbackCarriedRopeLength = 1.6f;

    private float CarriedRopeLength(Transform carried)
    {
        if (carried != m_carriedLengthSource)
        {
            m_carriedLengthSource = carried;
            m_carriedTowed = carried.GetComponent<PlayerTowedMotion>();
        }

        return m_carriedTowed != null ? m_carriedTowed.RopeLength : k_fallbackCarriedRopeLength;
    }

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

    private bool TryGetHandPoint(out Vector3 point)
    {
        if (m_handView != null && m_handView.TryGetHandWorldPoint(m_fpStartDepth, out point, m_fpStartOffsetX))
            return true;

        Transform anchor = m_heldItemView != null ? m_heldItemView.HandAnchor : null;
        point = anchor != null ? anchor.position : transform.position + Vector3.up * m_fallbackHandHeight;
        return false;
    }

    private AnimationCurve GetFpWidthCurve()
    {
        float ratio = m_ropeWidth > 0.0001f ? Mathf.Clamp01(m_fpStartWidth / m_ropeWidth) : 0f;
        if (m_fpWidthCurve == null || !Mathf.Approximately(m_fpWidthCurveRatio, ratio))
        {
            m_fpWidthCurveRatio = ratio;
            m_fpWidthCurve = new AnimationCurve(
                new Keyframe(0f, ratio),
                new Keyframe(k_fpTaperFraction, 1f)
            );
        }
        return m_fpWidthCurve;
    }

    private void DrawRope(RopeVisual visual, Vector3 handPoint, Vector3 knotPoint, float ropeLength, bool taperStart)
    {
        LineRenderer line = visual.Line;
        float distance = Vector3.Distance(handPoint, knotPoint);
        float slack = 1f - Mathf.Clamp01(distance / Mathf.Max(0.01f, ropeLength));
        float sag = m_maxSag * slack;

        if (line.positionCount != m_segments + 1)
            line.positionCount = m_segments + 1;

        line.widthCurve = taperStart ? GetFpWidthCurve() : s_flatWidthCurve;

        for (int i = 0; i <= m_segments; i++)
        {
            float t = (float)i / m_segments;
            Vector3 point = Vector3.Lerp(handPoint, knotPoint, t);
            point.y -= sag * Mathf.Sin(t * Mathf.PI);
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
            Debug.LogWarning($"[RopeDragView] 밧줄 선 머티리얼이 없어 표시를 끈다. {name} 프리팹에 지정할 것", this);
            return null;
        }

        GameObject ropeObject = new GameObject("RopeLine");
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

        if (m_dustPrefab != null)
        {
            visual.Dust = Instantiate(m_dustPrefab);
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
