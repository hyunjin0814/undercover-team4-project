using UnityEngine;

/// <summary>
/// PlayerRagdoll의 임시 진단 계측 partial — 직렬화 토글로 켜고 끈다. 원인이 잡히면 파일째 삭제한다.
/// </summary>
public partial class PlayerRagdoll
{
    [Header("진단 (임시 — docs/player-ragdoll.md §13)")]
    [Tooltip("부활 순간의 yaw를 실측해 콘솔에 남긴다 — m_rootYawOffset을 맞추기 위한 계측이다. " +
             "읽는 법은 docs/player-ragdoll.md §13. 값이 확정되면 끈다")]
    [SerializeField] private bool m_logRevivalYaw;

    [Tooltip("정착 순간 <b>앞뒤 20프레임</b>을 한 줄씩 찍는다 — 정착할 때 몸이 아래로 내려갔다 " +
             "올라오는 현상을 잡는 계측이다. 읽는 법은 docs/player-ragdoll.md §13. 확정되면 끈다")]
    [SerializeField] private bool m_logSettleTrace;

    private Transform FindLiveBone(string boneName)
    {
        if (m_rig.BoneRoot == null)
            return null;

        Transform[] bones = m_rig.BoneRoot.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i].name == boneName)
                return bones[i];
        }

        return null;
    }

    /// <summary>부활 순간 시체 방향과 클립 방향의 yaw 차이를 로그로 남긴다.</summary>
    private void LogRevivalYaw(
        bool haveCorpseYaw,
        float corpseYaw,
        bool haveLiveBefore,
        float liveBefore
    )
    {
        float rootYaw = m_root.eulerAngles.y;

        if (!haveCorpseYaw)
        {
            Debug.Log(
                $"[래그돌 부활 yaw] 시체가 거의 수직이라 누운 방향을 못 쟀다 — {name} "
                    + $"(루트 {rootYaw:F1}°). 다시 눕혀서 죽여 볼 것",
                this
            );
            return;
        }

        if (!TryLiveBodyYaw(out float clipYaw))
        {
            Debug.Log(
                $"[래그돌 부활 yaw] 살아있는 리그에서 Hips/Head를 못 찾아 클립 방향을 못 쟀다 — {name}",
                this
            );
            return;
        }

        bool animatorWrote =
            !haveLiveBefore || Mathf.Abs(Mathf.DeltaAngle(liveBefore, clipYaw)) > 0.05f;

        float needed = Mathf.DeltaAngle(clipYaw, rootYaw);
        Debug.Log(
            $"[래그돌 부활 yaw] 시체 {corpseYaw:F1}° / 클립 {clipYaw:F1}° / 루트 {rootYaw:F1}° "
                + $"→ m_rootYawOffset = {needed:F1}° (현재 {m_rootYawOffset:F1}°, "
                + $"어긋남 {Mathf.DeltaAngle(clipYaw, corpseYaw):F1}°) "
                + $"| 권한={HasMoveAuthority} 애니메이터기록={animatorWrote} "
                + $"컬링={m_animator.cullingMode} 상태={m_state}",
            this
        );
    }

    private bool TryLiveBodyYaw(out float yaw)
    {
        yaw = 0f;
        if (m_rig.BoneRoot == null)
            return false;

        Transform hips = FindLiveBone("Hips");
        Transform head = FindLiveBone("Head");
        if (hips == null || head == null)
            return false;

        Vector3 lengthwise = head.position - hips.position;
        lengthwise.y = 0f;
        if (lengthwise.sqrMagnitude < 0.0004f)
            return false;

        yaw = Quaternion.LookRotation(lengthwise.normalized).eulerAngles.y;
        return true;
    }

    [Tooltip("래그돌 진입 직후 30프레임을 <b>루트 / 뼈 / 지면</b> 세 줄로 찍는다 — 쓰러지는 순간 몸이 " +
             "죽기 직전 자세에서 뜨고 돌아 버리는 현상을 잡는 계측이다.\n\n" +
             "⚠ <b>권한=True로 찍어야 한다</b> — 원격은 첫 패킷 전까지 자세가 못박혀 있어 아무것도 " +
             "움직이지 않는다.\n\n" +
             "각 칸을 읽는 법은 docs/player-ragdoll.md §13. 확정되면 끈다")]
    [SerializeField] private bool m_logEntryHeadTrace;

    private const int k_entryTraceFrames = 30;

    private static readonly string[] s_entryTraceBones = { "Hips", "Spine_01", "Neck", "Head" };

    private const int k_entryTraceHeadIndex = 3;

    private PlayerHeadLook m_headLook;

    private Transform[] m_liveTraceBones;
    private Transform[] m_corpseTraceBones;

    private int m_entryTraceLeft;
    private int m_entryFrame;

    private float[] m_lastBoneY;
    private float[] m_lastBoneYaw;
    private Quaternion m_lastHeadRotation;
    private float m_lastRootY;
    private float m_lastRootYaw;
    private bool m_haveLast;

    private float[] m_baselineBoneY;
    private float[] m_baselineBoneYaw;
    private Quaternion m_baselineHeadRotation;
    private float m_baselineRootY;
    private float m_baselineRootYaw;
    private bool m_haveBaseline;

    private Quaternion m_prevCorpseHead;
    private bool m_havePrevCorpseHead;

    private static Transform FindBone(Transform root, string name)
    {
        if (root == null)
            return null;

        if (root.name == name)
            return root;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindBone(root.GetChild(i), name);
            if (found != null)
                return found;
        }

        return null;
    }

    private void ResolveTraceBones()
    {
        if (m_liveTraceBones != null)
            return;

        m_liveTraceBones = new Transform[s_entryTraceBones.Length];
        m_corpseTraceBones = new Transform[s_entryTraceBones.Length];
        m_lastBoneY = new float[s_entryTraceBones.Length];
        m_lastBoneYaw = new float[s_entryTraceBones.Length];
        m_baselineBoneY = new float[s_entryTraceBones.Length];
        m_baselineBoneYaw = new float[s_entryTraceBones.Length];

        for (int i = 0; i < s_entryTraceBones.Length; i++)
        {
            m_liveTraceBones[i] = FindBone(m_rig.BoneRoot, s_entryTraceBones[i]);
            m_corpseTraceBones[i] = FindBone(m_rig.BoneRoot, s_entryTraceBones[i]);
        }
    }

    private void SampleEntryBaseline()
    {
        if (!m_logEntryHeadTrace)
            return;

        ResolveTraceBones();

        for (int i = 0; i < m_liveTraceBones.Length; i++)
        {
            Transform bone = m_liveTraceBones[i];
            if (bone == null)
                continue;

            m_lastBoneY[i] = bone.position.y;
            m_lastBoneYaw[i] = bone.eulerAngles.y;
        }

        Transform head = m_liveTraceBones[k_entryTraceHeadIndex];
        if (head != null)
            m_lastHeadRotation = head.rotation;

        m_lastRootY = m_root.position.y;
        m_lastRootYaw = m_root.eulerAngles.y;
        m_haveLast = true;
    }

    private void BeginEntryTrace()
    {
        if (!m_logEntryHeadTrace)
            return;

        ResolveTraceBones();
        m_headLook ??= GetComponentInParent<PlayerHeadLook>();

        m_entryFrame = Time.frameCount;
        m_entryTraceLeft = k_entryTraceFrames;
        m_havePrevCorpseHead = false;

        m_haveBaseline = m_haveLast;
        System.Array.Copy(m_lastBoneY, m_baselineBoneY, m_lastBoneY.Length);
        System.Array.Copy(m_lastBoneYaw, m_baselineBoneYaw, m_lastBoneYaw.Length);
        m_baselineHeadRotation = m_lastHeadRotation;
        m_baselineRootY = m_lastRootY;
        m_baselineRootYaw = m_lastRootYaw;

        float tilt = m_headLook != null ? m_headLook.LastAppliedTilt : float.NaN;

        Debug.Log(
            $"[진입추적] ===== 시체 켬 (권한={HasMoveAuthority} 프레임={m_entryFrame}) — 시선={tilt:F1}° "
                + $"기준선={m_haveBaseline} 기준루트Y={m_baselineRootY:F3} "
                + $"기준루트yaw={m_baselineRootYaw:F1}° / 복사직후 ↓ =====",
            this
        );

        LogEntrySample();
    }

    private void TickEntryTrace()
    {
        if (m_entryTraceLeft <= 0)
            return;

        m_entryTraceLeft--;
        LogEntrySample();
    }

    private void LogEntrySample()
    {
        if (m_corpseTraceBones == null)
            return;

        string frame = (Time.frameCount - m_entryFrame).ToString("+0;-0;0");

        float rootLift = m_haveBaseline ? m_root.position.y - m_baselineRootY : float.NaN;
        float rootYawDelta = m_haveBaseline
            ? Mathf.DeltaAngle(m_baselineRootYaw, m_root.eulerAngles.y)
            : float.NaN;

        float bodyYaw = float.NaN;
        bool haveBodyYaw = false;
        if (m_rig != null && m_rig.TryGetBodyYaw(out float measuredYaw))
        {
            haveBodyYaw = true;
            bodyYaw = measuredYaw;
        }

        Transform corpseHips = m_corpseTraceBones[0];
        Transform corpseHead = m_corpseTraceBones[k_entryTraceHeadIndex];
        float horizontalCm = float.NaN;
        if (corpseHips != null && corpseHead != null)
        {
            Vector3 lengthwise = corpseHead.position - corpseHips.position;
            lengthwise.y = 0f;
            horizontalCm = lengthwise.magnitude * 100f;
        }

        Debug.Log(
            $"[진입루트] 권한={HasMoveAuthority} f={frame} 루트뜸={rootLift:+0.000;-0.000;0.000}m "
                + $"루트yawΔ={rootYawDelta:+0.0;-0.0;0.0}° 몸yaw={bodyYaw:F1}° 수평={horizontalCm:F1}cm "
                + $"유효={haveBodyYaw} 상태={m_state}",
            this
        );

        var bones = new System.Text.StringBuilder();
        for (int i = 0; i < m_corpseTraceBones.Length; i++)
        {
            Transform bone = m_corpseTraceBones[i];
            if (bone == null)
                continue;

            float lift = m_haveBaseline ? bone.position.y - m_baselineBoneY[i] : float.NaN;
            float yawDelta = m_haveBaseline
                ? Mathf.DeltaAngle(m_baselineBoneYaw[i], bone.eulerAngles.y)
                : float.NaN;

            bones.Append(
                $"{s_entryTraceBones[i]}={lift:+0.000;-0.000;0.000}m/{yawDelta:+0.0;-0.0;0.0}° "
            );
        }

        Transform liveHips = m_liveTraceBones[0];
        float rigSpan =
            liveHips != null && corpseHips != null
                ? (liveHips.position - corpseHips.position).magnitude
                : float.NaN;

        float pop =
            m_haveBaseline && corpseHead != null
                ? Quaternion.Angle(m_baselineHeadRotation, corpseHead.rotation)
                : float.NaN;

        float step = 0f;
        if (corpseHead != null)
        {
            step = m_havePrevCorpseHead
                ? Quaternion.Angle(m_prevCorpseHead, corpseHead.rotation)
                : 0f;
            m_prevCorpseHead = corpseHead.rotation;
            m_havePrevCorpseHead = true;
        }

        Debug.Log(
            $"[진입뼈] 권한={HasMoveAuthority} f={frame} {bones}골반간격={rigSpan:F3}m "
                + $"팝={pop:F1}° 단차={step:F1}°",
            this
        );

        float groundY = float.NaN;
        bool haveGround = false;
        if (corpseHips != null && TryGroundUnder(corpseHips.position, out Vector3 groundPoint))
        {
            haveGround = true;
            groundY = groundPoint.y;
        }

        float lowestAbove = haveGround && m_rig != null ? m_rig.LowestBoneY - groundY : float.NaN;
        float hipsAbove = haveGround && corpseHips != null
            ? corpseHips.position.y - groundY
            : float.NaN;
        float rootAbove = haveGround ? m_root.position.y - groundY : float.NaN;

        Debug.Log(
            $"[진입지면] 권한={HasMoveAuthority} f={frame} 지면Y={groundY:F3} 찾음={haveGround} "
                + $"최저뼈-지면={lowestAbove:+0.000;-0.000;0.000}m "
                + $"골반-지면={hipsAbove:F3}m 루트-지면={rootAbove:+0.000;-0.000;0.000}m",
            this
        );
    }

    private const int k_settleTraceFrames = 20;

    private struct SettleTraceSample
    {
        public int Frame;
        public float RootY;
        public float HipsWorldY;
        public float HipsLocalY;
        public bool Driven;
        public RagdollState State;
    }

    private SettleTraceSample[] m_trace;
    private int m_traceHead;
    private int m_traceFilled;
    private int m_traceAfter;
    private int m_traceSettleFrame;

    private void TickSettleTrace()
    {
        if (!m_logSettleTrace || m_rig == null || !m_rig.IsValid || m_rig.Hips == null)
            return;

        if (m_state == RagdollState.Animated)
            return;

        if (m_trace == null || m_trace.Length != k_settleTraceFrames)
        {
            m_trace = new SettleTraceSample[k_settleTraceFrames];
            m_traceHead = 0;
            m_traceFilled = 0;
        }

        SettleTraceSample sample = new SettleTraceSample
        {
            Frame = Time.frameCount,
            RootY = m_root != null ? m_root.position.y : float.NaN,
            HipsWorldY = m_rig.Hips.position.y,
            HipsLocalY = m_rig.Hips.localPosition.y,
            Driven = m_streamer != null && m_streamer.IsStreamDriven,
            State = m_state,
        };

        m_trace[m_traceHead] = sample;
        m_traceHead = (m_traceHead + 1) % k_settleTraceFrames;
        if (m_traceFilled < k_settleTraceFrames)
            m_traceFilled++;

        if (m_traceAfter > 0)
        {
            m_traceAfter--;
            LogTraceSample(sample);
        }
    }

    private void DumpSettleTrace()
    {
        if (!m_logSettleTrace)
            return;

        m_traceSettleFrame = Time.frameCount;
        Debug.Log(
            $"[정착추적] ===== 정착 (권한={HasMoveAuthority} 프레임={m_traceSettleFrame}) — "
                + $"이전 {m_traceFilled}프레임 ↓ =====",
            this
        );

        int start = (m_traceHead - m_traceFilled + k_settleTraceFrames) % k_settleTraceFrames;
        for (int i = 0; i < m_traceFilled; i++)
            LogTraceSample(m_trace[(start + i) % k_settleTraceFrames]);

        m_traceAfter = k_settleTraceFrames;
    }

    private void LogTraceSample(SettleTraceSample sample)
    {
        Debug.Log(
            $"[정착추적] 권한={HasMoveAuthority} f={sample.Frame - m_traceSettleFrame:+0;-0;0} "
                + $"루트Y={sample.RootY:F3} 골반월드Y={sample.HipsWorldY:F3} "
                + $"골반로컬Y={sample.HipsLocalY:F3} 스트림={sample.Driven} 상태={sample.State}",
            this
        );
    }
}