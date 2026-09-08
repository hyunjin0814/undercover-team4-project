using UnityEngine;

/// <summary>
/// 뼈를 <b>하나씩·계층으로</b> 묻는 자리 — 읽기 전용 질의만 있다. (#980 벽에 박힌 팔 접기)
///
/// <see cref="RagdollRig"/>에서 갈라냈다. 저쪽은 <b>전 뼈를 한꺼번에</b> 다루는 물리 조작
/// (키네마틱 전환·임펄스·감쇠·수면)이고, 여기는 <b>i번째 뼈가 무엇이며 누구에게 매달렸나</b>다.
/// 소비자도 갈린다 — 이 질의들을 쓰는 것은 <see cref="RagdollArmFold"/> 하나뿐이었다.
///
/// <b>배열을 소유하지 않는다.</b> 리그가 수집한 것을 그대로 받아 읽기만 한다 — 진짜 상태의 주인은
/// 계속 리그 하나다.
///
/// ⚠ 인덱스는 <c>bodies</c>/<c>colliders</c>/<c>joints</c>가 <b>공유</b>한다(같은 길이·같은 순서).
/// 리그의 <c>SetBoneKinematic</c>·<c>StopBone</c>도 같은 인덱스를 받는다.
///
/// ⚠ <b>쓰기는 여기 없다.</b> 뼈 하나를 물리에서 떼는 일은 리그에 남겼다 — 전 뼈 전환
/// (<c>SetKinematic</c>)과 "전이 양쪽에서 속도를 지운다"는 규칙(docs/ragdoll-rig.md §6)을 공유하는데,
/// 그 규칙이 두 클래스에 복제되면 한쪽만 고쳐지는 종류의 버그가 된다.
/// </summary>
public sealed class RagdollBoneGraph
{
    /// <summary>뼈를 못 찾은 리그 — 모든 질의가 비어 있는 답을 준다.</summary>
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

    /// <summary>
    /// <b>팔 뼈</b>의 인덱스를 담는다 — 사지 뼈 중 <b>다리와 머리를 뺀</b> 것.
    /// 실제로 벽에 끼는 것은 거의 팔이다(docs/980 §1-2: 팔만 "가장 얇고 + 가장 가볍고 + 지렛대가
    /// 가장 길다"를 동시에 만족한다).
    ///
    /// 이름 목록이 아니라 <b>계층</b>으로 가른다: 다리는 골반에 <b>직접</b> 매달리고 팔은 상체에
    /// 매달린다. 머리만 이름으로 알아본 뼈를 뺀다.
    /// </summary>
    /// <returns>담은 개수.</returns>
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
                continue; // 골반에 직접 매달렸다 = 다리(또는 척추)

            into[count++] = i;
        }

        return count;
    }

    /// <summary>
    /// <paramref name="index"/>에서 <b>부모 쪽으로</b> 뼈를 최대 <paramref name="depth"/>개 더한
    /// 체인을 담는다(자기 자신 포함). 질량이 <paramref name="maxMass"/>를 넘는 뼈에서 <b>멈춘다</b> —
    /// 몸통을 끌어들이면 사실상 전신이 되고, 그러면 방향이 틀렸을 때의 피해가 커진다.
    /// 부모는 <c>CharacterJoint.connectedBody</c>가 준다.
    /// </summary>
    /// <returns>담은 개수 — 1 이상.</returns>
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
                break; // 몸통에 닿았다 — 여기서 끊는 것이 이 함수의 요점이다

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

    // i번째 뼈의 Rigidbody — 범위 밖이면 null.
    private Rigidbody GetBody(int index) =>
        m_bodies != null && index >= 0 && index < m_bodies.Length ? m_bodies[index] : null;

    // 골반 뼈의 인덱스 — 못 찾으면 -1.
    private int HipsIndex()
    {
        if (m_bodies == null)
            return -1;

        for (int i = 0; i < m_bodies.Length; i++)
            if (m_bodies[i] == m_hipsBody)
                return i;

        return -1;
    }

    // 관절이 매달린 부모 뼈의 인덱스 — 골반이거나 못 찾으면 -1.
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
