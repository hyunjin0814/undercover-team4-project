using UnityEngine;

/// <summary>
/// 래그돌 뼈 한 벌을 물리로 넘기고 되돌리는 리그. 네트워크·권위는 소유자가 다룬다.
/// 설계 근거는 docs/ragdoll-rig.md 참고.
/// </summary>
public class RagdollRig : MonoBehaviour
{
    public const string k_layerName = "Ragdoll";

    public const string k_defaultBoneRootName = "Root";

    private const float k_maxDepenetrationVelocity = 0.5f;

    private const int k_solverIterations = 12;
    private const int k_solverVelocityIterations = 4;

    [Tooltip("몸통 리그 최상단 오브젝트 이름 — 이 오브젝트의 직속 자식이어야 한다(1인칭 팔 리그와 구분)")]
    [SerializeField] private string m_boneRootName = k_defaultBoneRootName;

    [Tooltip("겹친 콜라이더를 밀어내는 속도 상한(m/s). 올리면 겹침에서 튀어오름이 커진다(엔진 기본 10)")]
    [SerializeField] private float m_maxDepenetrationVelocity = k_maxDepenetrationVelocity;

    [Tooltip("골반보다 높은 뼈에 얹는 추가 속도 비율(1/m) — 상체가 더 빨라 다리가 끌리는 텀블이 생긴다")]
    [SerializeField] private float m_tumbleBias = 0.8f;

    private Transform m_boneRoot;

    private Rigidbody[] m_bodies;
    private Collider[] m_boneColliders;
    private CharacterJoint[] m_joints;
    private float[] m_baseLinearDamping;
    private float[] m_baseAngularDamping;

    private Transform m_hipsBone;
    private Rigidbody m_hipsBody;
    private Transform m_headBone;

    private RagdollSkins m_skins = RagdollSkins.Empty;

    private RagdollBoneGraph m_boneGraph = RagdollBoneGraph.Empty;

    private Vector3[] m_capturedPositions;
    private Quaternion[] m_capturedRotations;

    private RagdollBindPose m_bindPose = RagdollBindPose.Empty;

    private Transform[] m_poseBones;

    public bool IsValid => m_bodies != null && m_bodies.Length > 0 && m_hipsBone != null;

    public Transform Hips => m_hipsBone;

    public Rigidbody HipsBody => m_hipsBody;

    public Transform BoneRoot => m_boneRoot;

    public int BoneCount => m_poseBones != null ? m_poseBones.Length : 0;

    public float LowestBoneY
    {
        get
        {
            if (m_bodies == null || m_bodies.Length == 0)
                return 0f;

            float lowest = float.MaxValue;
            for (int i = 0; i < m_bodies.Length; i++)
            {
                float y = m_bodies[i].transform.position.y;
                if (y < lowest)
                    lowest = y;
            }
            return lowest;
        }
    }

    private bool m_collected;

    private void Awake() => EnsureCollected();

    /// <summary>뼈 수집을 보장한다(멱등). 리그를 쓰는 컴포넌트의 Awake 첫머리에서 호출한다.</summary>
    public void EnsureCollected()
    {
        if (m_collected)
            return;

        m_collected = true;
        Collect();
    }

    private void Collect()
    {
        m_bodies = new Rigidbody[0];

        int layer = LayerMask.NameToLayer(k_layerName);
        if (layer < 0)
        {
            Debug.LogError(
                $"[래그돌] 레이어 '{k_layerName}'가 없다 — Tools > Player > Finish Ragdoll Setup을 먼저 실행할 것",
                this
            );
            return;
        }

        m_boneRoot = transform.Find(m_boneRootName);
        if (m_boneRoot == null)
        {
            Debug.LogError(
                $"[래그돌] 몸통 리그 '{m_boneRootName}'를 {name}의 직속 자식에서 찾지 못했다",
                this
            );
            return;
        }

        Rigidbody[] all = m_boneRoot.GetComponentsInChildren<Rigidbody>(true);
        int count = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].gameObject.layer == layer)
                count++;
        }

        m_bodies = new Rigidbody[count];
        int next = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].gameObject.layer != layer)
                continue;

            m_bodies[next++] = all[i];

            if (m_hipsBone == null && all[i].GetComponent<CharacterJoint>() == null)
            {
                m_hipsBone = all[i].transform;
                m_hipsBody = all[i];
            }
            if (all[i].name == "Head")
                m_headBone = all[i].transform;
        }

        m_capturedPositions = new Vector3[count];
        m_capturedRotations = new Quaternion[count];

        m_baseLinearDamping = new float[count];
        m_baseAngularDamping = new float[count];

        m_boneColliders = new Collider[count];
        m_joints = new CharacterJoint[count];
        for (int i = 0; i < count; i++)
        {
            m_baseLinearDamping[i] = m_bodies[i].linearDamping;
            m_baseAngularDamping[i] = m_bodies[i].angularDamping;
            m_boneColliders[i] = m_bodies[i].GetComponent<Collider>();
            m_joints[i] = m_bodies[i].GetComponent<CharacterJoint>();
        }

        if (count == 0 || m_hipsBone == null)
        {
            Debug.LogError(
                $"[래그돌] {m_boneRoot.name} 아래에서 래그돌 뼈를 제대로 찾지 못했다"
                    + $" (뼈 {count}개, 골반 {(m_hipsBone == null ? "없음" : m_hipsBone.name)})"
                    + " — Tools > Player > Finish Ragdoll Setup을 실행할 것",
                this
            );
            m_bodies = new Rigidbody[0];
            return;
        }

        ApplyRuntimePhysics();

        m_bindPose = RagdollBindPose.Capture(m_boneRoot, m_bodies);
        m_poseBones = m_bindPose.BuildPoseBones();

        m_boneGraph = new RagdollBoneGraph(m_bodies, m_boneColliders, m_joints, m_hipsBody, m_headBone);

        m_skins = RagdollSkins.Collect(transform, m_boneRoot);
        SetKinematic(true);
    }

    private void ApplyRuntimePhysics()
    {
        for (int i = 0; i < m_bodies.Length; i++)
        {
            m_bodies[i].maxDepenetrationVelocity = m_maxDepenetrationVelocity;
            m_bodies[i].solverIterations = k_solverIterations;
            m_bodies[i].solverVelocityIterations = k_solverVelocityIterations;
        }
    }

    /// <summary>전 뼈를 키네마틱과 물리 사이에서 전환한다.</summary>
    public void SetKinematic(bool kinematic)
    {
        if (m_bodies == null)
            return;

        if (!kinematic)
            Physics.SyncTransforms();

        for (int i = 0; i < m_bodies.Length; i++)
            SetBodyKinematic(m_bodies[i], kinematic);
    }

    private static void SetBodyKinematic(Rigidbody body, bool kinematic)
    {
        if (kinematic && !body.isKinematic)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }

        body.isKinematic = kinematic;
        body.interpolation = kinematic
            ? RigidbodyInterpolation.None
            : RigidbodyInterpolation.Interpolate;

        if (kinematic)
            return;

        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
    }

    /// <summary>리그 뼈 콜라이더와 other의 충돌을 켜고 끈다.</summary>
    public void IgnoreCollisionWith(Collider other, bool ignore)
    {
        if (other == null || m_boneColliders == null)
            return;

        for (int i = 0; i < m_boneColliders.Length; i++)
        {
            if (m_boneColliders[i] != null)
                Physics.IgnoreCollision(m_boneColliders[i], other, ignore);
        }
    }

    public RagdollSkins Skins => m_skins;

    public RagdollBoneGraph Bones => m_boneGraph;

    public RagdollBindPose BindPose => m_bindPose;

    public bool AnyKinematic
    {
        get
        {
            if (m_bodies == null)
                return false;

            for (int i = 0; i < m_bodies.Length; i++)
            {
                if (m_bodies[i] != null && m_bodies[i].isKinematic)
                    return true;
            }
            return false;
        }
    }

    /// <summary>전 뼈에 같은 속도를 주고, 골반보다 높은 뼈에 조금 더 얹어 회전을 만든다.</summary>
    public void ApplyImpulse(Vector3 velocity)
    {
        if (velocity == Vector3.zero || m_bodies == null || m_hipsBone == null)
            return;

        int applied = 0;

        float hipsHeight = m_hipsBone.position.y;
        for (int i = 0; i < m_bodies.Length; i++)
        {
            if (m_bodies[i] == null || m_bodies[i].isKinematic)
                continue;

            float lift = m_bodies[i].worldCenterOfMass.y - hipsHeight;
            m_bodies[i].linearVelocity += velocity * (1f + m_tumbleBias * lift);
            applied++;
        }

        if (applied == 0)
        {
            Debug.LogWarning(
                $"RagdollRig: 임펄스({velocity.magnitude:0.00})가 통째로 버려졌다 — 뼈가 전부 "
                    + "키네마틱이다. 권위 피어가 맞는지, 뼈를 물리로 넘긴 뒤에 부르는지 확인할 것",
                this
            );
        }
    }

    /// <summary>전 뼈에 감쇠를 건다 — 푸는 것은 <see cref="RestoreDamping"/>.</summary>
    public void SetDamping(float linear, float angular)
    {
        if (m_bodies == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
        {
            if (m_bodies[i] == null)
                continue;
            m_bodies[i].linearDamping = linear;
            m_bodies[i].angularDamping = angular;
        }
    }

    /// <summary>감쇠를 프리팹에서 읽어 둔 평시 값으로 되돌린다.</summary>
    public void RestoreDamping()
    {
        if (m_bodies == null || m_baseLinearDamping == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
        {
            if (m_bodies[i] == null)
                continue;
            m_bodies[i].linearDamping = m_baseLinearDamping[i];
            m_bodies[i].angularDamping = m_baseAngularDamping[i];
        }
    }

    /// <summary>뼈 속도의 하드 상한 — 방향은 두고 크기만 자른다. 0 이하면 무동작.</summary>
    public void ClampSpeed(float maxSpeed)
    {
        if (maxSpeed <= 0f || m_bodies == null)
            return;

        float maxSqr = maxSpeed * maxSpeed;
        for (int i = 0; i < m_bodies.Length; i++)
        {
            Rigidbody body = m_bodies[i];
            if (body == null || body.isKinematic)
                continue;

            Vector3 velocity = body.linearVelocity;
            float sqr = velocity.sqrMagnitude;
            if (sqr > maxSqr)
                body.linearVelocity = velocity * (maxSpeed / Mathf.Sqrt(sqr));
        }
    }

    /// <summary>전 뼈를 깨운다. 골반만 깨우면 안 된다 — 사지가 자고 있으면 몸이 한 덩어리로 끌려온다.</summary>
    public void WakeAll()
    {
        if (m_bodies == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
        {
            if (m_bodies[i] != null && !m_bodies[i].isKinematic)
                m_bodies[i].WakeUp();
        }
    }

    public bool AllAsleep
    {
        get
        {
            if (m_bodies == null || m_bodies.Length == 0)
                return false;

            for (int i = 0; i < m_bodies.Length; i++)
            {
                if (m_bodies[i] == null || m_bodies[i].isKinematic)
                    continue;

                if (!m_bodies[i].IsSleeping())
                    return false;
            }

            return true;
        }
    }

    /// <summary>전 뼈를 강제로 물리 수면시킨다(정착 타임아웃용).</summary>
    public void SleepAll()
    {
        if (m_bodies == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
        {
            if (m_bodies[i] != null && !m_bodies[i].isKinematic)
                m_bodies[i].Sleep();
        }
    }

    /// <summary>전 뼈를 같은 델타로 평행이동한다. 포즈·상대속도·관절이 보존된다.</summary>
    public void TranslateBy(Vector3 delta)
    {
        if (m_bodies == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
            m_bodies[i].position += delta;
    }

    /// <summary>전 뼈의 월드 포즈를 저장한다 — 루트를 옮기기 전에 부른다.</summary>
    public void CapturePose()
    {
        if (m_bodies == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
        {
            m_capturedPositions[i] = m_bodies[i].transform.position;
            m_capturedRotations[i] = m_bodies[i].transform.rotation;
        }
    }

    /// <summary>저장한 월드 포즈를 되돌린다 — 루트를 옮긴 뒤에 부른다. 화면은 그대로다.</summary>
    public void RestoreCapturedPose()
    {
        if (m_bodies == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
            m_bodies[i].transform.SetPositionAndRotation(
                m_capturedPositions[i],
                m_capturedRotations[i]
            );
    }

    /// <summary>자세 뼈의 로컬 회전을 담아 간다 — 배열 길이는 <see cref="BoneCount"/>.</summary>
    public bool CaptureLocalPose(Quaternion[] rotations, out Vector3 hipsLocalPosition)
    {
        hipsLocalPosition = Vector3.zero;
        if (m_poseBones == null || rotations == null || rotations.Length != m_poseBones.Length)
            return false;

        for (int i = 0; i < m_poseBones.Length; i++)
            rotations[i] = m_poseBones[i].localRotation;

        hipsLocalPosition = m_hipsBone.localPosition;
        return true;
    }

    /// <summary>담아 온 로컬 회전을 그대로 입힌다 — 원격 피어가 자세를 재현할 때 쓴다.</summary>
    public bool ApplyLocalPose(Quaternion[] rotations, Vector3 hipsLocalPosition)
    {
        if (m_poseBones == null || rotations == null || rotations.Length != m_poseBones.Length)
            return false;

        m_hipsBone.localPosition = hipsLocalPosition;

        for (int i = 0; i < m_poseBones.Length; i++)
            m_poseBones[i].localRotation = rotations[i];

        return true;
    }

    /// <summary>전 자세 뼈의 로컬 위치(= 뼈 길이)를 담아 간다 — 길이는 <see cref="BoneCount"/>.</summary>
    public bool CaptureBoneLengths(Vector3[] lengths)
    {
        if (m_poseBones == null || lengths == null || lengths.Length != m_poseBones.Length)
            return false;

        for (int i = 0; i < m_poseBones.Length; i++)
            lengths[i] = m_poseBones[i].localPosition;

        return true;
    }

    /// <summary>받은 뼈 길이를 적용한다(원격 전용, 골반 제외).</summary>
    public bool ApplyBoneLengths(Vector3[] lengths)
    {
        if (m_poseBones == null || lengths == null || lengths.Length != m_poseBones.Length)
            return false;

        for (int i = 0; i < m_poseBones.Length; i++)
        {
            if (m_poseBones[i] == m_hipsBone)
                continue;

            m_poseBones[i].localPosition = lengths[i];
        }

        return true;
    }

    private const float k_lyingHorizontalRatio = 0.7f;

    /// <summary>골반→머리를 지면에 투영해 몸이 누운 방향의 yaw를 구한다. 누워 있지 않으면 false.</summary>
    public bool TryGetBodyYaw(out float yaw)
    {
        yaw = 0f;
        if (m_headBone == null || m_hipsBone == null)
            return false;

        Vector3 lengthwise = m_headBone.position - m_hipsBone.position;
        float length = lengthwise.magnitude;
        if (length < 0.02f)
            return false;

        Vector3 horizontal = new Vector3(lengthwise.x, 0f, lengthwise.z);
        if (horizontal.magnitude < length * k_lyingHorizontalRatio)
            return false;

        yaw = Quaternion.LookRotation(horizontal.normalized).eulerAngles.y;
        return true;
    }

    private Rigidbody GetBody(int index) =>
        m_bodies != null && index >= 0 && index < m_bodies.Length ? m_bodies[index] : null;

    /// <summary>뼈 하나만 물리에서 떼거나 되돌리고, 전환 시 속도를 지운다.</summary>
    public void SetBoneKinematic(int index, bool kinematic)
    {
        Rigidbody body = GetBody(index);
        if (body != null)
            SetBodyKinematic(body, kinematic);
    }

    /// <summary>그 뼈의 속도를 지운다 — 물리로 돌려준 직후 튀지 않게.</summary>
    public void StopBone(int index)
    {
        Rigidbody body = GetBody(index);
        if (body == null || body.isKinematic)
            return;

        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
    }
}
