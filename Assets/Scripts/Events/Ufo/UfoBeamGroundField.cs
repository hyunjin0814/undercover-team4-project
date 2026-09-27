using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

/// <summary>
/// UFO 기체 아래를 격자로 레이캐스트해 지면 높이맵 텍스처를 굽는다. 빔 셰이더가 이를 읽어 지면 아래를 잘라낸다.
/// </summary>
[RequireComponent(typeof(UfoCraft))]
public class UfoBeamGroundField : MonoBehaviour
{
    [Tooltip("빔 기둥의 렌더러 — 이 렌더러의 재질을 복제해 높이맵을 물린다. 비우면 굽지 않는다")]
    [SerializeField] private Renderer m_beamRenderer;

    [Tooltip("높이맵 한 변의 칸 수 — 32면 레이 1024발. 올리면 지붕 경계가 날카로워지지만 레이 수가 제곱으로 는다")]
    [Range(8, 64)]
    [SerializeField] private int m_grid = 32;

    [Tooltip("기둥 반경 바깥으로 더 재는 여유(m) — 기둥 벽면이 높이맵 가장자리에 걸치지 않게")]
    [Min(0.1f)]
    [SerializeField] private float m_padding = 0.5f;

    [Tooltip("기체보다 이만큼(m) 위에서 쏜다 — 기체 높이에 딱 붙은 면도 잡히게")]
    [Min(0f)]
    [SerializeField] private float m_rayLift = 0.5f;

    [Tooltip("다시 굽는 간격(초) — 높이맵은 구운 자리에 월드로 박혀 있어 건너뛴 동안에도 지면이 밀리지 않는다. " +
             "그사이 기체가 움직인 만큼만 발자국이 뒤처지므로 여유(m_padding)를 넘길 만큼 벌리지 말 것")]
    [Min(0f)]
    [SerializeField] private float m_refreshInterval = 0.05f;

    private const int k_raysPerJob = 64;

    private static readonly int s_heightMapId = Shader.PropertyToID("_HeightMap");
    private static readonly int s_heightFieldId = Shader.PropertyToID("_HeightField");

    private UfoCraft m_craft;
    private Material m_beamMaterial;
    private Texture2D m_map;
    private float[] m_heights;

    private NativeArray<RaycastCommand> m_commands;
    private NativeArray<RaycastHit> m_results;

    private int m_built;
    private float m_nextBakeAt;

    public bool HasField { get; private set; }

    public float LowestGround { get; private set; }

    private void Awake() => m_craft = GetComponent<UfoCraft>();

    private void OnDestroy()
    {
        if (m_map != null)
            Destroy(m_map);
        m_map = null;

        if (m_beamMaterial != null)
            Destroy(m_beamMaterial);
        m_beamMaterial = null;

        DisposeBatch();
    }

    private void DisposeBatch()
    {
        if (m_commands.IsCreated)
            m_commands.Dispose();
        if (m_results.IsCreated)
            m_results.Dispose();
    }

    private void LateUpdate()
    {
        if (m_beamRenderer == null || Time.time < m_nextBakeAt)
            return;

        m_nextBakeAt = Time.time + m_refreshInterval;
        EnsureBuffers();

        Vector3 center = transform.position;
        float size = FieldSize;
        Bake(center, size);

        m_map.SetPixelData(m_heights, 0);
        m_map.Apply(updateMipmaps: false);

        m_beamMaterial ??= m_beamRenderer.material;
        m_beamMaterial.SetTexture(s_heightMapId, m_map);
        m_beamMaterial.SetVector(
            s_heightFieldId,
            new Vector4(center.x - size * 0.5f, center.z - size * 0.5f, 1f / size, 1f)
        );

        HasField = true;
    }

    private float FieldSize => (m_craft.BeamRadius + m_padding) * 2f;

    private void EnsureBuffers()
    {
        if (m_built == m_grid && m_map != null)
            return;

        m_built = m_grid;
        int cells = m_grid * m_grid;
        m_heights = new float[cells];

        DisposeBatch();
        m_commands = new NativeArray<RaycastCommand>(cells, Allocator.Persistent);
        m_results = new NativeArray<RaycastHit>(cells, Allocator.Persistent);

        if (m_map != null)
            Destroy(m_map);

        m_map = new Texture2D(m_grid, m_grid, TextureFormat.RFloat, mipChain: false, linear: true)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            name = "UfoBeamGround",
        };
    }

    private void Bake(Vector3 center, float size)
    {
        float step = size / m_grid;
        float corner = -size * 0.5f + step * 0.5f;

        float probe = m_craft.GroundProbeDistance;
        float miss = center.y - probe;
        var query = new QueryParameters(m_craft.GroundMask, false, QueryTriggerInteraction.Ignore, false);

        for (int z = 0; z < m_grid; z++)
        {
            for (int x = 0; x < m_grid; x++)
            {
                Vector3 origin = new Vector3(
                    center.x + corner + x * step,
                    center.y + m_rayLift,
                    center.z + corner + z * step
                );

                m_commands[z * m_grid + x] =
                    new RaycastCommand(origin, Vector3.down, query, probe + m_rayLift);
            }
        }

        RaycastCommand.ScheduleBatch(m_commands, m_results, k_raysPerJob, 1).Complete();

        float lowest = float.MaxValue;
        bool anyHit = false;

        for (int i = 0; i < m_heights.Length; i++)
        {
            bool hitGround = m_results[i].colliderInstanceID != 0;
            float height = hitGround ? m_results[i].point.y : miss;
            m_heights[i] = height;

            if (hitGround && height < lowest)
            {
                lowest = height;
                anyHit = true;
            }
        }

        LowestGround = anyHit ? lowest : center.y;
    }
}
