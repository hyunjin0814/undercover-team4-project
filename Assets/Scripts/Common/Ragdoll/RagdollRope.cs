using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 시체에 밧줄(거리 제한 관절)을 묶어 운반자에게 물리로 끌려오게 한다.
/// 운반자 한 명당 한 가닥씩 걸리며, 네트워크·권위는 모르고 각 피어가 로컬로 처리한다.
/// </summary>
[RequireComponent(typeof(RagdollRig))]
public class RagdollRope : MonoBehaviour
{
    [Tooltip("밧줄 길이(m) — 운반자의 손과 시체 골반 사이의 최대 거리. 이 안에서는 시체가 자유롭고, " +
             "넘어가면 아래 강성·감쇠가 잡는다. 길게 잡으면 장력이 덜 걸려 전체적으로 순해진다. " +
             "⚠ 앵커가 손(약 1.1m)이라 손 높이보다 짧으면 시체가 바닥에 닿지 못하고 매달린다. " +
             "바닥에 누운 채 끌리는 수평거리 = √(길이² − (손높이 − 골반높이)²) — " +
             "2.0이면 약 1.8m 뒤에서 끌린다")]
    [SerializeField] private float m_length = 2f;

    [Tooltip("밧줄이 한계를 넘었을 때 되당기는 강성 — <b>한계 바깥에서만</b> 작동한다(늘어져 있으면 " +
             "힘이 0이라 시체를 들어올리지 못한다). 0이면 하드 리밋이 되어 위반량을 한 스텝에 " +
             "해소하며 시체를 11m/s로 튕긴다. 시체 70kg을 마찰(약 412N)에 맞서 끌려면 1500에서 " +
             "약 27cm 늘어난다 — 밧줄이 하중을 받아 늘어나는 정도라 자연스럽다")]
    [SerializeField] private float m_limitSpring = 1500f;

    [Tooltip("같은 한계의 감쇠 — <b>과감쇠로 둔다.</b> 임계는 약 2√(강성×질량) = 2√(1500×70) ≈ 650이고 " +
             "1000이면 ζ≈1.5다. 부족감쇠(예전 3000/600, ζ≈0.65)면 팽팽해질 때마다 오버슛으로 속도를 " +
             "얹는데, 늘어진 반주기에는 이 감쇠가 0이라 뺄 방법이 없다 — 운반자가 제자리에서 돌면 " +
             "회전 주기마다 에너지가 쌓여 시체가 점점 빨라지고 놓는 순간 날아간다 (§9-17)")]
    [SerializeField] private float m_limitDamper = 1000f;

    [Tooltip("밧줄에 묶인 동안 뼈에 거는 선형 감쇠(1/s) — <b>늘어진 구간의 유일한 에너지 배출구다.</b> " +
             "한계 감쇠는 밧줄이 팽팽할 때만 작동하므로 이것이 없으면 넣기만 하고 빼지 않는 펌프가 된다. " +
             "0.6이면 시정수 약 1.7초. 끌리는 저항이 늘어 밧줄이 조금 더 늘어난다(2m/s에서 약 84N)")]
    [SerializeField] private float m_dragLinearDamping = 0.6f;

    [Tooltip("같은 구간의 각 감쇠 — 팽이처럼 계속 도는 것을 잡는다. 평시 뼈 값은 0.05로 사실상 없다. " +
             "너무 올리면 끌릴 때 몸이 뻣뻣해져 흐느적임이 죽으므로 선형 감쇠부터 올려 볼 것")]
    [SerializeField] private float m_dragAngularDamping = 0.6f;

    [Tooltip("밧줄에 묶인 동안 뼈 속도의 <b>하드 상한</b>(m/s) — 슬링 차단용이다. 0이면 끈다. " +
             "감쇠로는 못 막는다: 운반자가 달리며 원을 그리면 장력이 하는 일이 ω²로 커지는데 " +
             "감쇠 배출은 v에 비례해, 빨리 돌수록 입력이 이긴다(§9-18). 기본 8은 스프린트 속도와 " +
             "같다 — 끌려가는 시체가 끄는 사람보다 빠를 이유는 없고, 넘는 만큼은 전부 슬링이다")]
    [SerializeField] private float m_maxSpeed = 8f;

    private RagdollRig m_rig;

    private class Strand
    {
        public Transform Carrier;
        public Rigidbody Anchor;
        public GameObject AnchorObject;
        public ConfigurableJoint Joint;
    }

    private readonly List<Strand> m_strands = new List<Strand>();

    private Vector4 m_appliedTuning;
    private float m_appliedAngularDamping;

    public float Length => m_length;

    public bool IsAttached => m_strands.Count > 0;

    /// <summary>이 사람이 쥔 가닥이 걸려 있는가.</summary>
    public bool IsAttachedTo(Transform carrier) => IndexOf(carrier) >= 0;

    public bool IsBeingCarried
    {
        get
        {
            for (int i = 0; i < m_strands.Count; i++)
                if (m_strands[i].Joint != null && m_strands[i].Carrier != null)
                    return true;

            return false;
        }
    }

    private int IndexOf(Transform carrier)
    {
        if (carrier == null)
            return -1;

        for (int i = 0; i < m_strands.Count; i++)
            if (m_strands[i].Carrier == carrier)
                return i;

        return -1;
    }

    private bool TuningChanged =>
        m_appliedTuning != new Vector4(m_length, m_limitSpring, m_limitDamper, m_dragLinearDamping)
        || m_appliedAngularDamping != m_dragAngularDamping;

    private void Awake()
    {
        m_rig = GetComponent<RagdollRig>();
        m_rig.EnsureCollected();
    }

    private void OnDestroy()
    {
        for (int i = 0; i < m_strands.Count; i++)
            if (m_strands[i].AnchorObject != null)
                Destroy(m_strands[i].AnchorObject);
    }

    private void FixedUpdate() => Tick();

    /// <summary>밧줄 앵커(부모 없는 키네마틱 Rigidbody)를 보장한다(멱등).</summary>
    private Rigidbody CreateAnchor(Strand strand)
    {
        if (m_rig == null || !m_rig.IsValid)
            return null;

        GameObject anchor = new GameObject($"RopeAnchor ({name})");
        Rigidbody body = anchor.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;

        strand.AnchorObject = anchor;
        strand.Anchor = body;
        return body;
    }

    /// <summary>운반자에게 밧줄 한 가닥을 묶는다. 이미 묶여 있으면 무동작이다.</summary>
    public void Attach(Transform carrier)
    {
        if (carrier == null || m_rig == null || m_rig.HipsBody == null)
            return;
        if (IsAttachedTo(carrier))
            return;

        var strand = new Strand { Carrier = carrier };

        if (CreateAnchor(strand) == null)
            return;

        strand.Anchor.position = carrier.position;

        ConfigurableJoint joint = m_rig.HipsBody.gameObject.AddComponent<ConfigurableJoint>();
        joint.autoConfigureConnectedAnchor = false;
        joint.anchor = Vector3.zero;
        joint.connectedAnchor = Vector3.zero;
        joint.connectedBody = strand.Anchor;

        joint.xMotion = ConfigurableJointMotion.Limited;
        joint.yMotion = ConfigurableJointMotion.Limited;
        joint.zMotion = ConfigurableJointMotion.Limited;

        joint.angularXMotion = ConfigurableJointMotion.Free;
        joint.angularYMotion = ConfigurableJointMotion.Free;
        joint.angularZMotion = ConfigurableJointMotion.Free;

        joint.projectionMode = JointProjectionMode.None;
        joint.enableCollision = false;

        strand.Joint = joint;
        m_strands.Add(strand);

        ApplyTuning();
        m_rig.WakeAll();
    }

    /// <summary>이 운반자의 밧줄 가닥만 푼다(멱등). 남은 가닥은 계속 끈다.</summary>
    public void Detach(Transform carrier)
    {
        int index = IndexOf(carrier);
        if (index < 0)
            return;

        DestroyStrand(m_strands[index]);
        m_strands.RemoveAt(index);

        if (m_strands.Count == 0)
            m_rig?.RestoreDamping();
    }

    /// <summary>걸린 가닥을 전부 푼다 — 내려놓기·부활·사망 정리.</summary>
    public void Detach()
    {
        if (m_strands.Count == 0)
            return;

        for (int i = 0; i < m_strands.Count; i++)
            DestroyStrand(m_strands[i]);

        m_strands.Clear();
        m_rig?.RestoreDamping();
    }

    private void DestroyStrand(Strand strand)
    {
        if (strand.Joint != null)
        {
            strand.Joint.xMotion = ConfigurableJointMotion.Free;
            strand.Joint.yMotion = ConfigurableJointMotion.Free;
            strand.Joint.zMotion = ConfigurableJointMotion.Free;

            Destroy(strand.Joint);
        }

        if (strand.AnchorObject != null)
            Destroy(strand.AnchorObject);

        strand.Joint = null;
        strand.Anchor = null;
        strand.AnchorObject = null;
        strand.Carrier = null;
    }

    private void Tick()
    {
        if (m_strands.Count == 0)
            return;

        if (TuningChanged)
            ApplyTuning();

        bool anyCarried = false;
        for (int i = 0; i < m_strands.Count; i++)
        {
            Strand strand = m_strands[i];
            if (strand.Anchor == null || strand.Carrier == null)
                continue;

            anyCarried = true;
            strand.Anchor.MovePosition(strand.Carrier.position);
        }

        if (!anyCarried)
            return;

        if (m_rig.HipsBody != null && m_rig.HipsBody.IsSleeping())
            m_rig.WakeAll();

        m_rig.ClampSpeed(m_maxSpeed);
    }

    /// <summary>밧줄 길이·한계 스프링·뼈 감쇠 튜닝 값을 현재 가닥들에 적용한다.</summary>
    private void ApplyTuning()
    {
        if (m_strands.Count == 0)
            return;

        for (int i = 0; i < m_strands.Count; i++)
        {
            ConfigurableJoint joint = m_strands[i].Joint;
            if (joint == null)
                continue;

            joint.linearLimit = new SoftJointLimit { limit = Mathf.Max(0.1f, m_length) };
            joint.linearLimitSpring = new SoftJointLimitSpring
            {
                spring = m_limitSpring,
                damper = m_limitDamper,
            };
        }

        m_rig.SetDamping(m_dragLinearDamping, m_dragAngularDamping);

        m_appliedTuning = new Vector4(m_length, m_limitSpring, m_limitDamper, m_dragLinearDamping);
        m_appliedAngularDamping = m_dragAngularDamping;
    }
}
