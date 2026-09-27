using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 맵 상공을 늘 배회하며 빔을 켜 둔 UFO 기체의 이동과 겉모습을 담당하는 상주 기믹.
/// 위치는 서버가 움직이고, 빔 길이는 각 피어가 아래로 레이를 쏴 스스로 잰다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class UfoCraft : NetworkBehaviour
{
    [Header("빔")]
    [Tooltip("빔 기둥의 뿌리 — 기체 원점에 두고 [b]아래로 2유닛[/b] 길이의 메시를 자식으로 둘 것. " +
             "이 트랜스폼의 배율로 굵기와 길이를 맞춘다. 비우면 빔이 안 보일 뿐 판정은 그대로 돈다")]
    [SerializeField] private Transform m_beamPivot;

    [Tooltip("빔 반경(m) — 보이는 굵기와 걸리는 범위가 모두 이 값이다")]
    [Min(0.5f)]
    [SerializeField] private float m_beamRadius = 3f;

    [Tooltip("지면을 찾을 때 볼 레이어 — 환경만 넣을 것. 사람이 들어가면 머리 위에서 빔이 끊긴다")]
    [SerializeField] private LayerMask m_groundMask = 1;

    [Tooltip("이 거리(m) 안에서 지면을 못 찾으면 기체 바로 아래를 지면으로 친다")]
    [Min(1f)]
    [SerializeField] private float m_groundProbeDistance = 200f;

    private static readonly RaycastHit[] s_groundHitBuffer = new RaycastHit[64];

    [Header("배회")]
    [Tooltip("씬에 놓인 처음 자리를 중심으로 이 반경(m) 안을 떠다닌다")]
    [Min(1f)]
    [SerializeField] private float m_roamRadius = 90f;

    [Tooltip("이동 속도(m/s) — 느릴수록 피할 시간이 길어진다. 이 값이 곧 난이도다")]
    [Min(0.1f)]
    [SerializeField] private float m_roamSpeed = 5f;

    [Tooltip("다음 목적지에 이 거리(m) 안까지 오면 새 목적지를 고른다")]
    [Min(0.5f)]
    [SerializeField] private float m_arriveDistance = 3f;

    [Header("연출")]
    [Tooltip("기체가 제자리에서 도는 속도(도/초)")]
    [SerializeField] private float m_spinDegreesPerSecond = 20f;

    [Tooltip("떠 있는 높이를 위아래로 흔드는 폭(m). 0이면 흔들지 않는다")]
    [Min(0f)]
    [SerializeField] private float m_bobAmplitude = 0.5f;

    [Tooltip("위아래 흔들림 한 주기(초)")]
    [Min(0.1f)]
    [SerializeField] private float m_bobPeriod = 4f;

    private Vector3 m_home;
    private Vector3 m_destination;
    private float m_bobPhase;
    private bool m_held;
    private UfoBeamGroundField m_groundField;

    public float BeamRadius => m_beamRadius;

    public LayerMask GroundMask => m_groundMask;

    public float GroundProbeDistance => m_groundProbeDistance;

    /// <summary>기체를 제자리에 세우거나 다시 배회시킨다. 서버 전용.</summary>
    public void ServerSetHold(bool held) => m_held = held;

    private void Awake()
    {
        m_home = transform.position;
        m_destination = m_home;
        m_groundField = GetComponent<UfoBeamGroundField>();

        m_bobPhase = Random.Range(0f, Mathf.PI * 2f);

        if (m_beamPivot != null)
            m_beamPivot.gameObject.SetActive(true);
    }

    /// <summary>기체 바로 아래 지면 지점을 레이로 찾는다. 못 찾으면 폴백 거리를 쓴다.</summary>
    public Vector3 BeamGroundPoint() => BeamGroundPoint(out _);

    /// <summary>지면 지점과 실제 레이 적중 여부를 함께 돌려준다.</summary>
    private Vector3 BeamGroundPoint(out bool grounded)
    {
        Vector3 origin = transform.position;

        int count = Physics.RaycastNonAlloc(
            origin, Vector3.down, s_groundHitBuffer, m_groundProbeDistance,
            m_groundMask, QueryTriggerInteraction.Ignore);

        if (count == 0)
        {
            grounded = false;
            return origin + Vector3.down * m_groundProbeDistance;
        }

        int farthest = 0;
        for (int i = 1; i < count; i++)
        {
            if (s_groundHitBuffer[i].distance > s_groundHitBuffer[farthest].distance)
                farthest = i;
        }

        grounded = true;
        return s_groundHitBuffer[farthest].point;
    }

    private void Update()
    {
        if (m_spinDegreesPerSecond != 0f)
            transform.Rotate(Vector3.up, m_spinDegreesPerSecond * Time.deltaTime, Space.World);

        StretchBeam();

        if (!HasServerAuthority)
            return;

        Vector3 position = transform.position;
        position.y -= BobOffset();

        if (!m_held)
        {
            position = Vector3.MoveTowards(position, m_destination, m_roamSpeed * Time.deltaTime);

            Vector3 flat = m_destination - position;
            flat.y = 0f;
            if (flat.sqrMagnitude <= m_arriveDistance * m_arriveDistance)
                PickDestination();

            m_bobPhase += m_bobPeriod > 0f ? Time.deltaTime * (Mathf.PI * 2f / m_bobPeriod) : 0f;
        }

        position.y += BobOffset();
        transform.position = position;
    }

    private void PickDestination()
    {
        Vector2 offset = Random.insideUnitCircle * m_roamRadius;
        m_destination = new Vector3(m_home.x + offset.x, m_home.y, m_home.z + offset.y);
    }

    private float BobOffset() => m_bobAmplitude <= 0f ? 0f : Mathf.Sin(m_bobPhase) * m_bobAmplitude;

    private void StretchBeam()
    {
        if (m_beamPivot == null)
            return;

        Vector3 groundPoint = BeamGroundPoint(out bool grounded);

        float bottom = m_groundField != null && m_groundField.HasField
            ? Mathf.Min(groundPoint.y, m_groundField.LowestGround)
            : groundPoint.y;

        float length = grounded ? Mathf.Max(0.1f, transform.position.y - bottom) : 0f;
        float diameter = m_beamRadius * 2f;
        m_beamPivot.localScale = new Vector3(diameter, length * 0.5f, diameter);
    }

    private bool HasServerAuthority =>
        NetworkManager.Singleton == null
        || !NetworkManager.Singleton.IsListening
        || NetworkManager.Singleton.IsServer;
}
