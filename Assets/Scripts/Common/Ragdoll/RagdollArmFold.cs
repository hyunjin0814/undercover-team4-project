using UnityEngine;

/// <summary>
/// 벽에 박힌 팔을 <b>몸쪽으로 접어서</b> 빼낸다. (#980)
///
/// <b>대상은 팔뿐이다</b> — 다리·머리는 보지 않는다. 팔만 "가장 얇고 + 가장 가볍고 + 지렛대가 가장
/// 길다"를 동시에 만족해서 실제로 끼는 것이 거의 팔이다(docs/980 §1-2).
///
/// <b>월드 방향을 판단하지 않는 것이 요점이다.</b> 먼저 시도한 "벽면 법선 방향으로 밀어내기"는
/// "어느 쪽이 벽 밖인가"에 전부를 걸었는데, 몸이 건물에 바짝 붙으면 <b>골반 자체가 건물 안쪽</b>이라
/// 그 판단이 뒤집힌다 — 실측에서 밀수록 깊어졌다(벽면 너머 48cm → 74cm, docs/980 §9-8).
/// 여기서는 방향을 묻지 않고 <b>팔을 몸통 쪽으로 접기만</b> 한다. "몸쪽"은 틀릴 수가 없다.
///
/// 셋을 지킨다:
/// <list type="number">
///   <item><b>위치를 대입하지 않고 회전만 한다</b> — 뼈 길이와 관절 앵커가 보존된다(docs/ragdoll-rig.md §5).
///         재현 도구가 팔을 벽에 찔러 넣을 때 쓰는 방법과 같다.</item>
///   <item><b>접는 동안 그 팔만 물리에서 뗀다</b>(키네마틱) — 벽 접촉과 싸우지 않는다.</item>
///   <item><b>빠져나오는 즉시 멈춘다</b> — 필요한 만큼만 접어야 관절 한계를 넘지 않는다.</item>
/// </list>
///
/// 권위 피어에서만 부른다(소유자가 보장한다). 리그만 알므로 플레이어도 그대로 쓸 수 있다.
/// </summary>
public class RagdollArmFold
{
    private const float k_probeClearance = 0.02f;

    // 접기를 끝낸 뒤 물리로 돌려주기 전에 쉬는 스텝 — 자세가 스트림에 실릴 틈을 준다.
    private const int k_holdSteps = 2;

    private const int k_maxFoldBones = 4;

    /// <summary>한 스텝의 튜닝값 — 소유자의 인스펙터가 정본이라 매 스텝 넘겨받는다.</summary>
    public readonly struct Tuning
    {
        public readonly float MinPastSurface; // m — 이보다 깊이 넘어가야 "박혔다"
        public readonly float FoldSeconds; // 다 접는 데 걸리는 시간
        public readonly float LimbMassMax; // kg — 이보다 무거우면 몸통이라 접지 않는다
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
    private readonly int[] m_arms = new int[8];
    private readonly int[] m_fold = new int[k_maxFoldBones]; // 접는 뼈 — 부모(어깨)가 앞이다

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

    /// <summary>로그 앞에 붙는 이름 — 소유자가 준다.</summary>
    public string Label { get; set; } = "?";

    /// <summary>지금 접고 있는가 — 참인 동안 소유자는 루트 추종을 쉬고 스트림을 살려 둔다.</summary>
    public bool IsFolding => m_foldCount > 0;

    /// <summary>이번 에피소드에서 아직 볼 일이 있는가.</summary>
    public bool IsWindowOpen => m_open;

    public RagdollArmFold(RagdollRig rig)
    {
        m_rig = rig;
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

    /// <summary>
    /// <b>진입 프레임의 예방</b> — 뼈가 아직 키네마틱일 때(물리로 넘기기 전에) 벽 안에 있는 팔을
    /// 한 번에 접는다. 여기서 막으면 애초에 박힌 채로 출발하지 않는다(docs/980 §3-ⓐ).
    /// </summary>
    /// <returns>접었으면 참.</returns>
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

        // 한 번에 끝까지 접는다. 여기서 조금씩 접으며 재는 것은 뜻이 없다 —
        // <c>Physics.autoSyncTransforms</c>가 꺼져 있어 같은 프레임 안에서는 콜라이더 bounds가
        // 갱신되지 않으므로(docs/npc-ragdoll.md의 그 함정), 재 봐야 접기 전 값이 나온다.
        FoldStep(1f);

        Debug.Log(
            $"[래그돌팔접기] {Label} 진입 예방 — 뼈={m_rig.Bones.GetName(bone)} 벽={pin.Wall.name} "
                + $"벽면 너머 {pin.PastSurface * 100f:F1}cm였다. 접은 채로 물리에 넘긴다"
        );

        m_foldCount = 0; // 키네마틱 전환을 안 했으므로 되돌릴 것도 없다
        return true;
    }

    /// <summary>
    /// 한 물리 스텝. <b><c>FixedUpdate</c>의 마지막</b>에서만 부른다.
    /// </summary>
    /// <param name="settled">잠들어 있는가 — 판정 주기를 늦춘다.</param>
    /// <returns>자세가 바뀌는 중이라 스트림이 살아 있어야 하면 참.</returns>
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

    // ---- 판정 ----

    private bool TickProbe(in Tuning tuning)
    {
        int bone = FindStuckArm(tuning, out RagdollWallProbe.Pin pin);

        if (bone < 0)
        {
            m_pendingBone = -1;
            m_confirmCount = 0;
            return false;
        }

        // 같은 뼈가 연속으로 잡혀야 손댄다 — 날아가며 벽을 스치는 팔을 거른다.
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

        // 그 팔만 물리에서 뗀다 — 접는 동안 벽 접촉과 싸우지 않게.
        for (int i = 0; i < m_foldCount; i++)
            m_rig.SetBoneKinematic(m_fold[i], true);

        Debug.Log(
            $"[래그돌팔접기] {Label} 접기 시작 — 뼈={m_rig.Bones.GetName(bone)} 벽={pin.Wall.name} "
                + $"벽면 너머 {pin.PastSurface * 100f:F1}cm, 대상 {m_foldCount}뼈, "
                + $"시도 {m_attempt}/{tuning.MaxAttempts}"
        );

        return true;
    }

    // 가장 깊이 박힌 팔 뼈 — 없으면 -1.
    private int FindStuckArm(in Tuning tuning, out RagdollWallProbe.Pin worstPin)
    {
        worstPin = default;

        // ⚠ 팔만 본다 — 다리·머리는 제외다. 실측상 끼는 것은 거의 팔이고, 대상을 좁힌 만큼
        // 레이 수(9발 → 4발)와 오탐(바닥에 잠긴 발) 둘 다 준다.
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
                out RagdollWallProbe.Pin pin
            )
        )
            return false;

        pastSurface = pin.PastSurface;
        return pastSurface > tuning.MinPastSurface;
    }

    // 접을 뼈 — 박힌 뼈와 그 위쪽 사지 뼈(어깨). 몸통에 닿으면 멈춘다. 부모가 앞에 오게 뒤집는다.
    private int CollectFoldChain(int bone, in Tuning tuning)
    {
        int count = m_rig.Bones.CollectChainUpward(bone, k_maxFoldBones - 1, tuning.LimbMassMax, m_fold);

        for (int i = 0; i < count / 2; i++)
        {
            (m_fold[i], m_fold[count - 1 - i]) = (m_fold[count - 1 - i], m_fold[i]);
        }

        return count;
    }

    // ---- 접기 ----

    private bool TickFold(in Tuning tuning)
    {
        // 접는 동안 몸이 잠들면 자세가 원격으로 안 나간다 — 나머지 뼈를 깨워 둔다.
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

        // ⚠ 이 판정은 <b>한 스텝 뒤진다</b> — autoSyncTransforms가 꺼져 있어 방금 돌린 회전이
        // 아직 콜라이더 bounds에 안 실렸다. 0.02초 지연이라 접기에는 문제가 없다.
        bool pinned = IsPinned(m_boneIndex, tuning, out float pastSurface);

        // 빠져나왔으면 <b>거기서 멈춘다</b> — 더 접으면 관절 한계를 넘어 놓는 순간 튕긴다.
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

        // 다 접었는데도 안 빠졌다.
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

    // 뼈를 몸통(골반) 쪽으로 조금 돌린다 — 위치는 건드리지 않는다.
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

    // 뼈가 향한 방향(뼈→자식)을 want 쪽으로 t만큼 돌린다. <b>회전만</b> — 위치를 대입하면 관절
    // 앵커가 어긋나 물리로 돌려주는 순간 몸이 발사된다(docs/ragdoll-rig.md §5).
    private static void AimBoneAlong(Transform bone, Transform child, Vector3 want, float t)
    {
        Vector3 current = child.position - bone.position;
        if (current.sqrMagnitude < 1e-6f || want.sqrMagnitude < 1e-6f)
            return;

        Quaternion target = Quaternion.FromToRotation(current.normalized, want.normalized) * bone.rotation;
        bone.rotation = Quaternion.Slerp(bone.rotation, target, Mathf.Clamp01(t));
    }

    // 방향을 재는 데 쓸 자식 — 리지드바디 자식이 있으면 그쪽, 없으면(말단) 계층의 첫 자식.
    private Transform ChildTransform(int index)
    {
        int child = m_rig.Bones.ChildIndex(index);
        if (child >= 0)
            return m_rig.Bones.GetTransform(child);

        Transform bone = m_rig.Bones.GetTransform(index);
        return bone != null && bone.childCount > 0 ? bone.GetChild(0) : null;
    }

    // 접기를 끝내고 팔을 물리로 돌려준다 — 어느 경로로 끝나든 여기를 지난다.
    private void Release()
    {
        for (int i = 0; i < m_foldCount; i++)
        {
            m_rig.SetBoneKinematic(m_fold[i], false);
            m_rig.StopBone(m_fold[i]); // 접은 속도가 실려 나가지 않게
        }

        if (m_foldCount > 0)
            m_rig.WakeAll(); // 돌려준 팔이 마저 무너지고 나서 다시 잠들어야 한다

        m_foldCount = 0;
        m_boneIndex = -1;
        m_foldElapsed = 0f;
        m_holdLeft = 0;
    }
}
