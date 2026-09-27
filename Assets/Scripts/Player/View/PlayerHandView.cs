using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 1인칭 손·장착 아이템 표시 — 장착 변경에 맞춰 손 앵커에 모델을 갈아 끼우고 흔들림·스윙을 절차적으로 준다.
/// 오너 로컬 전용이다.
/// </summary>
[RequireComponent(typeof(PlayerItemUser))]
public class PlayerHandView : NetworkBehaviour
{
    [Header("손 앵커 (비우면 카메라 하위에 자동 생성)")]
    [SerializeField]
    private Transform m_handAnchor;

    [Header("1인칭 손 모델 (선택)")]
    [Tooltip("카메라 하위에 배치할 것. PlayerLook의 OwnBody 루트 바깥에 둬야 내 카메라에 보인다")]
    [SerializeField]
    private GameObject m_handsModel;

    private static readonly Vector3 s_defaultAnchorOffset = new Vector3(0.4f, -0.2f, 0.5f);

    private const float k_bobRefSpeed = 5f;
    private const float k_idleFreq = 1.6f;
    private const float k_walkFreq = 9f;
    private const float k_idleAmp = 0.004f;
    private const float k_walkAmp = 0.010f;
    private const float k_swayTiltDegrees = 90f;
    private const float k_bobSpeedDamp = 12f;

    private const float k_swingDuration = PlayerAnimationDriver.k_swingSeconds;
    private const float k_swingStrikeEnd =
        PlayerAnimationDriver.k_swingImpactSeconds / PlayerAnimationDriver.k_swingSeconds;

    private const float k_hitShakeDuration = 0.22f;
    private const float k_hitShakeDegrees = 7f;
    private const float k_hitShakeOscillations = 1.5f;
    private const float k_hitShakeOffset = 0.02f;

    private const float k_convulsionDegrees = 3.2f;
    private const float k_convulsionOffset = 0.006f;

    [Header("스윙 포즈 — 준비 (#217)")]
    [Tooltip("오른쪽 위로 세워 젖히는 자세. 카메라 축 기준 회전(도)")]
    [SerializeField]
    private Vector3 m_swingWindupEuler = new Vector3(-32f, 3f, -24f);

    [Tooltip("몸쪽으로 당기는 위치 오프셋(m)")]
    [SerializeField]
    private Vector3 m_swingWindupOffset = new Vector3(0.015f, 0.06f, -0.05f);

    [Header("스윙 포즈 — 임팩트")]
    [Tooltip("우상 → 좌하 대각 내려치기. 맞는 순간의 자세다")]
    [SerializeField]
    private Vector3 m_swingStrikeEuler = new Vector3(8f, -18f, 46f);

    [Tooltip("앞으로 뻗으며 치는 위치 오프셋(m)")]
    [SerializeField]
    private Vector3 m_swingStrikeOffset = new Vector3(-0.035f, -0.025f, 0.085f);

    [Header("스윙 포즈 — 팔로스루")]
    [Tooltip("임팩트 뒤 흘러나가는 자세. X(아래)보다 Y·롤로 흘릴 것 — 아래로 파면 손이 화면에서 빠진다")]
    [SerializeField]
    private Vector3 m_swingFollowEuler = new Vector3(5f, -24f, 64f);

    [Tooltip("힘이 빠지며 옆으로 흘리는 위치 오프셋(m)")]
    [SerializeField]
    private Vector3 m_swingFollowOffset = new Vector3(-0.05f, 0.01f, 0.04f);

    [Header("스윙 구간 비율")]
    [Tooltip("임팩트까지의 시간 중 준비 동작이 차지하는 비율. 작을수록 늦게 젖혔다 급히 친다")]
    [SerializeField]
    [Range(0.15f, 0.85f)]
    private float m_swingWindupFraction = 0.62f;

    [Tooltip("임팩트 이후 남은 시간 중 팔로스루가 차지하는 비율. 나머지는 기본 자세로 회수")]
    [SerializeField]
    [Range(0.05f, 0.9f)]
    private float m_swingFollowFraction = 0.3f;

    [Header("스윙 가속 곡선")]
    [Tooltip("준비 — 빠르게 젖혔다 멎는다(감속으로 끝날 것)")]
    [SerializeField]
    private AnimationCurve m_swingWindupCurve = new AnimationCurve(
        new Keyframe(0f, 0f, 2f, 2f),
        new Keyframe(1f, 1f, 0f, 0f)
    );

    [Tooltip("내려침 — 임팩트가 최고 속도가 되도록 가속으로 끝낼 것. 여기를 S자로 바꾸면 타격감이 죽는다")]
    [SerializeField]
    private AnimationCurve m_swingStrikeCurve = new AnimationCurve(
        new Keyframe(0f, 0f, 0f, 0f),
        new Keyframe(1f, 1f, 2f, 2f)
    );

    [Tooltip("팔로스루 — 최고 속도로 이어받아 힘이 풀리며 멎는다")]
    [SerializeField]
    private AnimationCurve m_swingFollowCurve = new AnimationCurve(
        new Keyframe(0f, 0f, 2f, 2f),
        new Keyframe(1f, 1f, 0f, 0f)
    );

    [Tooltip("회수 — 기본 자세로 조용히 복귀. 여기서 튀면 다음 스윙이 지저분해진다")]
    [SerializeField]
    private AnimationCurve m_swingRecoverCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    private PlayerItemUser m_itemUser;
    private GameObject m_heldModelInstance;

    private Camera m_worldCamera;
    private Camera m_viewmodelCamera;

    private enum FingerKind { Finger, Index, Thumb }

    private struct FingerJoint
    {
        public Transform Bone;
        public Quaternion BaseRotation;
        public FingerKind Kind;
        public int Depth;
    }

    private FingerJoint[] m_fingerJoints;

    private static readonly float[] s_fingerDepthWeights = { 1f, 0.75f, 0.5f };

    private static readonly float[] s_thumbDepthWeights = { 0.25f, 0.9f, 0.7f };

    private const float k_indexAimMaxDegrees = 60f;
    private const float k_indexAimStepDegrees = 2.5f;

    private CharacterController m_controller;
    private Vector3 m_handBasePos;
    private Quaternion m_handBaseRot;
    private bool m_hasHandBase;
    private float m_bobTime;
    private float m_bobSpeedT;
    private float m_swingTime = -1f;
    private float m_hitShakeTime = -1f;
    private Vector3 m_hitShakeAxis;
    private float m_convulsionIntensity;

    public override void OnNetworkSpawn()
    {
        m_itemUser = GetComponent<PlayerItemUser>();

        if (!IsOwner)
        {
            if (m_handsModel != null)
            {
                m_handsModel.SetActive(false);
            }
            enabled = false;
            return;
        }

        m_controller = GetComponent<CharacterController>();

        CacheCameras();

        SetupHandViewmodel();
        EnsureHandAnchor();

        m_itemUser.OnEquippedItemChanged += RefreshHeldModel;
        RefreshHeldModel(m_itemUser.EquippedItem);
    }

    private void SetupHandViewmodel()
    {
        if (m_handsModel == null)
        {
            return;
        }

        m_handsModel.SetActive(true);
        m_handBasePos = m_handsModel.transform.localPosition;
        m_handBaseRot = m_handsModel.transform.localRotation;
        m_hasHandBase = true;

        int viewmodelLayer = LayerMask.NameToLayer("Viewmodel");
        if (viewmodelLayer >= 0)
        {
            PlayerLook.SetLayerRecursively(m_handsModel.transform, viewmodelLayer);
        }

        if (m_handAnchor == null)
        {
            m_handAnchor = FindChildByName(m_handsModel.transform, "HeldItemAnchor");
        }

        CacheFingerJoints();
    }

    private void CacheCameras()
    {
        int viewmodelLayer = LayerMask.NameToLayer("Viewmodel");
        if (viewmodelLayer < 0)
        {
            return;
        }

        int viewmodelBit = 1 << viewmodelLayer;
        foreach (Camera cam in GetComponentsInChildren<Camera>(true))
        {
            if ((cam.cullingMask & viewmodelBit) != 0)
                m_viewmodelCamera = cam;
            else
                m_worldCamera = cam;
        }
    }

    public bool TryGetHandWorldPoint(float depth, out Vector3 point, float viewportOffsetX = 0f)
    {
        point = default;

        if (!enabled || m_handAnchor == null || m_worldCamera == null)
        {
            return false;
        }

        if (m_handsModel != null && !m_handsModel.activeInHierarchy)
        {
            return false;
        }

        Camera viewCamera = m_viewmodelCamera != null ? m_viewmodelCamera : m_worldCamera;
        Vector3 viewport = viewCamera.WorldToViewportPoint(m_handAnchor.position);
        viewport.x += viewportOffsetX;
        viewport.z = depth > 0f ? depth : viewport.z;
        point = m_worldCamera.ViewportToWorldPoint(viewport);
        return true;
    }

    public override void OnNetworkDespawn()
    {
        if (m_itemUser != null)
        {
            m_itemUser.OnEquippedItemChanged -= RefreshHeldModel;
        }
    }

    public void SetViewmodelVisible(bool visible)
    {
        if (!enabled || m_handsModel == null)
        {
            return;
        }

        m_handsModel.SetActive(visible);
    }

    public void PlaySwing()
    {
        if (!enabled || m_handsModel == null)
        {
            return;
        }

        m_swingTime = 0f;
    }

    public void PlayHitShake()
    {
        if (!enabled || m_handsModel == null)
        {
            return;
        }

        m_hitShakeAxis = new Vector3(
            Random.Range(-1f, 1f),
            Random.Range(-1f, 1f),
            Random.Range(-1f, 1f) * 1.5f
        ).normalized;

        m_hitShakeTime = 0f;
    }

    public void SetConvulsion(float intensity) =>
        m_convulsionIntensity = Mathf.Clamp01(intensity);

    private void Update()
    {
        if (!m_hasHandBase || m_handsModel == null)
        {
            return;
        }

        float targetSpeedT = 0f;
        if (m_controller != null && m_controller.enabled)
        {
            Vector3 horizontalVelocity = m_controller.velocity;
            horizontalVelocity.y = 0f;
            targetSpeedT = Mathf.Clamp01(horizontalVelocity.magnitude / k_bobRefSpeed);
        }

        m_bobSpeedT = Mathf.Lerp(
            m_bobSpeedT, targetSpeedT, 1f - Mathf.Exp(-k_bobSpeedDamp * Time.deltaTime));

        m_bobTime += Time.deltaTime * (k_idleFreq + m_bobSpeedT * k_walkFreq);
        float amp = k_idleAmp + m_bobSpeedT * k_walkAmp;

        float x = Mathf.Cos(m_bobTime) * amp;
        float y = Mathf.Sin(m_bobTime * 2f) * amp;

        AdvanceSwingTime();
        EvaluateSwingPose(out Vector3 swingOffset, out Quaternion swingRotation);

        AdvanceHitShakeTime();
        EvaluateHitShakePose(out Vector3 hitOffset, out Quaternion hitRotation);

        EvaluateConvulsionPose(out Vector3 shockOffset, out Quaternion shockRotation);

        m_handsModel.transform.localPosition =
            m_handBasePos + new Vector3(x, y, 0f) + swingOffset + hitOffset + shockOffset;
        m_handsModel.transform.localRotation =
            shockRotation
            * hitRotation
            * swingRotation
            * m_handBaseRot
            * Quaternion.Euler(y * k_swayTiltDegrees, x * k_swayTiltDegrees, 0f);
    }

    private void EvaluateConvulsionPose(out Vector3 offset, out Quaternion rotation)
    {
        ShockShake.Evaluate(
            m_convulsionIntensity,
            k_convulsionDegrees,
            k_convulsionOffset,
            out Vector3 euler,
            out offset
        );
        rotation = Quaternion.Euler(euler);
    }

    private void AdvanceHitShakeTime()
    {
        if (m_hitShakeTime < 0f)
        {
            return;
        }

        m_hitShakeTime += Time.deltaTime;
        if (m_hitShakeTime >= k_hitShakeDuration)
        {
            m_hitShakeTime = -1f;
        }
    }

    private void EvaluateHitShakePose(out Vector3 offset, out Quaternion rotation)
    {
        offset = Vector3.zero;
        rotation = Quaternion.identity;

        if (m_hitShakeTime < 0f)
        {
            return;
        }

        float t = m_hitShakeTime / k_hitShakeDuration;
        float wave = Mathf.Sin(t * Mathf.PI * 2f * k_hitShakeOscillations) * (1f - t);

        rotation = Quaternion.Euler(m_hitShakeAxis * (wave * k_hitShakeDegrees));
        offset = m_hitShakeAxis * (wave * k_hitShakeOffset);
    }

    private void AdvanceSwingTime()
    {
        if (m_swingTime < 0f)
        {
            return;
        }

        m_swingTime += Time.deltaTime;
        if (m_swingTime >= k_swingDuration)
        {
            m_swingTime = -1f;
        }
    }

    private void EvaluateSwingPose(out Vector3 offset, out Quaternion rotation)
    {
        offset = Vector3.zero;
        rotation = Quaternion.identity;

        if (m_swingTime < 0f)
        {
            return;
        }

        float t = m_swingTime / k_swingDuration;

        float windupEnd = k_swingStrikeEnd * m_swingWindupFraction;
        float followEnd = k_swingStrikeEnd + (1f - k_swingStrikeEnd) * m_swingFollowFraction;

        Vector3 euler;
        if (t < windupEnd)
        {
            float u = Ease(m_swingWindupCurve, t / windupEnd);
            euler = Vector3.Lerp(Vector3.zero, m_swingWindupEuler, u);
            offset = Vector3.Lerp(Vector3.zero, m_swingWindupOffset, u);
        }
        else if (t < k_swingStrikeEnd)
        {
            float u = Ease(m_swingStrikeCurve, (t - windupEnd) / (k_swingStrikeEnd - windupEnd));
            euler = Vector3.Lerp(m_swingWindupEuler, m_swingStrikeEuler, u);
            offset = Vector3.Lerp(m_swingWindupOffset, m_swingStrikeOffset, u);
        }
        else if (t < followEnd)
        {
            float u = Ease(m_swingFollowCurve, (t - k_swingStrikeEnd) / (followEnd - k_swingStrikeEnd));
            euler = Vector3.Lerp(m_swingStrikeEuler, m_swingFollowEuler, u);
            offset = Vector3.Lerp(m_swingStrikeOffset, m_swingFollowOffset, u);
        }
        else
        {
            float u = Ease(m_swingRecoverCurve, (t - followEnd) / (1f - followEnd));
            euler = Vector3.Lerp(m_swingFollowEuler, Vector3.zero, u);
            offset = Vector3.Lerp(m_swingFollowOffset, Vector3.zero, u);
        }

        rotation = Quaternion.Euler(euler);
    }

    private static float Ease(AnimationCurve curve, float u)
    {
        return curve != null && curve.length >= 2 ? curve.Evaluate(u) : u;
    }

    private void EnsureHandAnchor()
    {
        if (m_handAnchor != null)
        {
            return;
        }

        Camera playerCamera = GetComponentInChildren<Camera>(true);
        Transform anchorParent = playerCamera != null ? playerCamera.transform : transform;

        GameObject anchor = new GameObject("HandAnchor");
        m_handAnchor = anchor.transform;
        m_handAnchor.SetParent(anchorParent, false);
        m_handAnchor.localPosition = s_defaultAnchorOffset;
    }

    private void RefreshHeldModel(ItemBase item)
    {
        if (m_heldModelInstance != null)
        {
            Destroy(m_heldModelInstance);
            m_heldModelInstance = null;
        }

        ApplyGrip(item != null ? item.HandGrip : HandGrip.Relaxed);

        if (item == null || item.HeldModelPrefab == null)
        {
            return;
        }

        m_heldModelInstance = Instantiate(item.HeldModelPrefab, m_handAnchor, false);

        m_heldModelInstance.transform.localPosition = item.HeldPositionOffset;
        m_heldModelInstance.transform.localRotation = Quaternion.Euler(item.HeldRotationOffset);

        if (item.HandGrip == HandGrip.Trigger)
        {
            AimIndexAtTrigger(m_heldModelInstance);
        }

        foreach (Collider heldCollider in m_heldModelInstance.GetComponentsInChildren<Collider>(true))
        {
            Destroy(heldCollider);
        }

        int viewmodelLayer = LayerMask.NameToLayer("Viewmodel");
        if (viewmodelLayer >= 0)
        {
            PlayerLook.SetLayerRecursively(m_heldModelInstance.transform, viewmodelLayer);
        }
    }

    private static Transform FindChildByName(Transform root, string childName)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == childName)
            {
                return t;
            }
        }
        return null;
    }

    private void CacheFingerJoints()
    {
        var joints = new System.Collections.Generic.List<FingerJoint>();
        if (m_handsModel != null)
        {
            Transform hand = FindChildByName(m_handsModel.transform, "Hand_R");

            if (hand != null)
            {
                foreach (Transform chainRoot in hand)
                {
                    FingerKind kind;
                    if (chainRoot.name.StartsWith("Thumb")) kind = FingerKind.Thumb;
                    else if (chainRoot.name.StartsWith("Index")) kind = FingerKind.Index;
                    else if (chainRoot.name.StartsWith("Finger")) kind = FingerKind.Finger;
                    else continue;

                    Transform seg = chainRoot;
                    for (int depth = 0; seg != null && depth < 3; depth++)
                    {
                        joints.Add(new FingerJoint
                        {
                            Bone = seg,
                            BaseRotation = seg.localRotation,
                            Kind = kind,
                            Depth = depth,
                        });
                        seg = seg.childCount > 0 ? seg.GetChild(0) : null;
                    }
                }
            }
        }
        m_fingerJoints = joints.ToArray();
    }

    private void ApplyGrip(HandGrip grip)
    {
        if (m_fingerJoints == null) return;
        foreach (FingerJoint j in m_fingerJoints)
        {
            if (j.Bone == null) continue;
            j.Bone.localRotation = j.BaseRotation * Quaternion.Euler(CurlEuler(grip, j.Kind, j.Depth));
        }
    }

    private void AimIndexAtTrigger(GameObject heldModel)
    {
        if (m_fingerJoints == null || heldModel == null)
        {
            return;
        }

        Renderer trigger = null;
        foreach (Renderer r in heldModel.GetComponentsInChildren<Renderer>(true))
        {
            if (r.name.IndexOf("Trigger", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                trigger = r;
                break;
            }
        }
        if (trigger == null)
        {
            return;
        }

        Transform[] bones = new Transform[s_fingerDepthWeights.Length];
        Quaternion[] baseRotations = new Quaternion[bones.Length];
        int count = 0;
        foreach (FingerJoint j in m_fingerJoints)
        {
            if (j.Kind != FingerKind.Index || j.Bone == null || count >= bones.Length)
            {
                continue;
            }
            bones[count] = j.Bone;
            baseRotations[count] = j.BaseRotation;
            count++;
        }
        if (count < bones.Length)
        {
            return;
        }

        Transform lastJoint = bones[bones.Length - 1];
        Transform tip = lastJoint.childCount > 0 ? lastJoint.GetChild(0) : lastJoint;
        Vector3 target = trigger.bounds.center;

        float bestCurl = 0f;
        float bestDistance = float.MaxValue;
        for (float curl = 0f; curl <= k_indexAimMaxDegrees; curl += k_indexAimStepDegrees)
        {
            ApplyIndexCurl(bones, baseRotations, curl);
            float distance = DistanceToSegment(lastJoint.position, tip.position, target);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestCurl = curl;
            }
        }
        ApplyIndexCurl(bones, baseRotations, bestCurl);
    }

    private static void ApplyIndexCurl(Transform[] bones, Quaternion[] baseRotations, float curl)
    {
        for (int i = 0; i < bones.Length; i++)
        {
            bones[i].localRotation =
                baseRotations[i] * Quaternion.Euler(0f, 0f, curl * s_fingerDepthWeights[i]);
        }
    }

    private static float DistanceToSegment(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = b - a;
        float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-8f));
        return Vector3.Distance(a + ab * t, p);
    }

    private static Vector3 CurlEuler(HandGrip grip, FingerKind kind, int depth)
    {
        bool isThumb = kind == FingerKind.Thumb;

        float curl;
        float spread = 0f;

        switch (grip)
        {
            case HandGrip.Trigger:
                curl = isThumb ? 35f : (kind == FingerKind.Index ? 20f : 65f);
                if (isThumb) spread = 20f;
                break;
            case HandGrip.Wide:
                curl = isThumb ? 6f : 10f;
                if (isThumb) spread = 5f;
                break;
            case HandGrip.Handle:
                curl = isThumb ? 52f : 70f;
                if (isThumb) spread = -6f;
                break;
            default:
                curl = isThumb ? 18f : 26f;
                if (isThumb) spread = 3f;
                break;
        }

        float[] weights = isThumb ? s_thumbDepthWeights : s_fingerDepthWeights;
        float w = depth < weights.Length ? weights[depth] : 0f;
        curl *= w;
        spread *= w;

        return isThumb ? new Vector3(0f, -curl, spread) : new Vector3(0f, spread, curl);
    }
}
