#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

/// <summary>
/// 래그돌 벽 관통 재현용 에디터 전용 단축키 —, 벽에 NPC를 세우고 팔을 찔러 넣기,. 벽 쪽으로 날리기.
/// 서버·오프라인에서만 동작하며 침투 깊이를 로그로 남긴다.
/// </summary>
public class RagdollWallDevHotkeys : MonoBehaviour
{
    [Tooltip("끄면 단축키가 듣지 않는다 — 같은 키를 쓰는 다른 테스트를 할 때 잠깐 내린다")]
    [SerializeField] private bool m_enabled = true;

    [Header("키 (인스펙터 조절)")]
    [Tooltip("내가 보는 벽에 NPC를 세우고 팔을 벽 안으로 넣는다")]
    [SerializeField] private Key m_placeKey = Key.Comma;

    [Tooltip("세워 둔 NPC를 벽 쪽으로 날린다")]
    [SerializeField] private Key m_launchKey = Key.Period;

    [Tooltip("대상 NPC의 <b>팔 접기</b>(#980)를 켜고 끈다 — 같은 조건으로 A/B를 보는 키다")]
    [SerializeField] private Key m_wallFoldKey = Key.Slash;

    [Header("배치")]
    [Tooltip("이 거리(m) 안에서 벽을 찾는다 — 벽을 바라보고 누를 것")]
    [SerializeField] private float m_wallSearchDistance = 12f;

    [Tooltip("벽면과 에이전트 캡슐 사이에 남길 간격(m) — 0이면 캡슐이 벽에 닿는다.\n\n" +
             "<b>몸통</b>의 자리다. 팔은 아래 항목이 따로 밀어 넣는다")]
    [SerializeField] private float m_wallGap;

    [Header("팔 찔러 넣기 — 재현의 핵심")]
    [Tooltip("끄면 캡슐만 벽에 붙이고 팔은 애니메이션 자세 그대로 둔다(구 동작). " +
             "켜면 애니메이터를 떼고 팔을 벽 쪽으로 돌려 아래 깊이만큼 넣는다")]
    [SerializeField] private bool m_poseArmIntoWall = true;

    [Tooltip("어느 쪽 팔을 넣을지 — 끄면 오른팔")]
    [SerializeField] private bool m_useLeftArm = true;

    [Tooltip("팔꿈치 뼈가 벽면 안으로 들어갈 목표 깊이(m). 벽 두께(0.038m)보다 크면 반대편까지 관통한다")]
    [SerializeField] private float m_armTargetPenetration = 0.03f;

    [Header("발사 (홈런 진압봉 기본값과 맞춰 둠)")]
    [Tooltip("수평 발사 속도(m/s) — HomeRunBaton.m_launchSpeed와 같은 값이 기본")]
    [SerializeField] private float m_launchSpeed = 14f;

    [Tooltip("수평 대비 들어올림 비율 — 1.0이면 45도")]
    [SerializeField] private float m_liftRatio = 0.5f;

    [Tooltip("발사 후 누워 있는 시간(초)")]
    [SerializeField] private float m_stunSeconds = 6f;

    [Header("팔 접기 A/B (#980)")]
    [Tooltip("세우는 NPC에게 걸 <c>NpcRagdoll.WallFoldEnabled</c> — 끄면 옛 동작(박힌 채 정착)이 " +
             "그대로 보인다. 프리팹 기본은 켜짐이다")]
    [SerializeField] private bool m_wallFold = true;

    private NpcController m_subject;
    private Vector3 m_wallNormal;

    private Animator m_subjectAnimator;
    private NavMeshAgent m_subjectAgent;

    private void Update()
    {
        if (!m_enabled)
            return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        if (keyboard[m_placeKey].wasPressedThisFrame)
            PlaceAgainstWall();
        if (keyboard[m_launchKey].wasPressedThisFrame)
            LaunchIntoWall();
        if (keyboard[m_wallFoldKey].wasPressedThisFrame)
            ToggleWallFold();
    }

    private void ToggleWallFold()
    {
        m_wallFold = !m_wallFold;
        ApplyWallFold();

        string where = m_subject != null ? m_subject.name : "다음에 세울 NPC부터";
        Debug.Log(
            $"[래그돌벽/개발용] 팔 접기 {(m_wallFold ? "켜짐" : "꺼짐")} — {where}",
            this
        );
    }

    private void ApplyWallFold()
    {
        if (m_subject != null && m_subject.Ragdoll != null)
            m_subject.Ragdoll.WallFoldEnabled = m_wallFold;
    }

    private void PlaceAgainstWall()
    {
        Transform player = DevPlayerLookup.LocalPlayer();
        if (player == null)
        {
            Debug.LogWarning("[래그돌벽/개발용] 플레이어를 찾지 못했다");
            return;
        }

        if (!TryFindWall(player, out RaycastHit wall))
        {
            Debug.LogWarning(
                $"[래그돌벽/개발용] 앞 {m_wallSearchDistance}m 안에서 벽을 못 찾았다 — 벽을 보고 다시 누를 것"
            );
            return;
        }

        NpcController npc = FindNearestUsableNpc(player.position);
        if (npc == null)
        {
            Debug.LogWarning("[래그돌벽/개발용] 세울 만한 NPC가 없다 — 성하고 서 있는 NPC 근처에서 누를 것");
            return;
        }

        NavMeshAgent agent = npc.GetComponent<NavMeshAgent>();
        if (agent == null)
        {
            Debug.LogWarning($"[래그돌벽/개발용] {npc.name}에 NavMeshAgent가 없다", npc);
            return;
        }

        Vector3 normal = new Vector3(wall.normal.x, 0f, wall.normal.z);
        if (normal.sqrMagnitude < 0.0001f)
        {
            Debug.LogWarning("[래그돌벽/개발용] 수평 법선이 없다 — 바닥이나 천장을 본 것 같다");
            return;
        }
        normal.Normalize();

        RestorePreviousSubject();

        npc.SetFrozen(true);

        agent.updatePosition = false;
        agent.updateRotation = false;
        m_subjectAgent = agent;

        npc.transform.position = wall.point + normal * (agent.radius + m_wallGap);
        npc.transform.rotation = Quaternion.LookRotation(-normal);
        Physics.SyncTransforms();

        m_subject = npc;
        m_wallNormal = normal;

        ApplyWallFold();

        if (m_poseArmIntoWall)
            PoseArmIntoWall(npc, wall.collider, normal);
        else
            Debug.Log($"[래그돌벽/개발용] {npc.name}을 벽({wall.collider.name})에 붙여 세웠다 (팔 찔러넣기 꺼짐)", npc);
    }

    private void PoseArmIntoWall(NpcController npc, Collider wall, Vector3 normal)
    {
        RagdollRig rig = npc.GetComponentInChildren<RagdollRig>(true);
        if (rig == null || !rig.IsValid)
        {
            Debug.LogWarning($"[래그돌벽/개발용] {npc.name}에서 RagdollRig를 찾지 못했다", npc);
            return;
        }

        Transform boneRoot = rig.Hips != null ? rig.Hips.parent : null;
        string side = m_useLeftArm ? "_L" : "_R";
        Transform shoulder = FindBone(boneRoot, "Shoulder" + side);
        Transform elbow = FindBone(boneRoot, "Elbow" + side);
        if (shoulder == null || elbow == null)
        {
            Debug.LogWarning($"[래그돌벽/개발용] Shoulder{side}/Elbow{side} 뼈를 찾지 못했다", npc);
            return;
        }

        m_subjectAnimator = npc.GetComponentInChildren<Animator>(true);
        if (m_subjectAnimator != null)
            m_subjectAnimator.enabled = false;

        Vector3 into = -normal;
        AimBoneAlong(shoulder, elbow, into);
        if (elbow.childCount > 0)
            AimBoneAlong(elbow, elbow.GetChild(0), into);
        Physics.SyncTransforms();

        Collider elbowCol = elbow.GetComponent<Collider>();
        if (elbowCol == null)
        {
            Debug.LogWarning($"[래그돌벽/개발용] Elbow{side}에 콜라이더가 없다", npc);
            return;
        }

        const int k_maxNudges = 24;
        const float k_minStep = 0.002f;
        float depth = MeasurePenetration(elbowCol, wall);
        for (int i = 0; i < k_maxNudges && depth < m_armTargetPenetration; i++)
        {
            if (IsThroughWall(elbow, rig.Hips, wall))
                break;

            float deficit = m_armTargetPenetration - depth;
            npc.transform.position += into * Mathf.Max(deficit * 0.5f, k_minStep);
            Physics.SyncTransforms();

            float next = MeasurePenetration(elbowCol, wall);
            if (next <= depth + 0.00001f && !IsThroughWall(elbow, rig.Hips, wall))
                break;
            depth = next;
        }

        Collider shoulderCol = shoulder.GetComponent<Collider>();
        float shoulderDepth = shoulderCol != null ? MeasurePenetration(shoulderCol, wall) : -1f;

        Collider hipsCol = rig.Hips != null ? rig.Hips.GetComponent<Collider>() : null;
        float hipsDepth = hipsCol != null ? MeasurePenetration(hipsCol, wall) : 0f;

        Debug.Log(
            $"[래그돌벽/개발용] {npc.name} 배치 완료 — 벽 {wall.gameObject.name}\n"
                + $"  팔꿈치(Elbow{side}) {DescribeDepth(depth, elbow, rig.Hips, wall)} (목표 {m_armTargetPenetration * 100f:F1}cm)\n"
                + $"  어깨(Shoulder{side}) {DescribeDepth(shoulderDepth, shoulder, rig.Hips, wall)}\n"
                + $"  골반 침투 {hipsDepth * 100f:F1}cm ← 0이어야 '팔만 박힌' 재현이다\n"
                + $"  → {m_launchKey}로 벽 쪽 발사",
            npc
        );
    }

    private static void AimBoneAlong(Transform bone, Transform child, Vector3 want)
    {
        Vector3 current = child.position - bone.position;
        if (current.sqrMagnitude < 0.000001f)
            return;

        bone.rotation = Quaternion.FromToRotation(current, want) * bone.rotation;
    }

    /// <summary>침투 깊이를 읽을 수 있는 문장으로 만든다(골반에서 레이로 완전 관통 여부도 판정).</summary>
    private static string DescribeDepth(float depth, Transform bone, Transform hips, Collider wall)
    {
        if (depth > 0.0001f)
            return $"침투 {depth * 100f:F1}cm";

        return IsThroughWall(bone, hips, wall)
            ? "<b>완전 관통 — 벽 반대편에 있다</b> (겹침 0)"
            : "침투 0.0cm (벽 밖)";
    }

    private static bool IsThroughWall(Transform bone, Transform hips, Collider wall)
    {
        if (bone == null || hips == null || wall == null)
            return false;

        Vector3 from = hips.position;
        Vector3 to = bone.position;
        float distance = Vector3.Distance(from, to);
        if (distance < 0.0001f)
            return false;

        RaycastHit[] hits = Physics.RaycastAll(
            from,
            (to - from) / distance,
            distance,
            ~0,
            QueryTriggerInteraction.Ignore
        );
        for (int i = 0; i < hits.Length; i++)
            if (hits[i].collider == wall)
                return true;

        return false;
    }

    private static float MeasurePenetration(Collider a, Collider b)
    {
        return Physics.ComputePenetration(
            a,
            a.transform.position,
            a.transform.rotation,
            b,
            b.transform.position,
            b.transform.rotation,
            out _,
            out float distance
        )
            ? distance
            : 0f;
    }

    private static Transform FindBone(Transform root, string boneName)
    {
        if (root == null)
            return null;

        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
            if (all[i].name == boneName)
                return all[i];

        return null;
    }

    private void RestorePreviousSubject()
    {
        if (m_subjectAnimator != null)
            m_subjectAnimator.enabled = true;
        m_subjectAnimator = null;

        if (m_subjectAgent != null)
        {
            m_subjectAgent.updatePosition = true;
            m_subjectAgent.updateRotation = true;
        }
        m_subjectAgent = null;

        if (m_subject != null)
            m_subject.SetFrozen(false);
    }

    private bool TryFindWall(Transform player, out RaycastHit wall)
    {
        wall = default;

        Vector3 origin = player.position + Vector3.up * 1.5f;
        Vector3 forward = new Vector3(player.forward.x, 0f, player.forward.z).normalized;

        RaycastHit[] hits = Physics.RaycastAll(
            origin,
            forward,
            m_wallSearchDistance,
            ~0,
            QueryTriggerInteraction.Ignore
        );

        bool found = false;
        for (int i = 0; i < hits.Length; i++)
        {
            Collider hit = hits[i].collider;
            if (hit == null)
                continue;
            if (hit.GetComponentInParent<PlayerHealth>() != null)
                continue;
            if (hit.GetComponentInParent<NpcController>() != null)
                continue;

            if (!found || hits[i].distance < wall.distance)
            {
                wall = hits[i];
                found = true;
            }
        }

        return found;
    }

    private static NpcController FindNearestUsableNpc(Vector3 origin)
    {
        NpcController[] all = Object.FindObjectsByType<NpcController>(FindObjectsSortMode.None);

        NpcController best = null;
        float bestSqr = float.MaxValue;
        for (int i = 0; i < all.Length; i++)
        {
            NpcController npc = all[i];
            if (npc == null || !npc.AgentReady)
                continue;

            NpcState state = npc.StateMachine.CurrentState;
            if (state == NpcState.Dead || state == NpcState.Jailed || state == NpcState.Intruding)
                continue;

            float sqr = (npc.transform.position - origin).sqrMagnitude;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                best = npc;
            }
        }

        return best;
    }

    private void LaunchIntoWall()
    {
        if (m_subject == null)
        {
            Debug.LogWarning($"[래그돌벽/개발용] 세워 둔 NPC가 없다 — {m_placeKey}로 먼저 세울 것");
            return;
        }

        Vector3 into = -m_wallNormal;
        Vector3 impulse = into * m_launchSpeed + Vector3.up * (m_launchSpeed * m_liftRatio);

        if (m_subjectAgent != null)
        {
            m_subjectAgent.updatePosition = true;
            m_subjectAgent.updateRotation = true;
        }
        m_subject.SetFrozen(false);

        m_subject.Knockback.ServerLaunchRagdoll(impulse, m_stunSeconds, DevPlayerLookup.LocalPlayer());

        Debug.Log(
            $"[래그돌벽/개발용] {m_subject.name} 벽 쪽으로 발사 — 요청 임펄스 {impulse:F2}",
            m_subject
        );
    }
}
#endif
