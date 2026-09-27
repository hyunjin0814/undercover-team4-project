using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 닫힌 문 자리의 NavMesh를 NavMeshObstacle carving으로 도려내 NPC도 닫힌 문을 지나지 못하게 한다.
/// DoubleDoor와 같은 오브젝트에 붙이며, 크기는 Awake 시점의 닫힌 문짝 부피로 잡는다.
/// </summary>
[RequireComponent(typeof(DoubleDoor))]
public class DoorNavBlocker : MonoBehaviour
{
    [Header("차단막 크기 (문짝에서 잰 값에 얹는다)")]
    [Tooltip("문틀 좌우로 넓힐 여유(m). 문짝과 문틀 사이 틈으로 경로가 새지 않게 조금 넉넉히 잡는다")]
    [SerializeField] private float m_widthPadding = 0.2f;

    [Tooltip("차단막 두께(m) — 문짝은 얇아서 그대로 쓰면 carving이 폴리곤을 못 잘라내는 경우가 있다")]
    [SerializeField] private float m_minThickness = 0.5f;

    private DoubleDoor m_door;
    private NavMeshObstacle m_obstacle;

    private void Awake()
    {
        m_door = GetComponent<DoubleDoor>();
        m_obstacle = BuildObstacle();
    }

    private void OnEnable()
    {
        if (m_obstacle == null)
            return;

        m_door.OnOpenChanged += HandleOpenChanged;
        Apply(m_door.IsOpen);
    }

    private void OnDisable()
    {
        m_door.OnOpenChanged -= HandleOpenChanged;
    }

    private void HandleOpenChanged(bool open) => Apply(open);

    private void Apply(bool open)
    {
        if (m_obstacle != null)
            m_obstacle.enabled = !open;
    }

    /// <summary>닫힌 문짝이 차지하는 부피(문 로컬 좌표 기준)만큼의 carving 상자를 만든다. 문짝이 없으면 null.</summary>
    private NavMeshObstacle BuildObstacle()
    {
        if (!TryMeasureLeaves(out Bounds local))
        {
            Debug.LogWarning($"DoorNavBlocker: 잴 문짝이 없어 차단막을 만들지 못했다: {name}", this);
            return null;
        }

        Vector3 size = local.size;
        size.x += m_widthPadding;
        size.z = Mathf.Max(size.z, m_minThickness);

        var holder = new GameObject("NavBlocker");
        holder.transform.SetParent(transform, false);
        holder.transform.localPosition = local.center;

        NavMeshObstacle obstacle = holder.AddComponent<NavMeshObstacle>();
        obstacle.shape = NavMeshObstacleShape.Box;
        obstacle.size = size;
        obstacle.center = Vector3.zero;

        obstacle.carving = true;

        obstacle.carveOnlyStationary = true;

        return obstacle;
    }

    private bool TryMeasureLeaves(out Bounds local)
    {
        local = default;
        bool started = false;

        Encapsulate(m_door.LeafLeft, ref local, ref started);
        Encapsulate(m_door.LeafRight, ref local, ref started);

        return started;
    }

    private void Encapsulate(Transform leaf, ref Bounds local, ref bool started)
    {
        if (leaf == null)
            return;

        Renderer[] renderers = leaf.GetComponentsInChildren<Renderer>();

        for (int i = 0; i < renderers.Length; i++)
        {
            Bounds world = renderers[i].bounds;

            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 sign = new Vector3(
                    (corner & 1) == 0 ? -1f : 1f,
                    (corner & 2) == 0 ? -1f : 1f,
                    (corner & 4) == 0 ? -1f : 1f
                );

                Vector3 point = transform.InverseTransformPoint(
                    world.center + Vector3.Scale(world.extents, sign)
                );

                if (started)
                {
                    local.Encapsulate(point);
                }
                else
                {
                    local = new Bounds(point, Vector3.zero);
                    started = true;
                }
            }
        }
    }
}
