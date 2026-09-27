using UnityEngine;

/// <summary>
/// 벽에 박힌 래그돌 팔을 몸쪽으로 회전시켜 접어서 빼낸다.
/// 접는 동안 그 팔만 키네마틱으로 두고, 빠져나오는 즉시 멈춘다. 권위 피어에서만 호출한다.
/// </summary>
public class RagdollArmFold
{
    private const float k_probeClearance = 0.02f;

    private const int k_holdSteps = 2;

    private const int k_maxFoldBones = 4;

    public readonly struct Tuning
    {
        public readonly float MinPastSurface;
        public readonly float FoldSeconds;
        public readonly float LimbMassMax;
        public readonly int MaxAttempts;
        public readonly float WindowSeconds;
        public readonly int ProbeStepsMoving;
        public readonly int ProbeStepsSettled;
        public readonly int ConfirmProbes;

        public Tuning(
            float minPastSurface,
            float foldSeconds,
            float limbMassMax,
            int maxAttempts,
            float windowSeconds,
            int probeStepsMoving,
            int probeStepsSettled,
            int confirmProbes
        )
        {
            MinPastSurface = minPastSurface;
            FoldSeconds = foldSeconds;
            LimbMassMax = limbMassMax;
            MaxAttempts = maxAttempts;
            WindowSeconds = windowSeconds;
            ProbeStepsMoving = probeStepsMoving;
            ProbeStepsSettled = probeStepsSettled;
            ConfirmProbes = confirmProbes;
        }
    }

    private readonly RagdollRig m_rig;

    private readonly System.Func<Collider, bool> m_isCharacter;

    private readonly int[] m_arms = new int[8];
    private readonly int[] m_fold = new int[k_maxFoldBones];

    private bool m_open;
    private float m_windowElapsed;
    private int m_attempt;

    private int m_probeCountdown;
    private int m_pendingBone = -1;
    private int m_confirmCount;

    private int m_boneIndex = -1;
    private int m_foldCount;
    private float m_foldElapsed;
    private int m_holdLeft;
    private float m_startPastSurface;

    public string Label { get; set; } = "?";

    public bool IsFolding => m_foldCount > 0;

    public bool IsWindowOpen => m_open;

    public RagdollArmFold(RagdollRig rig, System.Func<Collider, bool> isCharacter)
    {
        m_rig = rig;
        m_isCharacter = isCharacter;
    }

    /// <summary>래그돌 진입 — 창을 열고 카운터를 되돌린다.</summary>
    public void Begin()
    {
        Release();
        m_open = m_rig != null && m_rig.IsValid;
        m_windowElapsed = 0f;
        m_attempt = 0;
        m_probeCountdown = 1;
        m_pendingBone = -1;
        m_confirmCount = 0;
    }

    /// <summary>래그돌 이탈 — 접다 말았으면 물리로 돌려주고 창을 닫는다.</summary>
    public void End()
    {
        Release();
        m_open = false;
    }

    /// <summary>래그돌 진입 프레임에 벽 안에 있는 팔을 한 번에 접는다(물리로 넘기기 전).</summary>
    public bool FoldOnEntry(in Tuning tuning)
    {
        if (m_rig == null || !m_rig.IsValid || m_rig.Hips == null)
            return false;

        int bone = FindStuckArm(tuning, out RagdollWallProbe.Pin pin);
        if (bone < 0)
            return false;

        m_foldCount = CollectFoldChain(bone, tuning);
        if (m_foldCount <= 0)
            return false;

        FoldStep(1f);

        Debug.Log(
            $"[래그돌팔접기] {Label} 진입 예방 — 뼈={m_rig.Bones.GetName(bone)} 벽={pin.Wall.name} "
                + $"벽면 너머 {pin.PastSurface * 100f:F1}cm였다. 접은 채로 물리에 넘긴다"
        );

        m_foldCount = 0;
        return true;
    }

    /// <summary>한 물리 스텝. <c>FixedUpdate</c>의 마지막에서만 부른다.</summary>
    public bool Tick(bool settled, in Tuning tuning)
    {
        if (!m_open || m_rig == null || !m_rig.IsValid || m_rig.Hips == null)
            return false;

        m_windowElapsed += Time.fixedDeltaTime;
        if (m_windowElapsed > tuning.WindowSeconds)
        {
            End();
            return false;
        }

        if (m_foldCount > 0)
            return TickFold(tuning);

        if (--m_probeCountdown > 0)
            return false;

        m_probeCountdown = settled ? tuning.ProbeStepsSettled : tuning.ProbeStepsMoving;
        return TickProbe(tuning);
    }

    private bool TickProbe(in Tuning tuning)
    {
        int bone = FindStuckArm(tuning, out RagdollWallProbe.Pin pin);

        if (bone < 0)
        {
            m_pendingBone = -1;
            m_confirmCount = 0;
            return false;
        }

        if (bone != m_pendingBone)
        {
            m_pendingBone = bone;
            m_confirmCount = 1;
            return false;
        }

        if (++m_confirmCount < tuning.ConfirmProbes)
            return false;

        m_pendingBone = -1;
        m_confirmCount = 0;

        m_foldCount = CollectFoldChain(bone, tuning);
        if (m_foldCount <= 0)
            return false;

        m_attempt++;
        m_boneIndex = bone;
        m_foldElapsed = 0f;
        m_holdLeft = 0;
        m_startPastSurface = pin.PastSurface;

        for (int i = 0; i < m_foldCount; i++)
            m_rig.SetBoneKinematic(m_fold[i], true);

        Debug.Log(
            $"[래그돌팔접기] {Label} 접기 시작 — 뼈={m_rig.Bones.GetName(bone)} 벽={pin.Wall.name} "
                + $"벽면 너머 {pin.PastSurface * 100f:F1}cm, 대상 {m_foldCount}뼈, "
                + $"시도 {m_attempt}/{tuning.MaxAttempts}"
        );

        return true;
    }

    private int FindStuckArm(in Tuning tuning, out RagdollWallProbe.Pin worstPin)
    {
        worstPin = default;

        int armCount = m_rig.Bones.CollectArmBones(tuning.LimbMassMax, m_arms);
        Vector3 hips = m_rig.Hips.position;

        int worst = -1;
        for (int i = 0; i < armCount; i++)
        {
            if (
                !RagdollWallProbe.TryFindPinningWall(
                    hips,
                    m_rig.Bones.GetCollider(m_arms[i]),
                    k_probeClearance,
                    m_isCharacter,
                    out RagdollWallProbe.Pin pin
                )
            )
                continue;

            if (pin.PastSurface <= tuning.MinPastSurface)
                continue;

            if (worst < 0 || pin.PastSurface > worstPin.PastSurface)
            {
                worst = m_arms[i];
                worstPin = pin;
            }
        }

        return worst;
    }

    private bool IsPinned(int bone, in Tuning tuning, out float pastSurface)
    {
        pastSurface = 0f;

        if (
            !RagdollWallProbe.TryFindPinningWall(
                m_rig.Hips.position,
                m_rig.Bones.GetCollider(bone),
                k_probeClearance,
                m_isCharacter,
                out RagdollWallProbe.Pin pin
            )
        )
            return false;

        pastSurface = pin.PastSurface;
        return pastSurface > tuning.MinPastSurface;
    }

    private int CollectFoldChain(int bone, in Tuning tuning)
    {
        int count = m_rig.Bones.CollectChainUpward(bone, k_maxFoldBones - 1, tuning.LimbMassMax, m_fold);

        for (int i = 0; i < count / 2; i++)
        {
            (m_fold[i], m_fold[count - 1 - i]) = (m_fold[count - 1 - i], m_fold[i]);
        }

        return count;
    }

    private bool TickFold(in Tuning tuning)
    {
        m_rig.WakeAll();

        if (m_holdLeft > 0)
        {
            if (--m_holdLeft <= 0)
                Release();

            return true;
        }

        m_foldElapsed += Time.fixedDeltaTime;

        float step =
            tuning.FoldSeconds > 0.0001f ? Time.fixedDeltaTime / tuning.FoldSeconds : 1f;
        FoldStep(step);

        bool pinned = IsPinned(m_boneIndex, tuning, out float pastSurface);

        if (!pinned)
        {
            Debug.Log(
                $"[래그돌팔접기] {Label} 빠져나옴 — 뼈={m_rig.Bones.GetName(m_boneIndex)} "
                    + $"벽면 너머 {m_startPastSurface * 100f:F1}cm → 0 ({m_foldElapsed:F2}s)"
            );
            m_holdLeft = k_holdSteps;
            return true;
        }

        if (m_foldElapsed < tuning.FoldSeconds)
            return true;

        if (m_attempt >= tuning.MaxAttempts)
        {
            Debug.LogWarning(
                $"[래그돌팔접기] {Label} {m_attempt}회 실패 — 팔이 벽에 박힌 채 남는다. "
                    + $"뼈={m_rig.Bones.GetName(m_boneIndex)} 벽면 너머 "
                    + $"{m_startPastSurface * 100f:F1} → {pastSurface * 100f:F1}cm"
            );
            Release();
            m_open = false;
            return false;
        }

        Debug.Log(
            $"[래그돌팔접기] {Label} 시도 {m_attempt} 실패 — 벽면 너머 "
                + $"{m_startPastSurface * 100f:F1} → {pastSurface * 100f:F1}cm"
        );

        Release();
        m_probeCountdown = 1;
        return true;
    }

    private void FoldStep(float t)
    {
        Vector3 torso = m_rig.Hips.position;

        for (int i = 0; i < m_foldCount; i++)
        {
            Transform bone = m_rig.Bones.GetTransform(m_fold[i]);
            Transform child = ChildTransform(m_fold[i]);
            if (bone == null || child == null)
                continue;

            AimBoneAlong(bone, child, torso - bone.position, t);
        }
    }

    private static void AimBoneAlong(Transform bone, Transform child, Vector3 want, float t)
    {
        Vector3 current = child.position - bone.position;
        if (current.sqrMagnitude < 1e-6f || want.sqrMagnitude < 1e-6f)
            return;

        Quaternion target = Quaternion.FromToRotation(current.normalized, want.normalized) * bone.rotation;
        bone.rotation = Quaternion.Slerp(bone.rotation, target, Mathf.Clamp01(t));
    }

    private Transform ChildTransform(int index)
    {
        int child = m_rig.Bones.ChildIndex(index);
        if (child >= 0)
            return m_rig.Bones.GetTransform(child);

        Transform bone = m_rig.Bones.GetTransform(index);
        return bone != null && bone.childCount > 0 ? bone.GetChild(0) : null;
    }

    private void Release()
    {
        for (int i = 0; i < m_foldCount; i++)
        {
            m_rig.SetBoneKinematic(m_fold[i], false);
            m_rig.StopBone(m_fold[i]);
        }

        if (m_foldCount > 0)
            m_rig.WakeAll();

        m_foldCount = 0;
        m_boneIndex = -1;
        m_foldElapsed = 0f;
        m_holdLeft = 0;
    }
}
