using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 본부 부활 장치 — 운반 중인 기능 정지 동료를 넣으면 안치 자리에 두고 30초 뒤 부활시킨다(GDD 7-5).
/// 진행 중 밧줄로 다시 끌어가면 타이머가 취소된다.
/// </summary>
[RequireComponent(typeof(Collider))]
public class HqRevivalDevice : NetworkBehaviour, ICarriedBodyReceiver
{
    [Header("부활")]
    [Tooltip("안치 후 이 시간(초)이 지나면 부활한다")]
    [SerializeField]
    private float m_revivalSeconds = 30f;

    [Tooltip("몸이 놓이는 자리 — 비우면 이 오브젝트의 위치·회전을 쓴다")]
    [SerializeField]
    private Transform m_slot;

    [Tooltip("안치할 수 있는 거리(m) — 서버 검증용. 조준 사거리보다 넉넉히 잡는다")]
    [SerializeField]
    private float m_placeRange = 4f;

    [Tooltip(
        "안치 자리에서 이 거리(m) 밖으로 몸이 벗어나면 안치가 풀린다 — 밧줄이 아닌 경로로 몸이 "
        + "옮겨진 경우(오검거 광장 이송 등)를 잡는 방어선이다"
    )]
    [SerializeField]
    private float m_strayDistance = 3f;

    [Header("표시")]
    [Tooltip("남은 시간을 띄울 월드 라벨 — 비우면 표시 없이 동작한다")]
    [SerializeField]
    private TestWorldLabel m_label;

    [SerializeField]
    private string m_idleText = "부활 장치";

    private PlayerIncapacitation m_occupant;
    private float m_elapsed;

    private readonly NetworkVariable<float> m_remainingSynced = new();

    private int m_shownSeconds = -1;

    private const float k_strayGraceSeconds = 1f;

    private Transform Slot => m_slot != null ? m_slot : transform;

    public bool IsOccupied =>
        IsSpawned && !IsServer ? m_remainingSynced.Value > 0f : m_occupant != null;

    /// <summary>E가 실제로 동작하는 상태인지 — 조준 피드백(윤곽선)용. 서버 검증과 같은 기준.</summary>
    public bool CanInteract(GameObject interactor)
    {
        if (IsOccupied)
            return false;

        PlayerCarrier carrier = FindCarrier(interactor);
        return carrier != null && carrier.IsCarrying;
    }

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.HandOverBody;

    public void Interact(GameObject interactor)
    {
        PlayerCarrier carrier = FindCarrier(interactor);
        if (carrier == null)
            return;

        carrier.RequestPlaceInDevice(this);
    }

    private static PlayerCarrier FindCarrier(GameObject interactor) =>
        interactor != null ? interactor.GetComponentInParent<PlayerCarrier>() : null;

    /// <summary>운반 중인 몸을 안치한다. 상태·거리를 다시 검증하고 성공하면 true.</summary>
    public bool ServerPlace(PlayerCarrier carrier)
    {
        if (IsSpawned && !IsServer)
            return false;
        if (m_occupant != null || carrier == null)
            return false;

        PlayerCarrier body = carrier.CarriedTarget;
        if (body == null)
            return false;

        PlayerIncapacitation incapacitation = body.GetComponent<PlayerIncapacitation>();
        if (incapacitation == null || !incapacitation.IsDead)
            return false;

        if ((carrier.transform.position - transform.position).sqrMagnitude
            > m_placeRange * m_placeRange)
            return false;

        body.ServerDropAllCarriers("부활 장치에 안치");

        PlayerMovement movement = body.GetComponent<PlayerMovement>();
        if (movement != null)
            movement.ServerTeleport(Slot.position, Slot.rotation);

        m_occupant = incapacitation;
        m_elapsed = 0f;
        SetRemaining(m_revivalSeconds);

        Debug.Log($"[본부 부활] 안치 — {body.name} ({m_revivalSeconds}초)");
        return true;
    }

    private void Update()
    {
        UpdateLabel();

        if (IsSpawned && !IsServer)
            return;
        if (m_occupant == null)
            return;

        if (!m_occupant.IsDead)
        {
            ClearOccupant("대상이 이미 복구됨");
            return;
        }

        PlayerCarrier body = m_occupant.GetComponent<PlayerCarrier>();
        if (body != null && body.IsBeingCarried)
        {
            ClearOccupant("꺼내짐 — 진행 취소");
            return;
        }

        m_elapsed += Time.deltaTime;

        if (m_elapsed >= k_strayGraceSeconds)
        {
            Vector3 stray = m_occupant.transform.position - Slot.position;
            if (stray.sqrMagnitude > m_strayDistance * m_strayDistance)
            {
                ClearOccupant("몸이 자리를 벗어남 — 밧줄이 아닌 경로로 옮겨졌다");
                return;
            }
        }

        SetRemaining(Mathf.Max(0f, m_revivalSeconds - m_elapsed));

        if (m_elapsed >= m_revivalSeconds)
            Revive();
    }

    private void Revive()
    {
        PlayerHealth health = m_occupant.GetComponent<PlayerHealth>();
        PlayerIncapacitation revived = m_occupant;
        ClearOccupant(null);

        if (health == null)
        {
            Debug.LogWarning($"[본부 부활] PlayerHealth가 없어 부활할 수 없다 — {revived.name}", this);
            return;
        }

        health.ServerRevive();
        Debug.Log($"[본부 부활] 복구 완료 — {revived.name} HP={health.CurrentHp}, 상태={revived.Cause}");
    }

    private void ClearOccupant(string reason)
    {
        if (reason != null && m_occupant != null)
            Debug.Log($"[본부 부활] 안치 해제({reason}) — {m_occupant.name}");

        m_occupant = null;
        m_elapsed = 0f;
        SetRemaining(0f);
    }

    private void SetRemaining(float seconds)
    {
        if (IsSpawned && IsServer)
            m_remainingSynced.Value = seconds;
        else if (!IsSpawned)
            m_remainingSynced.Value = seconds;
    }

    private void UpdateLabel()
    {
        if (m_label == null)
            return;

        float remaining = m_remainingSynced.Value;
        int seconds = remaining > 0f ? Mathf.CeilToInt(remaining) : 0;
        if (seconds == m_shownSeconds)
            return;

        m_shownSeconds = seconds;
        m_label.Configure(
            seconds > 0 ? $"부활까지 {seconds}초" : m_idleText,
            seconds > 0 ? new Color(0.5f, 1f, 0.6f) : Color.yellow);
    }

    public override void OnDestroy()
    {
        m_occupant = null;
        base.OnDestroy();
    }
}
