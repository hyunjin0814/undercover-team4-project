using Unity.AI.Navigation;
using UnityEngine;

/// <summary>
/// 차가 나오는 도로 구간 마커 — 놓인 방향이 진행 방향이며, 맵 밖에서 진입해 구간을 지나 회수된다.
/// 속도는 레인이 정한다.
/// </summary>
public class TrafficLane : MonoBehaviour
{
    [Header("주행")]
    [Tooltip("이 레인이 덮는 도로 구간 길이(m). 도로 직선 구간을 넘기지 말 것 — 실제 주행 거리는 아래 맵 밖 여유가 앞뒤로 더해진다")]
    [Min(5f)]
    [SerializeField] private float m_runDistance = 120f;

    [Header("맵 밖 여유 (#673)")]
    [Tooltip(
        "도로 구간 <b>앞</b>에 붙는 맵 밖 주행 거리(m) — 차가 태어나는 자리다.\n"
            + "⚠ 엔진음 maxDistance(AudioLibrary의 VehicleEngine, 현재 90)보다 커야 스폰 순간이 들리지 않는다"
    )]
    [Min(0f)]
    [SerializeField] private float m_approachDistance = 100f;

    [Tooltip("도로 구간 <b>뒤</b>에 붙는 맵 밖 주행 거리(m) — 회수 자리다. 같은 이유로 엔진음 maxDistance보다 클 것")]
    [Min(0f)]
    [SerializeField] private float m_exitDistance = 100f;

    [Tooltip("이 레인의 주행 속도(m/s) — 플레이어 전력질주(8)보다 충분히 빨라야 '피한다'가 성립한다. 레인 안에서는 모두 같은 속도다")]
    [Min(1f)]
    [SerializeField] private float m_speed = 22f;

    [Header("배출 간격 하한")]
    [Tooltip(
        "이 레인이 가로지르는 도로 폭(m) — 배출 하한(폭 ÷ 이동 속도 + 여유)의 근거다.\n"
            + "⚠ <b>폴백</b>이다 — 평소에는 Road 볼륨에서 실제 폭을 읽어 쓴다 (#673)"
    )]
    [Min(1f)]
    [SerializeField] private float m_crossWidth = 8f;

    [Tooltip("도로 폭을 읽어 올 Road 영역 볼륨 — 비워 두면 마커를 품은 볼륨을 씬에서 찾는다. 자동 탐색이 엉뚱한 것을 잡을 때만 지정한다")]
    [SerializeField] private NavMeshModifierVolume m_roadVolume;

    [Header("차종")]
    [Tooltip("TrafficManager의 차량 프리팹 목록에서 쓸 인덱스 — -1이면 매번 랜덤으로 고른다")]
    [SerializeField] private int m_vehicleIndex = -1;

    public Vector3 RoadPoint => transform.position;

    public Vector3 StartPoint => RoadPoint - Direction * m_approachDistance;

    public Vector3 Direction
    {
        get
        {
            Vector3 forward = transform.forward;
            forward.y = 0f;
            return forward.sqrMagnitude < 0.001f ? Vector3.zero : forward.normalized;
        }
    }

    public float RunDistance => m_approachDistance + m_runDistance + m_exitDistance;

    public float Speed => m_speed;

    public int VehicleIndex => m_vehicleIndex;

    /// <summary>배출 간격의 하한(초)을 계산한다(건너는 시간 + 여유).</summary>
    public float MinGapSeconds(float crossSpeed, float marginSeconds) =>
        CrossWidth / Mathf.Max(0.1f, crossSpeed) + Mathf.Max(0f, marginSeconds);

    public float CrossWidth => m_resolvedCrossWidth > 0f ? m_resolvedCrossWidth : m_crossWidth;

    private float m_resolvedCrossWidth;

    /// <summary>Road 볼륨에서 이 레인의 도로 폭을 읽어 둔다.</summary>
    public void ResolveCrossWidth(NavMeshModifierVolume[] volumes)
    {
        NavMeshModifierVolume volume = m_roadVolume != null ? m_roadVolume : FindRoadVolume(volumes);
        if (volume == null)
            return;

        float width = LateralExtentOf(volume);
        if (width > 0f)
            m_resolvedCrossWidth = width;
    }

    private NavMeshModifierVolume FindRoadVolume(NavMeshModifierVolume[] volumes)
    {
        if (volumes == null || NpcNavAreas.RoadMask == 0)
            return null;

        for (int i = 0; i < volumes.Length; i++)
        {
            NavMeshModifierVolume volume = volumes[i];
            if (volume == null || (1 << volume.area & NpcNavAreas.RoadMask) == 0)
                continue;

            Vector3 local = volume.transform.InverseTransformPoint(RoadPoint) - volume.center;
            Vector3 half = volume.size * 0.5f;
            if (Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.z) <= half.z)
                return volume;
        }

        return null;
    }

    private float LateralExtentOf(NavMeshModifierVolume volume)
    {
        Vector3 direction = Direction;
        if (direction == Vector3.zero)
            return 0f;

        Vector3 lateral = Vector3.Cross(Vector3.up, direction);
        Transform box = volume.transform;
        Vector3 half = Vector3.Scale(volume.size, box.lossyScale) * 0.5f;

        return 2f
            * (Mathf.Abs(Vector3.Dot(lateral, box.right * half.x))
                + Mathf.Abs(Vector3.Dot(lateral, box.up * half.y))
                + Mathf.Abs(Vector3.Dot(lateral, box.forward * half.z)));
    }

    private void OnDrawGizmos()
    {
        Vector3 direction = Direction;
        if (direction == Vector3.zero)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(StartPoint, 2f);
            return;
        }

        Vector3 roadStart = RoadPoint;
        Vector3 roadEnd = roadStart + direction * m_runDistance;
        Vector3 spawn = StartPoint;
        Vector3 end = spawn + direction * RunDistance;
        Vector3 right = Vector3.Cross(Vector3.up, direction);

        Gizmos.color = Color.green;
        Gizmos.DrawLine(roadStart, roadEnd);
        Gizmos.DrawWireSphere(roadStart, 1f);

        Gizmos.color = new Color(0.6f, 0.6f, 0.6f, 0.8f);
        Gizmos.DrawLine(spawn, roadStart);
        Gizmos.DrawLine(roadEnd, end);
        Gizmos.DrawWireSphere(spawn, 1.5f);

        Gizmos.DrawLine(end, end - direction * 3f + right * 1.5f);
        Gizmos.DrawLine(end, end - direction * 3f - right * 1.5f);

        Gizmos.color = new Color(0f, 1f, 0f, 0.35f);
        float crossWidth = CrossWidth;
        Gizmos.DrawLine(roadStart - right * (crossWidth * 0.5f), roadStart + right * (crossWidth * 0.5f));
    }
}
