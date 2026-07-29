using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 유치장 창살 문 (#415) — 플레이어가 다가오면 저절로 열리는 미닫이 자동문. 문짝이 옆으로 미끄러진다.
///
/// <b>플레이어만 막는다.</b> 닫힌 문짝의 콜라이더는 CharacterController(플레이어)를 막지만
/// NavMeshAgent(NPC)는 NavMesh만 따라가므로 통과한다 — 그래서 자동 수감 이송(NpcJailedState)을
/// 깨지 않고, 신병을 직접 끌고 들어가는 흐름(밧줄 끌기는 위치를 직접 세팅한다)에도 걸리지 않는다.
/// 시민이 유치장에 못 들어가는 것은 이 문이 아니라 NavMesh 영역(Jail) 게이팅이 담당한다.
///
/// <b>플레이어는 상호작용키(E)로 여닫고, NPC 앞에서는 저절로 열린다.</b> 경찰은 유치장에 드나들 권한이
/// 있으니 자물쇠(<see cref="JailLock"/>) 잠김과 무관하게 E가 먹힌다. E는 <b>토글</b>이라 한 번 열면 다시
/// 누를 때까지 열려 있다 — 신병을 끌고 드나드는 동안 등 뒤에서 닫히지 않게 하기 위해서다.
/// 자동 개폐는 <b>수감 이송(Jailed) NPC</b>에게만 적용한다: 스스로 걸어 들어가는 대상이라 열어 줄 주체가
/// 없기 때문이다. 그래서 플레이어가 E로 닫아 둔 동안에도 이송 중인 NPC 앞에서는 문이 열린다.
/// 자물쇠가 풀린 동안(탈옥, #231)에는 아무도 없어도 계속 열어 둔다: "문이 열려 있다"가 탈옥을 알아채는
/// 신호이기 때문이다.
///
/// 씬 배치: 조준용 콜라이더를 <b>Interactable 레이어</b>에 둘 것 — PlayerInteractor의 조준 마스크가 그
/// 레이어만 본다 (HqDropoffTerminal과 같은 관례).
///
/// 서버 권위 — 개폐 판단과 상태는 서버가 정해 NetworkVariable로 전 피어에 동기화하고(#56),
/// 미끄러지는 연출은 각 피어가 로컬로 보간한다.
///
/// 씬 배치: 문짝(m_leaf)은 NavMesh 베이크에서 제외할 것(NavMeshModifier의 Ignore From Build) —
/// 닫힌 문짝이 베이크에 잡히면 문턱의 NavMesh가 끊겨 수감 이송 경로가 사라진다.
/// </summary>
public class JailDoor : NetworkBehaviour, IInteractable
{
    [Header("문짝 (미끄러지는 창살 게이트)")]
    [SerializeField] private Transform m_leaf;

    [Tooltip("열릴 때 문짝이 이동하는 오프셋(문짝의 부모 기준). 개구부 폭만큼 옆으로 밀면 통로가 완전히 열린다")]
    [SerializeField] private Vector3 m_openOffset = new Vector3(-1.05f, 0f, 0f);

    [Tooltip("완전히 열리거나 닫히는 데 걸리는 시간(초)")]
    [SerializeField] private float m_slideSeconds = 0.7f;

    [Header("자동 개폐 (수감 이송 NPC 전용 — 플레이어는 E 토글)")]
    [Tooltip("이 거리(m) 안에 수감 이송(Jailed) NPC가 들어오면 저절로 열리고, 벗어나면 닫힌다")]
    [SerializeField] private float m_autoOpenRadius = 3f;

    [Tooltip("근접 검사 주기(초) — 매 프레임 돌 필요가 없다. 0이면 매 프레임 검사한다")]
    [SerializeField] private float m_proximityCheckInterval = 0.1f;

    [Header("자물쇠·유치장 (비우면 부모에서 자동 탐색)")]
    [Tooltip("풀려 있고 아직 수감자가 남아 있는 동안(탈옥 진행 중, #231)에는 근처에 아무도 없어도 열어 둔다")]
    [SerializeField] private JailLock m_jailLock;

    [Tooltip("수감자가 남아 있는지 확인용 — 다 빠져나간 빈 유치장이면 문을 닫는다")]
    [SerializeField] private JailZone m_jailZone;

    // 서버 권위 개폐 상태 — JailLock·JailZone과 동일한 이중 구조(오프라인 폴백 로컬 값)
    private readonly NetworkVariable<bool> m_isOpenSynced = new NetworkVariable<bool>(false);
    private bool m_localIsOpen;

    // 닫힌 위치 — Awake에 잡아 두고 여기에 m_openOffset을 더한 곳이 열린 위치가 된다
    private Vector3 m_closedLocalPosition;

    // 다음 근접 검사까지 남은 시간
    private float m_proximityCooldown;

    // 플레이어가 E로 걸어 둔 개방 — 다시 누를 때까지 유지된다. 개폐 판단이 서버에서만 돌므로
    // 동기화하지 않는다(결과인 m_isOpenSynced만 전 피어가 본다). 서버(또는 오프라인) 전용.
    private bool m_manualOpen;

    /// <summary>문이 열려 있는가. 세션 중에는 동기화된 값이라 클라이언트에서도 읽을 수 있다.</summary>
    public bool IsOpen => IsSpawned ? m_isOpenSynced.Value : m_localIsOpen;

    /// <summary>개폐 전환 — 소리·램프 연출이 구독할 훅. 전 피어에서 발생한다.</summary>
    public event System.Action<bool> OnOpenChanged;

    private void Awake()
    {
        if (m_leaf != null)
            m_closedLocalPosition = m_leaf.localPosition;

        // 자물쇠는 같은 유치장 오브젝트에 있다 — 부모 쪽에서 찾는다 (JailZone.Awake와 같은 관례)
        if (m_jailLock == null)
            m_jailLock = GetComponentInParent<JailLock>();

        if (m_jailZone == null)
            m_jailZone = GetComponentInParent<JailZone>();
    }

    public override void OnNetworkSpawn() => m_isOpenSynced.OnValueChanged += HandleOpenSyncedChanged;

    public override void OnNetworkDespawn() => m_isOpenSynced.OnValueChanged -= HandleOpenSyncedChanged;

    private void HandleOpenSyncedChanged(bool previous, bool current) => OnOpenChanged?.Invoke(current);

    // ---- 자동 개폐 판단 (서버 권위) ----

    // 근처에 플레이어가 있으면 연다. 자물쇠가 풀린 동안에는 아무도 없어도 계속 열어 둔다 (#231).
    private void ServerTickAutoDoor()
    {
        m_proximityCooldown -= Time.deltaTime;
        if (m_proximityCooldown > 0f)
            return;
        m_proximityCooldown = m_proximityCheckInterval;

        ServerSetOpen(ShouldBeOpen());
    }

    // 지금 문이 열려 있어야 하는가 — 수동 개방(E) · 이송 NPC 근접 · 탈옥 진행 중 셋의 합.
    // 플레이어 근접은 보지 않는다: 여닫는 것은 플레이어의 명시적 입력(E)이다.
    private bool ShouldBeOpen() =>
        m_manualOpen || IsJailBoundNpcNear(DoorCenter) || IsJailbreakHoldingOpen;

    // 탈옥이 '진행 중'일 때만 열어 둔다 — 자물쇠가 풀렸어도 수감자가 다 빠져나갔으면 닫는다.
    // 안 그러면 마지막 수감자가 나간 뒤 다음 수감자가 들어와 재잠금될 때까지 영영 열려 있다.
    // Inmates는 서버 권위 집합이라 이 판단은 서버(또는 오프라인)에서만 유효하다.
    private bool IsJailbreakHoldingOpen =>
        m_jailLock != null && !m_jailLock.IsLocked
        && m_jailZone != null && m_jailZone.Inmates.Count > 0;

    // ---- 플레이어 상호작용 (E 토글) ----

    /// <summary>
    /// 유치장 문은 경찰 누구나 여닫을 수 있다 — 자물쇠 잠김·수감 인원과 무관하다.
    /// (사거리·가시선은 PlayerInteractor가 이미 걸러 준다)
    /// </summary>
    public bool CanInteract(GameObject interactor) => m_leaf != null;

    /// <summary>E 토글 — 열려 있으면 닫고, 닫혀 있으면 연다. (#415)</summary>
    public void Interact(GameObject interactor)
    {
        if (!IsSpawned)
        {
            ServerToggleManual(); // 오프라인 단독 테스트
            return;
        }

        RequestToggleRpc();
    }

    // 클라 입력을 서버로 넘긴다 — 소유권을 요구하지 않는다(씬 오브젝트이고 누구나 여닫는다).
    // 개폐 권위는 서버에 있으므로 여기서 상태를 직접 건드리지 않는다 (CCTVSwitcher와 같은 관례, #362).
    [Rpc(SendTo.Server)]
    private void RequestToggleRpc() => ServerToggleManual();

    // 수동 개방 래치를 뒤집는다 — 서버(또는 오프라인) 전용.
    // 닫기를 눌러도 이송 중인 NPC가 앞에 있거나 탈옥이 진행 중이면 문은 열린 채로 남는다 —
    // 그 둘은 플레이어가 막을 수 있는 개폐가 아니다(각각 이송 경로 보장, 탈옥 신호).
    private void ServerToggleManual()
    {
        if (IsSpawned && !IsServer)
            return;

        m_manualOpen = !m_manualOpen;

        // 다음 근접 검사 주기(최대 m_proximityCheckInterval)를 기다리지 않고 즉시 반영한다 —
        // 누르자마자 움직여야 입력이 먹혔다는 것이 보인다.
        ServerSetOpen(ShouldBeOpen());
    }

    // 문을 통과해야 하는 NPC가 반경 안에 있는가 — 수감 이송(Jailed)만 본다.
    // 배회 시민까지 세면 본부를 지나가는 것만으로 문이 계속 열려 있게 되고, 애초에 시민은
    // Jail 영역에 못 들어가므로(NavMesh 게이팅) 열어 줄 이유가 없다.
    // 연행(Escorted) 중인 대상은 끌고 있는 플레이어가 반경 안에 있으니 위 판정에서 이미 걸린다.
    //
    // <b>침입자(Intruding)는 일부러 뺐다.</b> 목표가 문 바깥의 접근 지점(JailLock.ApproachPoint)이라
    // 문을 통과할 일이 없고(#415에서 자물쇠가 우리 안이라 경로가 막히던 것을 밖으로 빼서 해결했다),
    // 문이 저절로 열리면 자물쇠를 해제해 탈옥을 일으킨다는 이벤트 전제가 무너진다 (#231).
    private bool IsJailBoundNpcNear(Vector3 center)
    {
        // 검사 주기(m_proximityCheckInterval)로 호출을 눌러 두었기에 목록 훑기로 충분하다.
        NpcController[] npcs = UnityEngine.Object.FindObjectsByType<NpcController>(FindObjectsSortMode.None);
        float sqrRadius = m_autoOpenRadius * m_autoOpenRadius;

        for (int i = 0; i < npcs.Length; i++)
        {
            if (npcs[i].CurrentState != NpcState.Jailed)
                continue;
            if ((npcs[i].transform.position - center).sqrMagnitude > sqrRadius)
                continue;

            // 이미 유치장 안으로 들어선 대상은 문을 잡아 두지 않는다 — 셀 지점이 문에서 1.6~2.5m라
            // 반경 안에 들어와, 수용이 끝나고도 문이 영영 열려 있었다. 들어가면 등 뒤로 닫힌다.
            if (IsInsideJailArea(npcs[i].transform.position))
                continue;

            return true;
        }

        return false;
    }

    // 근접 판정의 기준점 — 문짝의 <b>닫힌 위치</b>를 월드로 환산해 쓴다.
    // 문짝의 현재 위치를 쓰면 열린 뒤 기준점이 옆으로 옮겨가 반경을 벗어나고, 닫혔다 열리는 진동이 생긴다.
    // 이 오브젝트(문틀)의 원점도 부적합하다 — 실측상 개구부에서 1m 넘게 떨어져 있어 접근 방향에 따라
    // 문 앞에 서고도 반경 밖이 된다. 문짝이 없으면 문틀로 폴백한다.
    private Vector3 DoorCenter =>
        m_leaf != null && m_leaf.parent != null
            ? m_leaf.parent.TransformPoint(m_closedLocalPosition)
            : transform.position;

    /// <summary>
    /// 개폐 설정 — 서버(또는 오프라인) 전용. 자동 판단과 외부 강제(연출·치트)가 모두 이 지점을 지난다.
    /// </summary>
    public void ServerSetOpen(bool open)
    {
        if (IsSpawned && !IsServer)
            return;

        if (IsOpen == open)
            return;

        m_localIsOpen = open;

        if (IsSpawned && IsServer)
            m_isOpenSynced.Value = open; // OnValueChanged를 거쳐 모든 피어에서 이벤트 발생
        else if (!IsSpawned)
            OnOpenChanged?.Invoke(open);

        Debug.Log($"[유치장 문] {(open ? "열림" : "닫힘")}");
    }

    // ---- 연출 (전 피어 로컬) ----

    private void Update()
    {
        // 개폐 판단은 서버(또는 오프라인)만 한다 — 클라이언트는 동기화된 IsOpen을 보고 연출만 따라간다
        if (!IsSpawned || IsServer)
            ServerTickAutoDoor();

        TickLeafSlide();
    }

    private void TickLeafSlide()
    {
        if (m_leaf == null)
            return;

        Vector3 target = IsOpen ? m_closedLocalPosition + m_openOffset : m_closedLocalPosition;
        if (m_leaf.localPosition == target)
            return;

        // 속도는 "완주 시간"에서 역산한다 — 오프셋을 인스펙터에서 바꿔도 체감 속도가 유지된다
        float speed = m_slideSeconds > 0f ? m_openOffset.magnitude / m_slideSeconds : float.MaxValue;
        m_leaf.localPosition = Vector3.MoveTowards(m_leaf.localPosition, target, speed * Time.deltaTime);
    }

    // 유치장 내부(Jail) NavMesh 영역의 마스크 — 이름으로 한 번만 해석해 캐시한다.
    // (NpcController.JailAreaMask와 같은 관례. 0이면 Jail 영역이 없는 프로젝트라 판정을 생략한다)
    private static int s_jailAreaMask = -1;

    // Jail 영역 판정 허용치(m) — 위 IsInsideJailArea 주석의 실측 근거 참고
    private const float k_insideSampleRadius = 0.2f;

    private static int JailAreaMask
    {
        get
        {
            if (s_jailAreaMask < 0)
            {
                int area = NavMesh.GetAreaFromName("Jail");
                s_jailAreaMask = area >= 0 ? 1 << area : 0;
            }
            return s_jailAreaMask;
        }
    }

    // 이 지점이 유치장 내부(Jail 영역) 위인가.
    // <b>플레이어에는 적용하지 않는다</b> — 안에 있는 플레이어까지 빼면 나가려고 문 앞에 서도 열리지
    // 않아 갇힌다(문짝 콜라이더는 플레이어를 막는다). NPC는 문짝을 통과하므로 갇힐 일이 없다.
    private static bool IsInsideJailArea(Vector3 position)
    {
        int mask = JailAreaMask;
        if (mask == 0)
            return false;

        // 허용치는 0.2m — 실측상 Jail 영역은 문짝(z 49.70)보다 0.2m 안쪽(z 49.90)에서 시작한다.
        // 0.5m로 잡으면 문 밖 접근 지점(LockApproach)까지 "안"으로 걸려, 들어오려는 NPC 앞에서
        // 문이 열리지 않는다. 0.02m는 반대로 셀 지점도 놓친다.
        return NavMesh.SamplePosition(position, out NavMeshHit _, k_insideSampleRadius, mask);
    }
}
