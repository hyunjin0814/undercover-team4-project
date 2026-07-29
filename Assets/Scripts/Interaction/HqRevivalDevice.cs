using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 본부 부활 장치 — 기능 정지(Die)된 동료를 <b>넣어 두면</b> 30초 뒤 부활시킨다. (#365, GDD 7-5)
/// 동료를 운반한 채 장치를 겨냥해 E를 누르면 몸이 안치 자리로 옮겨지고 타이머가 돈다.
///
/// 처음에는 콜라이더 안에 몸이 들어왔는지로 판정했는데 두 번 갈아엎었다:
///  · 트리거 콜백은 서버에서 안 울린다 — 원격 클라의 플레이어는 NetworkTransform이 트랜스폼을 직접 써서
///    CharacterController.Move를 타지 않는다(실측 확인).
///  · 위치 폴링으로 바꾸니 이번엔 <b>조작이 까다로워졌다</b> — 몸을 구역 안에 정확히 내려놓아야 했다.
/// 그래서 위치가 아니라 상호작용으로 확정한다: 겨냥해서 넣으면 끝이고, 어디에 눕혔는지는 상관없다.
/// 안치된 몸은 서버가 자리로 스냅하므로 그림도 항상 같다.
///
/// 자리는 하나다. 진행 중에 누가 밧줄로 다시 끌어가면(꺼내기) 타이머는 취소된다 — 반쯤 살린 몸을
/// 다시 데려가는 것도 선택지로 열어 둔다.
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

    [Header("표시")]
    [Tooltip("남은 시간을 띄울 월드 라벨 — 비우면 표시 없이 동작한다")]
    [SerializeField]
    private TestWorldLabel m_label;

    [SerializeField]
    private string m_idleText = "부활 장치";

    // 안치된 몸 — 서버(또는 오프라인)에서만 유효. 자리는 하나뿐이다.
    private PlayerIncapacitation m_occupant;
    private float m_elapsed;

    // 남은 시간(초) — 0이면 비어 있다. 라벨을 전 피어가 같은 값으로 그리기 위해 동기화한다.
    private readonly NetworkVariable<float> m_remainingSynced = new();

    private int m_shownSeconds = -1; // 라벨 갱신 스로틀 — 초 단위가 바뀔 때만 텍스트를 만든다

    private Transform Slot => m_slot != null ? m_slot : transform;

    /// <summary>지금 몸이 들어 있는지 — 서버·오프라인은 실참조, 원격 피어는 동기화값. (PlayerEscorter 관례)</summary>
    public bool IsOccupied =>
        IsSpawned && !IsServer ? m_remainingSynced.Value > 0f : m_occupant != null;

    // ---- 상호작용 (오너 클라에서 호출됨) ----

    /// <summary>E가 실제로 동작하는 상태인지 — 조준 피드백(윤곽선)용. 서버 검증과 같은 기준. (#184)</summary>
    public bool CanInteract(GameObject interactor)
    {
        if (IsOccupied)
            return false;

        PlayerCarrier carrier = FindCarrier(interactor);
        return carrier != null && carrier.IsCarrying;
    }

    public void Interact(GameObject interactor)
    {
        PlayerCarrier carrier = FindCarrier(interactor);
        if (carrier == null)
            return;

        // 서버 권위 — 요청만 넘긴다. 대상·거리 판정은 서버가 한다 (#118 관례)
        carrier.RequestPlaceInDevice(this);
    }

    private static PlayerCarrier FindCarrier(GameObject interactor) =>
        interactor != null ? interactor.GetComponentInParent<PlayerCarrier>() : null;

    // ---- 서버 실행 (권위) ----

    /// <summary>
    /// 운반 중인 몸을 안치한다 — <see cref="PlayerCarrier"/>가 서버에서 호출. 성공하면 true.
    /// 위조 RPC 방어를 겸해 여기서 상태·거리를 다시 본다.
    /// </summary>
    public bool ServerPlace(PlayerCarrier carrier)
    {
        if (IsSpawned && !IsServer)
            return false;
        if (m_occupant != null || carrier == null)
            return false; // 자리는 하나

        PlayerCarrier body = carrier.CarriedTarget;
        if (body == null)
            return false; // 아무도 안 끌고 있다

        PlayerIncapacitation incapacitation = body.GetComponent<PlayerIncapacitation>();
        if (incapacitation == null || !incapacitation.IsDead)
            return false;

        // 거리 — 겨냥만으로는 부족하다(위조 RPC). 장치 앞까지 실제로 와야 한다
        if ((carrier.transform.position - transform.position).sqrMagnitude
            > m_placeRange * m_placeRange)
            return false;

        // 끌기를 먼저 끊고 나서 옮긴다 — 순서를 뒤집으면 추종이 살아 있어 몸이 자리에서 다시 끌려 나온다
        carrier.ServerDrop("부활 장치에 안치");

        // 몸 위치는 오너 권한이라 서버가 직접 못 옮긴다 — 오너에게 넘기는 텔레포트 경로를 쓴다 (#101/#214)
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
            return; // 타이머는 서버 권위
        if (m_occupant == null)
            return;

        // 다른 경로로 복구됐거나(라운드 리셋 등) 몸이 사라졌으면 자리를 비운다
        if (!m_occupant.IsDead)
        {
            ClearOccupant("대상이 이미 복구됨");
            return;
        }

        // 누가 다시 밧줄로 끌어갔다 — 꺼내기다. 진행은 버린다(다시 넣으면 처음부터)
        PlayerCarrier body = m_occupant.GetComponent<PlayerCarrier>();
        if (body != null && body.IsBeingCarried)
        {
            ClearOccupant("꺼내짐 — 진행 취소");
            return;
        }

        m_elapsed += Time.deltaTime;
        SetRemaining(Mathf.Max(0f, m_revivalSeconds - m_elapsed));

        if (m_elapsed >= m_revivalSeconds)
            Revive();
    }

    private void Revive()
    {
        PlayerData data = m_occupant.GetComponent<PlayerData>();
        PlayerIncapacitation revived = m_occupant;
        ClearOccupant(null);

        if (data == null)
        {
            Debug.LogWarning($"[본부 부활] PlayerData가 없어 부활할 수 없다 — {revived.name}", this);
            return;
        }

        // 부활은 구조와 같은 경로를 쓴다 — HP 부분 회복 + 무력화 해제 (PlayerData.ServerRevive)
        data.ServerRevive();
        Debug.Log($"[본부 부활] 복구 완료 — {revived.name} HP={data.CurrentHp}, 상태={revived.Cause}");
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
            m_remainingSynced.Value = seconds; // 오프라인 Play 테스트 — 로컬 값으로만 쓰인다
    }

    // 라벨은 전 피어에서 각자 그린다 — 남은 시간이 초 단위로 바뀔 때만 텍스트를 새로 만든다.
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
        base.OnDestroy(); // NetworkBehaviour 내부 정리 — 반드시 호출
    }
}
