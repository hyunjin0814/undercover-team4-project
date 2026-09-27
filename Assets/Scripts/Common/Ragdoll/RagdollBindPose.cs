using UnityEngine;

/// <summary>
/// 프리팹에 authoring된 리그 전체의 로컬 위치·회전(바인드 포즈)을 저장하고 되돌린다.
/// 수집 시점에 한 번만 기록하며, 복원은 키네마틱 상태에서만 의미가 있다.
/// </summary>
public sealed class RagdollBindPose
{
    public static readonly RagdollBindPose Empty =
        new RagdollBindPose(new Transform[0], new bool[0]);

    private readonly Transform[] m_bones;
    private readonly Vector3[] m_positions;
    private readonly Quaternion[] m_rotations;
    private readonly bool[] m_streamed;

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

    /// <summary>현재 리그 자세를 바인드 포즈로 저장한다 — 수집 시점에만 호출할 것.</summary>
    public static RagdollBindPose Capture(Transform boneRoot, Rigidbody[] bodies)
    {
        if (boneRoot == null || bodies == null)
            return Empty;

        Transform[] bones = boneRoot.GetComponentsInChildren<Transform>(true);
        var streamed = new bool[bones.Length];

        for (int i = 0; i < bones.Length; i++)
        {
            streamed[i] = IsSelfOrAncestorOfBody(bones[i], bodies);
        }

        return new RagdollBindPose(bones, streamed);
    }

    /// <summary>리지드바디 뼈와 체인 뼈를 계층 순서로 담은 자세 배열을 만들어 돌려준다.</summary>
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

    /// <summary>리그 전체를 바인드 포즈(위치·회전)로 되돌려 뼈 길이를 복원한다.</summary>
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

    /// <summary>스트리밍되지 않는 말단 뼈(손·발·손가락)의 회전을 바인드 값으로 맞춘다.</summary>
    public void RestoreUnstreamedRotations()
    {
        for (int i = 0; i < m_bones.Length; i++)
        {
            if (m_bones[i] == null || m_streamed[i])
                continue;

            m_bones[i].localRotation = m_rotations[i];
        }
    }

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
