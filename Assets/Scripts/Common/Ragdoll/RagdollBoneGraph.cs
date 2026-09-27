using UnityEngine;

/// <summary>
/// 래그돌 뼈를 개별·계층 단위로 조회하는 읽기 전용 헬퍼.
/// 배열은 RagdollRig가 소유하며 인덱스는 bodies/colliders/joints가 공유한다.
/// </summary>
public sealed class RagdollBoneGraph
{
    public static readonly RagdollBoneGraph Empty = new RagdollBoneGraph(
        new Rigidbody[0],
        new Collider[0],
        new CharacterJoint[0],
        null,
        null
    );

    private readonly Rigidbody[] m_bodies;
    private readonly Collider[] m_colliders;
    private readonly CharacterJoint[] m_joints;
    private readonly Rigidbody m_hipsBody;
    private readonly Transform m_headBone;

    public RagdollBoneGraph(
        Rigidbody[] bodies,
        Collider[] colliders,
        CharacterJoint[] joints,
        Rigidbody hipsBody,
        Transform headBone
    )
    {
        m_bodies = bodies;
        m_colliders = colliders;
        m_joints = joints;
        m_hipsBody = hipsBody;
        m_headBone = headBone;
    }

    /// <summary>i번째 뼈의 콜라이더 — 범위 밖이면 null.</summary>
    public Collider GetCollider(int index) =>
        m_colliders != null && index >= 0 && index < m_colliders.Length ? m_colliders[index] : null;

    /// <summary>i번째 뼈의 이름 — 로그가 "어느 뼈인지"를 말할 수 있어야 판정을 읽는다.</summary>
    public string GetName(int index)
    {
        Rigidbody body = GetBody(index);
        return body != null ? body.name : "?";
    }

    /// <summary>i번째 뼈의 트랜스폼 — 회전으로 자세를 고칠 때 쓴다(위치 대입은 관절 앵커를 깬다).</summary>
    public Transform GetTransform(int index)
    {
        Rigidbody body = GetBody(index);
        return body != null ? body.transform : null;
    }

    /// <summary>다리와 머리를 제외한 팔 뼈의 인덱스를 모은다.</summary>
    public int CollectArmBones(float maxMass, int[] into)
    {
        if (m_bodies == null || into == null)
            return 0;

        int hips = HipsIndex();
        int count = 0;

        for (int i = 0; i < m_bodies.Length && count < into.Length; i++)
        {
            if (m_bodies[i] == null || m_bodies[i] == m_hipsBody || m_bodies[i].mass > maxMass)
                continue;

            if (m_headBone != null && m_bodies[i].transform == m_headBone)
                continue;

            if (ParentIndex(i) == hips)
                continue;

            into[count++] = i;
        }

        return count;
    }

    /// <summary>index부터 부모 쪽으로 최대 depth개 뼈를 모으되, maxMass를 넘는 뼈에서 멈춘다.</summary>
    public int CollectChainUpward(int index, int depth, float maxMass, int[] into)
    {
        if (into == null || into.Length == 0 || GetBody(index) == null)
            return 0;

        into[0] = index;
        int count = 1;

        int current = index;
        for (int step = 0; step < depth && count < into.Length; step++)
        {
            int parent = ParentIndex(current);
            if (parent < 0 || m_bodies[parent].mass > maxMass)
                break;

            into[count++] = parent;
            current = parent;
        }

        return count;
    }

    /// <summary>이 뼈를 부모로 삼는 첫 자식 뼈 — 없으면 -1(말단). 뼈가 향한 방향을 재는 데 쓴다.</summary>
    public int ChildIndex(int index)
    {
        if (m_bodies == null || index < 0)
            return -1;

        for (int i = 0; i < m_bodies.Length; i++)
            if (ParentIndex(i) == index)
                return i;

        return -1;
    }

    private Rigidbody GetBody(int index) =>
        m_bodies != null && index >= 0 && index < m_bodies.Length ? m_bodies[index] : null;

    private int HipsIndex()
    {
        if (m_bodies == null)
            return -1;

        for (int i = 0; i < m_bodies.Length; i++)
            if (m_bodies[i] == m_hipsBody)
                return i;

        return -1;
    }

    private int ParentIndex(int index)
    {
        if (m_joints == null || index < 0 || index >= m_joints.Length || m_joints[index] == null)
            return -1;

        Rigidbody parent = m_joints[index].connectedBody;
        if (parent == null)
            return -1;

        for (int i = 0; i < m_bodies.Length; i++)
            if (m_bodies[i] == parent)
                return i;

        return -1;
    }
}
