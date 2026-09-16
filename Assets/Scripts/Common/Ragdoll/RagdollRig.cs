using UnityEngine;

/// <summary>
/// 래그돌 뼈 한 벌 — <b>물리에 넘기고 되돌리는 것만</b> 한다. (#506)
///
/// <b>네트워크·권위·이동 프록시를 모른다</b> — <c>IsOwner</c>·<c>NetworkObject</c>·
/// <c>CharacterController</c>가 여기에 <b>한 번도 나오지 않는다.</b> 나오기 시작하면 분리가 무너진
/// 것이다. "누가 위치를 쥐나"는 이 리그를 소유하는 쪽(<see cref="PlayerRagdoll"/>·<c>NpcRagdoll</c>)이
/// 각자 쥔다.
///
/// <b>설계 근거·실측·되살리면 안 되는 것들은 <c>docs/ragdoll-rig.md</c>에 있다.</b>
/// 이 파일을 고치기 전에 그쪽의 해당 항목을 먼저 볼 것.
///
/// 붙이는 곳: 리그 최상단(<see cref="m_boneRootName"/>)을 <b>직속 자식으로</b> 가진 오브젝트.
/// </summary>
public class RagdollRig : MonoBehaviour
{
    /// <summary>래그돌 뼈 콜라이더 전용 레이어 — <c>RagdollSetup</c>이 만든다.</summary>
    public const string k_layerName = "Ragdoll";

    /// <summary>리그 최상단의 기본 이름(Synty 관례) — <c>RagdollSetup</c>이 같은 값을 쓴다.</summary>
    public const string k_defaultBoneRootName = "Root";

    // ---- 프리팹에 저장되지 않는 Rigidbody 값 — 런타임에 다시 건다 (docs §2) ----

    // 겹침 탈출 속도 상한. 이 값이 곧 시체가 튀어오르는 높이다(0.5m/s = 1.3cm).
    // 엔진·프로젝트 기본값은 10이라 이 값은 그 1/20이다.
    //
    // ⚠ <b>한때 이 값을 슬로모션(#759)의 용의자로 봤으나 무죄로 끝났다</b> — 원인은 루트 추종이
    // 프레임마다 돌던 것이었다(docs/759 §2·§4-A). 인스펙터로 빼 둔 것은 그때의 A/B 실험 잔재이고,
    // 아래 필드가 정본·이 상수가 기본값인 구성은 그대로 둔다.
    private const float k_maxDepenetrationVelocity = 0.5f;

    // 관절 projection을 껐으므로 관절을 붙드는 일은 전부 solver 반복이 맡는다 (기본 6/1로는 늘어난다).
    private const int k_solverIterations = 12;
    private const int k_solverVelocityIterations = 4;

    [Tooltip("몸통 리그 최상단의 이름 — 이 오브젝트의 <b>직속</b> 자식이어야 한다.\n\n" +
             "⚠ 범위를 여기로 못박는 것이 핵심이다. 플레이어 프리팹에는 뼈 이름이 같은 리그가 " +
             "두 벌 있어(1인칭 팔) 프리팹 전체를 훑으면 그쪽이 걸린다 — docs/ragdoll-rig.md §3")]
    [SerializeField] private string m_boneRootName = k_defaultBoneRootName;

    [Tooltip("겹친 콜라이더를 밀어내는 속도 상한(m/s) — <b>직렬화되지 않는 Rigidbody 값이라 런타임에 " +
             "다시 건다.</b>\n\n" +
             "<b>0.5는 엔진 기본값(10)의 1/20이다.</b> 튀어오름을 막으려고 조인 값인데(0.5m/s = 1.3cm), " +
             "그 대가로 <b>접촉 해소가 느려진다</b> — 겹친 상태로 있는 몸은 초당 이 값만큼만 빠져나온다.\n\n" +
             "⚠ 올리면 겹침에서 <b>튀어오름</b>이 커진다(10m/s면 수십 cm). 슬로모션(#759)의 원인이 " +
             "아님은 실측으로 확인됐으므로(docs/759 §4-A) 바꿀 이유가 있을 때만 건드릴 것.")]
    [SerializeField] private float m_maxDepenetrationVelocity = k_maxDepenetrationVelocity;

    [Tooltip("골반보다 높은 뼈에 얹는 추가 속도 비율(1/m) — 상체가 더 빨라 다리가 끌리는 텀블이 생긴다")]
    [SerializeField] private float m_tumbleBias = 0.8f;

    private Transform m_boneRoot; // 리그 최상단 — 뼈·스킨 수집 범위를 여기로 못박는다

    private Rigidbody[] m_bodies; // 래그돌 레이어의 뼈 Rigidbody만 (손에 든 아이템의 rb가 섞이지 않게)
    private Collider[] m_boneColliders; // 위와 같은 순서 — 뼈마다 하나 (위저드가 그렇게 만든다)
    private CharacterJoint[] m_joints; // 위와 같은 순서 — 골반만 null. 뼈 체인 질의(#980)가 쓴다
    private float[] m_baseLinearDamping; // 감쇠를 풀 때 되돌릴 평시 값 — 프리팹이 진실이라 상수로 박지 않는다
    private float[] m_baseAngularDamping;

    private Transform m_hipsBone; // 관절이 없는 뼈 = 래그돌 루트
    private Rigidbody m_hipsBody;
    private Transform m_headBone; // 누운 방향(yaw) 계산용

    // 몸통 스킨드 메시 — 래그돌 동안 컬링 바운즈를 매 프레임 재계산시켜야 한다 (docs §7).
    // 뼈와 필드를 공유하지 않아 <see cref="RagdollSkins"/>로 갈라냈다.
    private RagdollSkins m_skins = RagdollSkins.Empty;

    // "i번째 뼈가 무엇이며 누구에게 매달렸나" — 위 배열들을 읽기만 하는 질의 묶음 (#980).
    private RagdollBoneGraph m_boneGraph = RagdollBoneGraph.Empty;

    private Vector3[] m_capturedPositions; // 캡처한 월드 포즈 (재정렬 전후를 잇는다)
    private Quaternion[] m_capturedRotations;

    // ---- 바인드 포즈 (프리팹이 authoring한 자세) ----
    // 관절의 connectedAnchor가 여기 구워지므로 뼈 길이가 틀어지면 관절이 위반 상태로 출발한다 (docs §5).
    // 리지드바디 뼈만이 아니라 <b>리그 전체</b>를 담는다 — 포즈 복사가 훑는 범위와 같아야 한다.
    private RagdollBindPose m_bindPose = RagdollBindPose.Empty;

    /// <summary>
    /// <b>자세 한 벌 — 복제되는 뼈 전부.</b> <see cref="m_bodies"/>에 그 사이를 잇는 <b>체인 뼈</b>를
    /// 더한 것이다(빼면 상체가 Spine_01 하나에 매달린다). 순서·포함 규칙은 docs §4.
    /// </summary>
    private Transform[] m_poseBones;

    /// <summary>뼈를 제대로 찾았는가 — 거짓이면 소유자는 래그돌 기능 전체를 꺼야 한다.</summary>
    public bool IsValid => m_bodies != null && m_bodies.Length > 0 && m_hipsBone != null;

    /// <summary>골반 — 관절이 없는 뼈. 래그돌의 기준점이자 위치 대리값의 추종 대상.</summary>
    public Transform Hips => m_hipsBone;

    /// <summary>같은 뼈의 Rigidbody — 밧줄 관절이 여기 붙는다.</summary>
    public Rigidbody HipsBody => m_hipsBody;

    /// <summary>리그 최상단 — 스킨 판정·계층 질의용.</summary>
    public Transform BoneRoot => m_boneRoot;

    /// <summary>
    /// <b>자세 한 벌을 이루는 뼈 수</b> — 스트림 페이로드의 길이이자 피어 간 배선 검증값.
    /// <see cref="m_bodies"/>보다 많다(docs §4).
    /// </summary>
    public int BoneCount => m_poseBones != null ? m_poseBones.Length : 0;

    /// <summary>
    /// 가장 낮은 뼈의 월드 y — 시체가 지면을 파고드는지 재는 값.
    /// ⚠ <c>Rigidbody.position</c>이 아니라 <b>트랜스폼</b>을 읽는다 — 이유는 docs §6.
    /// </summary>
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

    /// <summary>
    /// 뼈 수집을 보장한다 — <b>멱등</b>이다. 같은 오브젝트의 Awake 순서가 보장되지 않으므로
    /// 이 리그를 쓰는 컴포넌트는 자기 <c>Awake</c> 첫머리에서 부른다 (docs §13).
    /// </summary>
    public void EnsureCollected()
    {
        if (m_collected)
            return;

        m_collected = true;
        Collect();
    }

    // ---- 수집 ----

    // 리그 최상단 아래의 래그돌 레이어 뼈·콜라이더·골반을 모으고 런타임 물리값과 바인드 포즈를 잡는다.
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

            // 관절이 없는 뼈가 래그돌 루트(골반)다 — 빌더가 하나만 그렇게 만든다
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

        // 감쇠를 풀 때 되돌릴 자리 — 여기서 읽어 두면 항상 프리팹이 진실이다.
        m_baseLinearDamping = new float[count];
        m_baseAngularDamping = new float[count];

        // 뼈 콜라이더 — 위저드가 뼈마다 하나씩 만든다. 켜고 끄는 것과 충돌 무시가 같은 배열을 쓴다.
        m_boneColliders = new Collider[count];
        m_joints = new CharacterJoint[count];
        for (int i = 0; i < count; i++)
        {
            m_baseLinearDamping[i] = m_bodies[i].linearDamping;
            m_baseAngularDamping[i] = m_bodies[i].angularDamping;
            m_boneColliders[i] = m_bodies[i].GetComponent<Collider>();
            m_joints[i] = m_bodies[i].GetComponent<CharacterJoint>();
        }

        // 골반(관절 없는 뼈)이 없으면 정착 재정렬·임펄스 기준이 없다 — 반쯤 도는 것보다 끄는 편이 낫다
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

        ApplyRuntimePhysics(); // 프리팹이 들고 있을 수 없는 값 (docs §2)

        // 아직 아무도 리그를 건드리지 않은 지금이 바인드를 담을 유일한 기회다.
        m_bindPose = RagdollBindPose.Capture(m_boneRoot, m_bodies);
        m_poseBones = m_bindPose.BuildPoseBones();

        // 뼈 계층 질의는 배열이 다 찬 뒤에 만든다 — 읽기만 하므로 배열의 주인은 계속 여기다.
        m_boneGraph = new RagdollBoneGraph(m_bodies, m_boneColliders, m_joints, m_hipsBody, m_headBone);

        m_skins = RagdollSkins.Collect(transform, m_boneRoot);
        SetKinematic(true); // 평시는 애니메이터가 포즈를 쥔다
    }

    // 직렬화되지 않는 Rigidbody 값을 인스턴스마다 다시 건다 (docs §2).
    private void ApplyRuntimePhysics()
    {
        for (int i = 0; i < m_bodies.Length; i++)
        {
            m_bodies[i].maxDepenetrationVelocity = m_maxDepenetrationVelocity;
            m_bodies[i].solverIterations = k_solverIterations;
            m_bodies[i].solverVelocityIterations = k_solverVelocityIterations;
        }
    }

    // ---- 물리 on/off ----

    /// <summary>
    /// 전 뼈를 키네마틱(애니메이터가 포즈를 쥠) ↔ 물리 사이에서 전환한다.
    /// 물리로 넘기기 전 <c>Physics.SyncTransforms()</c>가 필수다 — 안 하면 물리가 액터가 들고 있던
    /// 옛 포즈에서 출발한다(실측 81.7°). 반대 방향에는 필요 없다 (docs §6).
    /// </summary>
    public void SetKinematic(bool kinematic)
    {
        if (m_bodies == null)
            return;

        if (!kinematic)
            Physics.SyncTransforms();

        for (int i = 0; i < m_bodies.Length; i++)
            SetBodyKinematic(m_bodies[i], kinematic);
    }

    // 보간을 키네마틱 여부와 함께 갈아탄다(물리 중 Interpolate / 키네마틱 None).
    // 속도는 <b>양쪽 전이에서 모두</b> 지운다 — 넘기기 전에, 돌려준 뒤에. 근거는 docs §6.
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

    /// <summary>
    /// 이 리그의 뼈 콜라이더와 <paramref name="other"/>의 충돌을 켜고 끈다 — 소유자의 이동 대리값
    /// (캡슐 등)과 겹친 채 출발하지 않게 한다.
    /// ⚠ 콜라이더를 껐다 켜면 이 상태가 초기화된다(Unity 사양) — 다시 거는 책임은 소유자에게 있다.
    /// </summary>
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

    /// <summary>
    /// 이 리그가 구동하는 스킨드 메시 — 컬링을 다루는 자리다(<c>Skins.SetAlwaysVisible</c>).
    /// 리그를 못 찾았으면 <see cref="RagdollSkins.Empty"/>라 호출이 무동작이다.
    /// </summary>
    public RagdollSkins Skins => m_skins;

    /// <summary>
    /// 뼈를 <b>하나씩·계층으로</b> 묻는 자리 — "i번째 뼈가 무엇이며 누구에게 매달렸나"(#980).
    /// 뼈를 못 찾았으면 <see cref="RagdollBoneGraph.Empty"/>라 질의가 비어 있는 답을 준다.
    /// </summary>
    public RagdollBoneGraph Bones => m_boneGraph;

    /// <summary>
    /// 프리팹이 authoring한 자세 — 되돌리는 자리다(<c>BindPose.RestoreAll</c>·
    /// <c>RestoreUnstreamedRotations</c>). ⚠ 둘 다 키네마틱일 때만 의미가 있다 (docs §5).
    /// </summary>
    public RagdollBindPose BindPose => m_bindPose;

    // ---- 힘·속도 ----

    /// <summary>
    /// 뼈가 하나라도 키네마틱인가 — <see cref="ApplyImpulse"/>가 <b>통째로 버려지는</b> 상태의 판정.
    /// 원격에서 자세를 받는 동안·정착한 시체가 이 상태다. 임펄스를 넣기 전에 물어볼 자리가 있다
    /// (<c>PlayerRagdoll.EnterRagdoll</c>의 재진입 분기 — docs/506-explosion-ragdoll.md §15).
    /// </summary>
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

    /// <summary>
    /// 전 뼈에 같은 속도를 주고, 골반보다 높은 뼈에만 조금 더 얹어 텀블을 만든다.
    /// 폭심 기준 <c>AddExplosionForce</c>를 쓰지 않는 이유는 결정론이다 (docs §8).
    /// </summary>
    public void ApplyImpulse(Vector3 velocity)
    {
        if (velocity == Vector3.zero || m_bodies == null || m_hipsBone == null)
            return;

        // 실제로 힘이 들어간 뼈 수 — 0이면 통째로 버려졌다는 뜻이고, 아래 경고가 그것을 잡는다.
        // 계측으로 넣었다가 <b>영구 가드로 남겼다</b>: 이 실패는 #768·#957에서 세 번 났고 매번
        // 조용했다(경고 하나 없이 "안 날아간다"로만 보인다). 근거는 docs/865-down-ragdoll.md §9-7.
        int applied = 0;

        float hipsHeight = m_hipsBone.position.y;
        for (int i = 0; i < m_bodies.Length; i++)
        {
            // 키네마틱 바디는 속도 대입이 무시되고 경고만 난다 — 형제 루프(ClampSpeed 등)와 같은 가드다.
            // 정착한 시체가 이 상태다: 뼈 12개가 전부 키네마틱이라 임펄스가 경고로만 남았다 (#768).
            if (m_bodies[i] == null || m_bodies[i].isKinematic)
                continue;

            float lift = m_bodies[i].worldCenterOfMass.y - hipsHeight;
            m_bodies[i].linearVelocity += velocity * (1f + m_tumbleBias * lift);
            applied++;
        }

        // ⚠ <b>힘을 실을 뼈가 하나도 없었다.</b> 뼈가 전부 키네마틱이라는 뜻이고, 그러면 임펄스가
        // 경고 없이 사라진다 — 이 저장소에서 세 번 난 고장이다:
        //  · 정착한 시체에 임펄스를 건 것 (#768)
        //  · 소유권이 방금 넘어와 뼈가 아직 원격 시절 키네마틱인 것 (§15)
        //  · 권위가 아닌 피어에 임펄스를 보낸 것 (§9-7)
        // 셋 다 "안 날아간다"로만 보였다. 조용히 넘기지 않는다.
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

    /// <summary>
    /// 전 뼈를 깨운다. <b>골반만 깨우면 안 된다</b> — 사지가 자고 있으면 몸이 한 덩어리로 끌려온다.
    /// </summary>
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

    /// <summary>
    /// <b>전 뼈가 잠들었는가</b> — 물리가 스스로 낸 "다 끝났다" 신호이자 정착 판정 그 자체다.
    /// 하나라도 깨어 있으면 거짓이고, 키네마틱 뼈는 세지 않는다 (docs §9).
    /// </summary>
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

    /// <summary>
    /// 전 뼈를 강제로 재운다 — <b>지형에 껴서 영영 안 자는 몸</b>의 안전망이다(타임아웃 경로).
    /// 얼리는 것이 아니라 <b>물리 수면</b>이라 밟히면 그 자리에서 그대로 이어진다.
    /// </summary>
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

    /// <summary>
    /// 전 뼈를 같은 델타로 강체 평행이동한다 — 포즈·상대속도·관절이 보존된다.
    /// 동적 바디의 <c>position</c> 대입은 텔레포트라 속도가 유도되지 않는다 (docs §6).
    /// </summary>
    public void TranslateBy(Vector3 delta)
    {
        if (m_bodies == null)
            return;

        for (int i = 0; i < m_bodies.Length; i++)
            m_bodies[i].position += delta;
    }

    // ---- 포즈 캡처 / 블렌드 ----

    /// <summary>전 뼈의 월드 포즈를 저장한다 — 루트를 옮기기 <b>전에</b> 부른다.</summary>
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

    /// <summary>저장한 월드 포즈를 되돌린다 — 루트를 옮긴 <b>뒤에</b> 부른다. 화면은 그대로다.</summary>
    public void RestoreCapturedPose()
    {
        if (m_bodies == null)
            return;

        // 부모가 먼저 와야 자식의 월드 포즈 대입이 헛되지 않는다 — 수집이 계층 순서를 준다.
        for (int i = 0; i < m_bodies.Length; i++)
            m_bodies[i].transform.SetPositionAndRotation(
                m_capturedPositions[i],
                m_capturedRotations[i]
            );
    }

    // ---- 로컬 포즈 스냅샷 (#571) ----
    // 월드 캡처와 용도가 다르다 — 이쪽은 <b>다른 피어로 보내는</b> 값이라 반드시 로컬이어야 한다.
    // 순서가 피어마다 같아야 성립한다 (docs §10).

    /// <summary>자세 뼈의 로컬 회전을 담아 간다 — 배열 길이는 <see cref="BoneCount"/>. (#571)</summary>
    /// <returns>담을 수 있으면 참 — 길이가 안 맞으면 거짓(아무것도 쓰지 않는다).</returns>
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

    /// <summary>담아 온 로컬 회전을 그대로 입힌다 — 원격 피어가 자세를 재현할 때 쓴다. (#571)</summary>
    /// <returns>입혔으면 참 — 길이가 안 맞으면 거짓.</returns>
    public bool ApplyLocalPose(Quaternion[] rotations, Vector3 hipsLocalPosition)
    {
        if (m_poseBones == null || rotations == null || rotations.Length != m_poseBones.Length)
            return false;

        // 골반이 먼저다 — 자식들의 월드 위치가 골반의 로컬 위치 위에 얹히기 때문.
        m_hipsBone.localPosition = hipsLocalPosition;

        for (int i = 0; i < m_poseBones.Length; i++)
            m_poseBones[i].localRotation = rotations[i];

        return true;
    }

    // ---- 뼈 길이 (#728 후속 5) ----
    // "뼈 길이는 관절이 유지하므로 상수다"가 시체에서는 거짓이다(무너지는 동안 늘어나 그대로 굳는다.
    // 실측 0.036m — 2026-09-02, 예전 기록은 0.206m였다).
    //
    // 나가는 자리가 둘이다: <b>정착·순간이동의 신뢰 1회 패킷</b>과, 무너지는 동안 흐르는
    // <b>낮은 주기의 언리라이어블 스트림</b>(RagdollPoseStreamer.m_lengthEveryFixedSteps, 기본 2Hz).
    // 후자를 더한 것은 정착 때 몰아 넘기면 원격이 한 프레임에 3.9cm를 입어 상체가 내려앉기
    // 때문이다. <b>25Hz 자세 스트림에는 여전히 싣지 않는다</b> (docs §11 · docs/npc-ragdoll.md §8).

    /// <summary>전 자세 뼈의 <b>로컬 위치</b>(= 뼈 길이)를 담아 간다 — 길이는 <see cref="BoneCount"/>.</summary>
    /// <returns>담았으면 참 — 길이가 안 맞으면 거짓(아무것도 쓰지 않는다).</returns>
    public bool CaptureBoneLengths(Vector3[] lengths)
    {
        if (m_poseBones == null || lengths == null || lengths.Length != m_poseBones.Length)
            return false;

        for (int i = 0; i < m_poseBones.Length; i++)
            lengths[i] = m_poseBones[i].localPosition;

        return true;
    }

    /// <summary>
    /// 담아 온 뼈 길이를 입힌다 — 원격 전용(키네마틱일 때만 의미가 있다).
    /// ⚠ 골반은 건너뛴다 — 그쪽 로컬 위치는 길이가 아니라 자세다 (docs §11).
    /// </summary>
    /// <returns>입혔으면 참 — 길이가 안 맞으면 거짓.</returns>
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

    // 부활 블렌드는 <see cref="RagdollPoseBlend"/>로 나갔다 (#571) — 블렌드는 살아있는 리그에서
    // 일어나는데 이 리그는 시체에 붙어 있다.

    // ---- 질의 ----

    // 몸이 "누웠다"고 보는 최소 기울기 — 수평 성분 / 전체 길이라 0.7은 약 45°다.
    // ⚠ 절대 길이로 재면 서 있는 몸도 통과해 죽는 순간 몸이 뒤집힌다 (docs §12).
    private const float k_lyingHorizontalRatio = 0.7f;

    /// <summary>
    /// 몸이 누운 방향의 yaw — 골반→머리를 지면에 투영한 값. <b>누워 있지 않으면 거짓을 낸다</b>
    /// (부르는 쪽은 그때 기존 yaw를 유지할 것). 기상 클립 보정은 소유자가 얹는다 (docs §12).
    /// </summary>
    public bool TryGetBodyYaw(out float yaw)
    {
        yaw = 0f;
        if (m_headBone == null || m_hipsBone == null)
            return false;

        Vector3 lengthwise = m_headBone.position - m_hipsBone.position;
        float length = lengthwise.magnitude;
        if (length < 0.02f)
            return false; // 두 뼈가 겹쳐 있다 — 뺄 방향이 없다

        Vector3 horizontal = new Vector3(lengthwise.x, 0f, lengthwise.z);
        if (horizontal.magnitude < length * k_lyingHorizontalRatio)
            return false; // 아직 서 있다 — 방향을 못 정하니 기존 yaw를 유지한다

        yaw = Quaternion.LookRotation(horizontal.normalized).eulerAngles.y;
        return true;
    }

    // ---- 뼈 하나만 물리에서 떼기 (#980 — 벽에 박힌 팔 접기) ----
    //
    // ⚠ 인덱스는 <see cref="Bones"/>가 주는 것과 <b>같다</b>(m_bodies / m_boneColliders / m_joints가
    // 같은 길이·같은 순서). "무엇이 몇 번 뼈인가"를 묻는 질의는 전부 그쪽에 있다.
    //
    // ⚠ <b>이 둘만 리그에 남긴 이유</b>: 아래 전이가 <see cref="SetKinematic"/>과 같은 헬퍼를 쓴다 —
    // "전이 양쪽에서 속도를 지운다"(docs §6)는 규칙이 두 클래스에 복제되면 한쪽만 고쳐진다.

    // i번째 뼈의 Rigidbody — 범위 밖이면 null. 아래 둘만 쓴다.
    private Rigidbody GetBody(int index) =>
        m_bodies != null && index >= 0 && index < m_bodies.Length ? m_bodies[index] : null;

    /// <summary>
    /// 그 뼈 <b>하나만</b> 물리에서 떼거나 돌려준다 — 전이 양쪽에서 속도를 지운다
    /// (<see cref="SetKinematic"/>과 같은 규칙, docs §6).
    /// 벽에 박힌 팔을 <b>회전으로</b> 고치는 동안 그 팔만 물리에서 떼는 데 쓴다 (#980).
    /// </summary>
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
