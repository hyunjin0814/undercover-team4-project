using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 오너 플레이어의 조준 레이캐스트로 상호작용 대상을 찾고 E 입력을 전달한다.
/// 서버 판정용 사거리·가시선 검사도 제공한다.
/// </summary>
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerInteractor : NetworkBehaviour
{
    [Header("레이캐스트")]
    [SerializeField]
    private Camera m_camera;

    [Tooltip("상호작용이 실제로 닿는 거리(m). 줍기·구조·운반·검거가 이 값을 공유한다")]
    [SerializeField]
    private float m_range = 3f;

    [Tooltip(
        "조준 레이가 대상을 찾는 거리(m) — 도달 거리보다 길다. 원거리 표시(체력 바 등)가 쓰는 "
            + "ProbeTarget이 여기까지 잡힌다 (#499)"
    )]
    [SerializeField]
    private float m_probeRange = 20f;

    [SerializeField]
    private LayerMask m_interactMask = ~0;

    [Tooltip("시야를 가로막는 장애물 레이어 — 벽·건물(Default). 여기 걸리면 대상으로 잡지 않는다")]
    [SerializeField]
    private LayerMask m_losBlockMask = 1;

    [Header("디버그")]
    [Tooltip(
        "조준·가시선 판정을 콘솔에 찍는다 — 히트 목록·거리·무엇이 차단했는지. Gizmos를 켜면 레이도 보인다"
    )]
    [SerializeField]
    private bool m_logLineOfSight;

    [Tooltip(
        "Gizmos로 그리는 레이가 화면에 남는 시간(초). 콘솔 로그는 상황이 바뀔 때 한 번만 찍힌다"
    )]
    [SerializeField]
    private float m_logInterval = 0.5f;

    private const float k_losEndMargin = 0.05f;

    private static readonly RaycastHit[] s_losHits = new RaycastHit[64];

    private static readonly RaycastHit[] s_ragdollHits = new RaycastHit[64];

    private static int s_ragdollAimMask = -1;

    private static int RagdollAimMask
    {
        get
        {
            if (s_ragdollAimMask < 0)
            {
                int layer = LayerMask.NameToLayer(RagdollRig.k_layerName);
                s_ragdollAimMask = layer >= 0 ? 1 << layer : 0;
            }
            return s_ragdollAimMask;
        }
    }

    public IInteractable CurrentInteractable { get; private set; }
    public GameObject CurrentTarget { get; private set; }

    public GameObject ProbeTarget { get; private set; }

    public event System.Action<GameObject> OnTargetChanged;

    public event System.Action<GameObject> OnProbeTargetChanged;

    public float Range => m_range;

    public Transform AimOrigin => m_camera != null ? m_camera.transform : transform;

    public Camera AimCamera => m_camera;

    public LayerMask LosBlockMask => m_losBlockMask;

    private PlayerInputHandler m_inputHandler;
    private PlayerEscorter m_escorter;
    private PlayerEscortCommands m_commands;
    private PlayerIncapacitation m_incapacitation;
    private PlayerCarrier m_carrier;

    private int m_lastLogTargetId;
    private int m_lastLogBlockerId;
    private bool m_lastLogBlocked;
    private bool m_lastLogWasAimMiss;

    public override void OnNetworkSpawn()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_escorter = GetComponent<PlayerEscorter>();
        m_commands = GetComponent<PlayerEscortCommands>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_carrier = GetComponent<PlayerCarrier>();
        if (m_camera == null)
            m_camera = Camera.main;

        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        m_inputHandler.OnInteractPerformed += HandleInteract;
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner)
            return;

        m_inputHandler.OnInteractPerformed -= HandleInteract;
    }

    private void Update()
    {
        UpdateTarget();
    }

    private void UpdateTarget()
    {
        if (m_camera == null)
            return;

        GameObject previousTarget = CurrentTarget;
        GameObject previousProbe = ProbeTarget;

        Ray ray = new Ray(m_camera.transform.position, m_camera.transform.forward);
        bool aimed = Physics.Raycast(ray, out RaycastHit hit, m_probeRange, m_interactMask);

        bool viaBone =
            TryAimRagdollBone(ray, out RaycastHit boneHit)
            && (!aimed || boneHit.distance < hit.distance);
        if (viaBone)
        {
            hit = boneHit;
            aimed = true;
        }

        if (
            aimed
            && HasLineOfSight(ray.origin, hit.point, hit.transform, viaBone ? "조준/뼈" : "조준")
        )
        {
            ProbeTarget = hit.collider.gameObject;

            bool inReach = hit.distance <= m_range;
            CurrentTarget = inReach ? ProbeTarget : null;
            CurrentInteractable = inReach
                ? hit.collider.GetComponentInParent<IInteractable>()
                : null;
        }
        else
        {
            if (!aimed)
                LogAimMiss(ray);
            ProbeTarget = null;
            CurrentTarget = null;
            CurrentInteractable = null;
        }

        if (CurrentTarget != previousTarget)
            OnTargetChanged?.Invoke(CurrentTarget);

        if (ProbeTarget != previousProbe)
            OnProbeTargetChanged?.Invoke(ProbeTarget);
    }

    /// <summary>서버 판정용 가시선 검사 — AimOrigin에서 대상이 벽에 가리지 않았는지 확인한다.</summary>
    public bool HasLineOfSightTo(Transform target)
    {
        Vector3 point = target.TryGetComponent(out Collider targetCollider)
            ? targetCollider.bounds.center
            : target.position;

        return HasLineOfSight(AimOrigin.position, point, target, "서버 판정");
    }

    /// <summary>사거리와 가시선을 함께 보는 서버 판정. interactor가 없으면 fallbackOrigin 기준 거리만 본다.</summary>
    public static bool IsWithinReach(
        PlayerInteractor interactor,
        Transform target,
        float range,
        Vector3 fallbackOrigin
    )
    {
        Vector3 origin = interactor != null ? interactor.AimOrigin.position : fallbackOrigin;
        return (target.position - origin).sqrMagnitude <= range * range
            && (interactor == null || interactor.HasLineOfSightTo(target));
    }

    /// <summary>인터랙터의 사거리 — 없는 구성(테스트 등)이면 fallback. IsWithinReach와 짝으로 쓴다.</summary>
    public static float RangeOf(PlayerInteractor interactor, float fallback) =>
        interactor != null ? interactor.Range : fallback;

    private bool TryAimRagdollBone(Ray ray, out RaycastHit nearest)
    {
        nearest = default;

        int mask = RagdollAimMask;
        if (mask == 0)
            return false;

        int count = Physics.RaycastNonAlloc(
            ray,
            s_ragdollHits,
            m_probeRange,
            mask,
            QueryTriggerInteraction.Ignore
        );

        float nearestDistance = float.PositiveInfinity;
        bool found = false;
        for (int i = 0; i < count; i++)
        {
            float distance = s_ragdollHits[i].distance;
            if (distance <= 0f || distance >= nearestDistance)
                continue;
            if (!IsAimableRagdollBone(s_ragdollHits[i].collider))
                continue;

            nearest = s_ragdollHits[i];
            nearestDistance = distance;
            found = true;
        }

        return found;
    }

    private bool IsAimableRagdollBone(Collider collider)
    {
        if (collider == null || collider.transform.IsChildOf(transform))
            return false;

        PlayerIncapacitation body = collider.GetComponentInParent<PlayerIncapacitation>();
        if (body != null)
            return body.IsAimTargetable;

        NpcRagdoll npc = collider.GetComponentInParent<NpcRagdoll>();
        return npc != null && npc.IsRagdollActive;
    }

    private bool HasLineOfSight(
        Vector3 origin,
        Vector3 point,
        Transform target,
        string context = null
    )
    {
        Vector3 toPoint = point - origin;
        float fullDistance = toPoint.magnitude;
        float distance = fullDistance - k_losEndMargin;
        if (distance <= 0f)
        {
            return true;
        }

        int count = Physics.RaycastNonAlloc(
            origin,
            toPoint / fullDistance,
            s_losHits,
            distance,
            m_losBlockMask,
            QueryTriggerInteraction.Ignore
        );

        int blockerIndex = AimOcclusion.FindNearest(origin, s_losHits, count, target.root);

        LogLineOfSight(context, origin, point, target, count, blockerIndex, distance);
        return blockerIndex < 0;
    }

    private void LogLineOfSight(
        string context,
        Vector3 origin,
        Vector3 point,
        Transform target,
        int count,
        int blockerIndex,
        float distance
    )
    {
        if (!m_logLineOfSight)
            return;

        bool blocked = blockerIndex >= 0;

        Debug.DrawLine(
            origin,
            blocked ? s_losHits[blockerIndex].point : point,
            blocked ? Color.red : Color.green,
            Mathf.Max(0.05f, m_logInterval)
        );

        int targetId = target.GetInstanceID();
        int blockerId = blocked ? s_losHits[blockerIndex].collider.GetInstanceID() : 0;
        if (
            !m_lastLogWasAimMiss
            && targetId == m_lastLogTargetId
            && blockerId == m_lastLogBlockerId
            && blocked == m_lastLogBlocked
        )
        {
            return;
        }

        m_lastLogTargetId = targetId;
        m_lastLogBlockerId = blockerId;
        m_lastLogBlocked = blocked;
        m_lastLogWasAimMiss = false;

        var sb = new System.Text.StringBuilder();
        sb.Append($"[LOS/{context ?? "?"}] {(blocked ? "차단" : "통과")} — 대상 {target.name}");
        sb.Append($", 사거리 검사 {distance:F2}m, 히트 {count}개");
        sb.Append($" (server={IsServer} owner={IsOwner}, blockMask={m_losBlockMask.value})");

        if (count == 0)
        {
            sb.Append("\n  히트 없음 — 이 경로는 가시선 때문에 실패한 것이 아니다");
        }

        for (int i = 0; i < count; i++)
        {
            Collider c = s_losHits[i].collider;
            string mark =
                i == blockerIndex ? "  ← 여기서 차단"
                : c != null && c.transform.root == target.root ? "  (대상 자신)"
                : "";
            sb.Append($"\n  [{i}] {s_losHits[i].distance:F2}m  {(c == null ? "(null)" : c.name)}");
            if (c != null)
            {
                sb.Append(
                    $"  (Transform까지 {Vector3.Distance(origin, c.transform.position):F2}m)"
                );
                sb.Append(
                    $"  root={c.transform.root.name}, layer={LayerMask.LayerToName(c.gameObject.layer)}"
                );
            }

            sb.Append(mark);
        }

        if (blocked)
            Debug.LogWarning(sb.ToString(), s_losHits[blockerIndex].collider);
        else
            Debug.Log(sb.ToString(), target);
    }

    private void LogAimMiss(Ray ray)
    {
        if (!m_logLineOfSight)
            return;

        Debug.DrawRay(
            ray.origin,
            ray.direction * m_probeRange,
            Color.yellow,
            Mathf.Max(0.05f, m_logInterval)
        );

        if (m_lastLogWasAimMiss)
            return;
        m_lastLogWasAimMiss = true;
        m_lastLogTargetId = 0;
        m_lastLogBlockerId = 0;

        string extra = Physics.Raycast(
            ray,
            out RaycastHit any,
            m_probeRange,
            ~0,
            QueryTriggerInteraction.Collide
        )
            ? $"마스크를 무시하면 {any.collider.name} (layer={LayerMask.LayerToName(any.collider.gameObject.layer)}, "
                + $"{any.distance:F2}m, trigger={any.collider.isTrigger})를 맞는다 — 대상 레이어가 interactMask 밖일 수 있다"
                + RagdollAimRejectReason(any.collider)
            : $"마스크를 무시해도 아무것도 없다 — 레이 길이({m_probeRange}m) 밖이거나 콜라이더가 없다";

        Debug.Log(
            $"[LOS/조준] 대상 없음 — interactMask={m_interactMask.value}, "
                + $"probeRange={m_probeRange}m, range={m_range}m. {extra}"
        );
    }

    private string RagdollAimRejectReason(Collider collider)
    {
        if (collider == null || ((1 << collider.gameObject.layer) & RagdollAimMask) == 0)
            return string.Empty;
        if (collider.transform.IsChildOf(transform))
            return " (뼈: 내 몸이라 제외)";

        const string valid = " (뼈: 유효한데 안 잡혔다 — distance<=0·버퍼 넘침을 의심할 것)";

        PlayerIncapacitation body = collider.GetComponentInParent<PlayerIncapacitation>();
        if (body != null)
            return body.IsAimTargetable
                ? valid
                : $" (뼈: 겨냥 대상 아님 — cause={body.Cause}, bodyLost={body.IsBodyLost})";

        NpcRagdoll npc = collider.GetComponentInParent<NpcRagdoll>();
        if (npc == null)
            return " (뼈: PlayerIncapacitation·NpcRagdoll 둘 다 없음 — 리그 배선 확인)";
        return npc.IsRagdollActive ? valid : " (뼈: NPC가 래그돌이 아니다)";
    }

    private void HandleInteract()
    {
        if (CursorLock.IsUnlocked)
            return;

        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return;

        bool aimedInteraction =
            CurrentInteractable != null && CurrentInteractable.CanInteract(gameObject);

        NpcController aimed =
            CurrentTarget != null ? CurrentTarget.GetComponentInParent<NpcController>() : null;
        if (m_escorter != null && m_escorter.IsDraggingNpc(aimed))
        {
            Debug.Log($"E 입력 — 밧줄 풀기 요청(겨냥): {aimed.name}");
            m_commands?.RequestUnrope(aimed);
            return;
        }

        if (m_carrier != null && m_carrier.IsCarrying)
        {
            if (
                CurrentInteractable is ICarriedBodyReceiver receiver
                && receiver.CanInteract(gameObject)
            )
            {
                receiver.Interact(gameObject);
                return;
            }

            Transform carried = m_carrier.CarriedTransform;
            if (
                carried != null
                && CurrentTarget != null
                && CurrentTarget.transform.IsChildOf(carried)
            )
            {
                Debug.Log("E 입력 — 내려놓기 요청 (운반, 겨냥)");
                m_carrier.RequestDrop();
                return;
            }
        }

        if (!aimedInteraction)
        {
            bool released = false;

            if (m_escorter != null && m_escorter.IsDraggingAny)
            {
                Debug.Log("E 입력 — 끌던 대상 전원 밧줄 풀기 요청");
                m_commands?.RequestUnropeAll();
                released = true;
            }

            if (m_carrier != null && m_carrier.IsCarrying)
            {
                Debug.Log("E 입력 — 내려놓기 요청 (운반)");
                m_carrier.RequestDrop();
                released = true;
            }

            if (released)
                return;
        }

        CurrentInteractable?.Interact(gameObject);
    }
}
