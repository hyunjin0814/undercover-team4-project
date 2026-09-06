using UnityEngine;

/// <summary>
/// <b>프리팹이 authoring한 자세</b>를 담아 두고 되돌리는 자리 — 리그 전체의 로컬 위치·회전 한 벌.
///
/// <see cref="RagdollRig"/>에서 갈라냈다. 저쪽은 <b>지금 뼈가 어떤 상태인가</b>를 매 프레임 바꾸는데,
/// 이쪽은 <b>출고 시 뼈가 어땠나</b>라 쓰기가 수집 때 <b>한 번</b>뿐이다 — 수명이 다르다.
///
/// <b>왜 들고 있나.</b> 관절의 <c>connectedAnchor</c>가 자동 설정이라 관절이 처음 깨어날 때의 뼈
/// 길이를 기준으로 구워진다. 그 뒤 <c>localPosition</c>이 달라지면 관절은 첫 스텝부터 위반 상태로
/// 출발한다 — 사지가 고무처럼 늘어나는 그림이다. 근거는 <c>docs/ragdoll-rig.md</c> §5.
///
/// 담는 범위는 리지드바디 뼈만이 아니라 <b>리그 전체</b>다 — 포즈 복사가 훑는 범위와 같아야 한다.
///
/// ⚠ <b>되돌리는 메서드 둘을 합치지 말 것</b>(docs §5) — 대상도 되돌리는 것도 부르는 자리도 다르다.
/// ⚠ 둘 다 <b>키네마틱일 때만</b> 의미가 있다. 동적인 뼈는 다음 물리 스텝에 덮인다.
/// </summary>
public sealed class RagdollBindPose
{
    /// <summary>리그를 못 찾은 상태 — 모든 호출이 무동작이다.</summary>
    public static readonly RagdollBindPose Empty =
        new RagdollBindPose(new Transform[0], new bool[0]);

    private readonly Transform[] m_bones;
    private readonly Vector3[] m_positions;
    private readonly Quaternion[] m_rotations;
    private readonly bool[] m_streamed; // 자세 한 벌에 드는가 — 거짓인 말단 뼈는 바인드로 못박는다

    private RagdollBindPose(Transform[] bones, bool[] streamed)
    {
        m_bones = bones;
        m_streamed = streamed;
        m_positions = new Vector3[bones.Length];
        m_rotations = new Quaternion[bones.Length];

        for (int i = 0; i < bones.Length; i++)
        {
            m_positions[i] = bones[i].localPosition;
            m_rotations[i] = bones[i].localRotation;
        }
    }

    /// <summary>
    /// 지금 리그의 자세를 바인드로 담는다.
    ///
    /// ⚠ <b>수집 시점에만 부른다</b> — 아직 아무도 리그를 건드리지 않았을 때다. 나중에 부르면
    /// 그때의 오염된 자세가 "바인드"가 된다.
    /// </summary>
    /// <param name="bodies">리지드바디 뼈 — "체인 위에 있는가" 판정의 기준이다.</param>
    public static RagdollBindPose Capture(Transform boneRoot, Rigidbody[] bodies)
    {
        if (boneRoot == null || bodies == null)
            return Empty;

        Transform[] bones = boneRoot.GetComponentsInChildren<Transform>(true);
        var streamed = new bool[bones.Length];

        for (int i = 0; i < bones.Length; i++)
        {
            // ⚠ Rigidbody 유무가 아니라 <b>bodies 소속</b>으로 판정한다 — 수집이 레이어로도 거른다.
            streamed[i] = IsSelfOrAncestorOfBody(bones[i], bodies);
        }

        return new RagdollBindPose(bones, streamed);
    }

    /// <summary>
    /// <b>자세 한 벌</b>을 만들어 돌려준다 — 리지드바디 뼈에 그 사이를 잇는 <b>체인 뼈</b>를 더한 것이다
    /// (빼면 상체가 Spine_01 하나에 매달린다). 계층 순서 그대로라 피어마다 같고 부모가 자식보다 먼저 온다.
    ///
    /// 만들어 주기만 하고 <b>들고 있지 않는다</b> — 이 배열로 자세를 담고 입히는 것은 리그의 일이다.
    /// </summary>
    public Transform[] BuildPoseBones()
    {
        int count = 0;
        for (int i = 0; i < m_bones.Length; i++)
        {
            if (m_streamed[i])
                count++;
        }

        var pose = new Transform[count];
        int next = 0;
        for (int i = 0; i < m_bones.Length; i++)
        {
            if (m_streamed[i])
                pose[next++] = m_bones[i];
        }

        return pose;
    }

    /// <summary>
    /// 리그를 바인드 포즈(위치 + 회전)로 되돌린다 — <b>뼈 길이 복원이 목적</b>이라 부활처럼
    /// 시체가 쉬는 시점에 부른다.
    /// </summary>
    public void RestoreAll()
    {
        for (int i = 0; i < m_bones.Length; i++)
        {
            if (m_bones[i] == null)
                continue;

            m_bones[i].localPosition = m_positions[i];
            m_bones[i].localRotation = m_rotations[i];
        }
    }

    /// <summary>
    /// <b>말단 뼈</b>(손·발·손가락)의 회전만 바인드로 못박는다 — 아무도 값을 보내 주지 않는 뼈를
    /// 전 피어가 같은 값으로 맞추는 것이다. 래그돌 <b>진입 시 모든 피어가</b> 부른다.
    ///
    /// ⚠ 체인 뼈는 손대지 않는다 — 한때 못박았다가 <b>시체가 바닥에 파묻혔다</b>(docs §5).
    /// ⚠ 위치를 건드리지 않는다 — 그쪽은 뼈 길이이고, 맞추는 짝은 원격의 <c>ApplyBoneLengths</c>다.
    /// </summary>
    public void RestoreUnstreamedRotations()
    {
        for (int i = 0; i < m_bones.Length; i++)
        {
            if (m_bones[i] == null || m_streamed[i])
                continue;

            m_bones[i].localRotation = m_rotations[i];
        }
    }

    // 이 뼈가 리지드바디 뼈이거나 그 조상인가 — 즉 몸 모양을 결정하는 체인 위에 있는가.
    private static bool IsSelfOrAncestorOfBody(Transform bone, Rigidbody[] bodies)
    {
        for (int i = 0; i < bodies.Length; i++)
        {
            Transform body = bodies[i].transform;
            if (body == bone || body.IsChildOf(bone))
                return true;
        }

        return false;
    }
}
