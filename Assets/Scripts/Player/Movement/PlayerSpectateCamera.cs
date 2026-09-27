using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 기능 정지 동안 내 시체(골반) 또는 살아 있는 동료(루트)를 중심으로 도는 3인칭 관전 오빗 카메라.
/// 좌클릭으로 대상을 순환하며, 포즈는 PlayerLook이 카메라에 적용한다.
/// </summary>
public class PlayerSpectateCamera : MonoBehaviour
{
    [Tooltip("시체(골반)에서 카메라가 도는 중심까지의 높이(m)")]
    [SerializeField]
    private float m_pivotHeight = 0.6f;

    [Tooltip("동료 루트에서 오빗 중심까지의 높이(m)")]
    [SerializeField]
    private float m_teammatePivotHeight = 1.4f;

    [Tooltip("동료 피벗(위치) 감쇠 추종 속도 (#963)")]
    [SerializeField]
    private float m_followRate = 6f;

    [Tooltip(
        "동료 기준 yaw 감쇠 추종 속도 — 위치보다 훨씬 낮게. 각도 오차가 궤도 반지름만큼 증폭돼 화면에서 더 크게 보인다 (#963)"
    )]
    [SerializeField]
    private float m_followYawRate = 4f;

    [Tooltip("중심에서 카메라까지의 거리(m)")]
    [SerializeField]
    private float m_distance = 3.5f;

    [Tooltip("오빗 피치 하한(음수=카메라가 중심보다 낮아진다). 너무 낮추면 바닥을 파고든다")]
    [SerializeField]
    private float m_minPitch = -10f;

    [Tooltip("오빗 피치 상한(양수=위에서 내려다본다). 시체를 내려다보는 쪽을 넉넉히 연다")]
    [SerializeField]
    private float m_maxPitch = 70f;

    [Tooltip("관전 진입 시 시작 피치 — 살짝 내려다보는 각에서 출발한다")]
    [SerializeField]
    private float m_enterPitch = 20f;

    [Tooltip("1인칭↔관전 전환 보간 속도. 클수록 빨리 빠진다 — 7이면 약 0.5초 (#665)")]
    [SerializeField]
    private float m_blendSpeed = 7f;

    [Tooltip("카메라가 벽을 파고들지 않게 띄울 반경(m)")]
    [SerializeField]
    private float m_probeRadius = 0.25f;

    [Tooltip("카메라 충돌 판정 레이어 — 플레이어·트리거는 빼 둘 것 (감정표현 3인칭과 같은 값)")]
    [SerializeField]
    private LayerMask m_collisionMask = ~0;

    [Tooltip(
        "관전 진입 직후 좌클릭 순환 입력을 무시하는 시간(초) — 죽는 순간까지 누르고 있던 공격 클릭이 그대로 넘어와 동료로 튀는 것을 막는다 (#899)"
    )]
    [SerializeField]
    private float m_inputGraceSeconds = 0.3f;

    private RagdollRig m_ownRig;
    private PlayerIncapacitation m_self;
    private PlayerInputHandler m_input;

    private PlayerIncapacitation m_target;

    private Vector3 m_followPivot;
    private float m_followYaw;
    private bool m_followValid;

    private readonly List<PlayerIncapacitation> m_ring = new();

    private static readonly RaycastHit[] s_wallProbeBuffer = new RaycastHit[8];

    private float m_activatedAt;

    private bool m_active;
    private float m_blend;
    private bool m_snap;
    private float m_pitch;

    private float m_yaw;

    public bool IsActive => m_active;

    private Vector3 m_pivotOverride;
    private bool m_hasPivotOverride;

    public bool HasPivotOverride => m_hasPivotOverride;

    private void Awake()
    {
        m_ownRig = GetComponentInChildren<RagdollRig>();
        m_ownRig?.EnsureCollected();
        m_self = GetComponent<PlayerIncapacitation>();
        m_input = GetComponent<PlayerInputHandler>();
    }

    private void OnEnable()
    {
        if (m_input != null)
            m_input.OnUseItemStarted += CycleNext;
    }

    private void OnDisable()
    {
        if (m_input != null)
            m_input.OnUseItemStarted -= CycleNext;
    }

    private void CycleNext()
    {
        if (CursorLock.IsUnlocked || m_self == null || !m_self.IsDead)
            return;

        if (Time.time - m_activatedAt < m_inputGraceSeconds)
            return;

        CycleTarget(1);
    }

    /// <summary>관전 대상을 [내 시체, 살아 있는 동료들] 고리에서 한 칸 옮긴다.</summary>
    public void CycleTarget(int direction)
    {
        if (!m_active || direction == 0)
            return;

        RebuildRing();

        int current = m_ring.IndexOf(m_target);
        if (current < 0)
            current = 0;

        int count = m_ring.Count;
        int next = ((current + direction) % count + count) % count;
        SetTarget(m_ring[next]);
    }

    private void RebuildRing()
    {
        m_ring.Clear();
        m_ring.Add(null);

        IReadOnlyList<PlayerIncapacitation> all = PlayerIncapacitation.All;
        for (int i = 0; i < all.Count; i++)
        {
            PlayerIncapacitation candidate = all[i];
            if (candidate == null || candidate == m_self || candidate.IsDead)
                continue;

            m_ring.Add(candidate);
        }
    }

    private void SetTarget(PlayerIncapacitation target)
    {
        if (m_target == target)
            return;

        float previousBase = TargetBaseYaw();

        m_target = target;
        m_followValid = false;

        m_yaw = target != null ? 0f : previousBase + m_yaw;
    }

    private void EnsureTargetValid()
    {
        if (m_target == null)
            return;

        if (m_target.isActiveAndEnabled && !m_target.IsDead)
            return;

        CycleTarget(1);
    }

    private float TargetBaseYaw()
    {
        if (m_target == null)
            return 0f;

        return m_followValid ? m_followYaw : m_target.transform.eulerAngles.y;
    }

    /// <summary>관전에 진입·이탈한다. entryYaw로 시작 각도를 맞춘다.</summary>
    public void SetSpectating(bool spectating, float entryYaw)
    {
        if (m_active == spectating)
            return;

        m_active = spectating;

        if (!spectating)
        {
            SetTarget(null);
            return;
        }

        m_yaw = entryYaw;
        m_pitch = m_enterPitch;
        m_activatedAt = Time.time;
    }

    /// <summary>마우스 입력을 오빗 각에 누적한다(상하는 제한).</summary>
    public void AddLook(Vector2 delta)
    {
        m_yaw += delta.x;
        m_pitch = Mathf.Clamp(m_pitch - delta.y, m_minPitch, m_maxPitch);
    }

    /// <summary>관전 블렌드를 한 프레임 진행하고 값(0 1인칭 ↔ 1 관전)을 돌려준다.</summary>
    public float Tick()
    {
        if (m_snap)
        {
            m_snap = false;
            m_blend = m_active ? 1f : 0f;
            return m_blend;
        }

        float t = m_blendSpeed <= 0f ? 1f : 1f - Mathf.Exp(-m_blendSpeed * Time.deltaTime);
        m_blend = Mathf.Lerp(m_blend, m_active ? 1f : 0f, t);

        if (m_blend < 0.001f)
            m_blend = 0f;

        return m_blend;
    }

    /// <summary>다음 Tick에서 보간을 끊고 현재 목표로 즉시 튀게 한다(몸 순간이동 시).</summary>
    public void SnapNextTick() => m_snap = true;

    /// <summary>오빗 중심을 지정 지점으로 고정한다(몸이 사라졌을 때).</summary>
    public void SetPivotOverride(Vector3 worldPosition)
    {
        m_pivotOverride = worldPosition;
        m_hasPivotOverride = true;
    }

    /// <summary>피벗 고정을 놓는다 — 부활 등으로 자기 몸을 다시 돌 수 있게 됐을 때.</summary>
    public void ClearPivotOverride() => m_hasPivotOverride = false;

    /// <summary>관전 대상을 곧장 살아있는 동료로 돌린다 — 없으면 내 시체(피벗 고정) 슬롯에 남는다.</summary>
    public void SpectateTeammateIfAny()
    {
        RebuildRing();
        for (int i = 1; i < m_ring.Count; i++)
        {
            if (m_ring[i] != null)
            {
                SetTarget(m_ring[i]);
                return;
            }
        }
    }

    /// <summary>관전 카메라의 월드 포즈. 골반이 없으면 false — 호출자는 1인칭 포즈를 그대로 쓴다.</summary>
    public bool TryGetPose(out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = default;

        EnsureTargetValid();

        Vector3 pivot;

        if (m_target != null)
        {
            Vector3 rawPivot = m_target.transform.position + Vector3.up * m_teammatePivotHeight;
            float rawYaw = m_target.transform.eulerAngles.y;

            if (!m_followValid)
            {
                m_followPivot = rawPivot;
                m_followYaw = rawYaw;
                m_followValid = true;
            }
            else
            {
                float t = m_followRate <= 0f ? 1f : 1f - Mathf.Exp(-m_followRate * Time.deltaTime);
                float yawT =
                    m_followYawRate <= 0f ? 1f : 1f - Mathf.Exp(-m_followYawRate * Time.deltaTime);
                m_followPivot = Vector3.Lerp(m_followPivot, rawPivot, t);
                m_followYaw = Mathf.LerpAngle(m_followYaw, rawYaw, yawT);
            }

            pivot = m_followPivot;
        }
        else
        {
            Transform hips = m_ownRig != null ? m_ownRig.Hips : null;
            bool useOverride = m_hasPivotOverride;

            if (hips == null && !useOverride)
                return false;

            Vector3 pivotBase = useOverride ? m_pivotOverride : hips.position;
            pivot = pivotBase + Vector3.up * m_pivotHeight;
        }

        rotation = Quaternion.Euler(m_pitch, TargetBaseYaw() + m_yaw, 0f);

        Vector3 back = rotation * Vector3.back;
        float distance = m_distance;
        int hitCount = Physics.SphereCastNonAlloc(
            pivot,
            m_probeRadius,
            back,
            s_wallProbeBuffer,
            distance,
            m_collisionMask,
            QueryTriggerInteraction.Ignore
        );
        for (int i = 0; i < hitCount; i++)
        {
            Collider hitCollider = s_wallProbeBuffer[i].collider;
            if (hitCollider == null)
                continue;
            if (hitCollider.GetComponentInParent<PlayerHealth>() != null)
                continue;
            if (hitCollider.GetComponentInParent<NpcController>() != null)
                continue;

            float hitDistance = s_wallProbeBuffer[i].distance;
            if (hitDistance < distance)
                distance = hitDistance;
        }

        distance = Mathf.Max(distance - m_probeRadius, 0f);

        position = pivot + back * distance;
        return true;
    }
}
