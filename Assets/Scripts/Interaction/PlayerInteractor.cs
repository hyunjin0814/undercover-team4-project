using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerInteractor : NetworkBehaviour
{
    [Header("레이캐스트")]
    [SerializeField] private Camera m_camera;
    [SerializeField] private float m_range = 3f;
    [SerializeField] private LayerMask m_interactMask = ~0;

    [Tooltip("시야를 가로막는 장애물 레이어 — 벽·건물(Default). 여기 걸리면 대상으로 잡지 않는다")]
    [SerializeField] private LayerMask m_losBlockMask = 1; // Default

    // 가시선 검사를 끝점 직전에서 멈추는 여유(m) — 대상이 딛고 선 바닥이 가림으로 잡히는 것을 막는다.
    private const float k_losEndMargin = 0.05f;

    public IInteractable CurrentInteractable { get; private set; }
    public GameObject CurrentTarget { get; private set; } // 아이템 타겟팅/UI용

    /// <summary>조준 대상이 바뀔 때 발행 — 조준 피드백(아웃라인·크로스헤어)용. null = 대상 없음. (#184)</summary>
    public event System.Action<GameObject> OnTargetChanged;

    /// <summary>상호작용 레이캐스트 사거리(m). 서버 줍기 거리 검증(#147)이 같은 값을 재사용한다.</summary>
    public float Range => m_range;

    /// <summary>레이캐스트 기준점(카메라 위치). 카메라 미배정 시 플레이어 루트로 대체.
    /// 서버 줍기 거리 검증(#147)이 클라이언트 조준과 동일한 기준점을 쓰기 위해 참조한다.</summary>
    public Transform AimOrigin => m_camera != null ? m_camera.transform : transform;

    /// <summary>조준 카메라. 조준 대상의 월드→스크린 좌표 변환(#233 스캔 정보 추종)에 쓴다. 미배정이면 null.</summary>
    public Camera AimCamera => m_camera;

    /// <summary>시야를 막는 장애물 레이어. 서버 드롭 위치 보정(#360)이 "벽"의 정의를 여기서 재사용한다 —
    /// 값을 따로 두면 조준은 막히는데 드롭은 통과하는 식으로 어긋난다.</summary>
    public LayerMask LosBlockMask => m_losBlockMask;

    private PlayerInputHandler m_inputHandler;
    private PlayerEscorter m_escorter;
    private PlayerIncapacitation m_incapacitation;

    public override void OnNetworkSpawn()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
        // 연행 중 E 입력의 "놓기" 선점 판정용 — 없는 구성(테스트 등)이면 null (#91)
        m_escorter = GetComponent<PlayerEscorter>();
        // 행동불능 중 상호작용 차단용 — 이동/아이템은 각자 게이팅하지만 E 상호작용은 공백이었다 (#101)
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        if (m_camera == null) m_camera = Camera.main;

        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        m_inputHandler.OnInteractPerformed += HandleInteract;
        m_inputHandler.OnInteractCanceled += HandleInteractReleased; // 도주 제압 홀드 뗌 취소 (#332)
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner) return;

        m_inputHandler.OnInteractPerformed -= HandleInteract;
        m_inputHandler.OnInteractCanceled -= HandleInteractReleased;
    }

    private void Update()
    {
        UpdateTarget();
    }

    private void UpdateTarget()
    {
        if (m_camera == null) return;

        GameObject previousTarget = CurrentTarget;

        Ray ray = new Ray(m_camera.transform.position, m_camera.transform.forward);
        if (
            Physics.Raycast(ray, out RaycastHit hit, m_range, m_interactMask)
            && HasLineOfSight(ray.origin, hit.point, hit.transform)
        )
        {
            CurrentTarget = hit.collider.gameObject;
            CurrentInteractable = hit.collider.GetComponentInParent<IInteractable>();
        }
        else
        {
            CurrentTarget = null;
            CurrentInteractable = null;
        }

        if (CurrentTarget != previousTarget) OnTargetChanged?.Invoke(CurrentTarget);
    }

    /// <summary>
    /// 서버 판정용 가시선 검사 — 조준 기준점(AimOrigin)에서 대상이 벽에 가리지 않았는가. (#360)
    /// 서버 사거리 검증(#147)만으로는 위조 RPC로 벽 너머 줍기·제압·구조·스캔이 뚫리므로,
    /// 각 서버 판정이 사거리와 함께 이걸 통과해야 한다. 클라 조준과 같은 기준점·마스크를 쓴다.
    /// </summary>
    public bool HasLineOfSightTo(Transform target)
    {
        // 끝점은 대상 콜라이더의 중심 — 루트 원점은 대개 발밑이라 바닥(Default)에 걸려 오차단된다.
        // 비재귀 조회라 판정 콜라이더가 루트에 있다는 전제 — 아이템·NPC·플레이어 모두 성립한다.
        Vector3 point = target.TryGetComponent(out Collider targetCollider)
            ? targetCollider.bounds.center
            : target.position;

        return HasLineOfSight(AimOrigin.position, point, target);
    }

    // 조준 레이캐스트는 Interactable 레이어만 보므로 벽(Default)을 그냥 통과한다 — 대상 확정 후
    // 여기서 장애물만 따로 본다. (마스크에 벽을 넣으면 본부 트리거 존이 레이를 가로채고, 트리거를
    // 무시하자니 줍기 콜라이더가 트리거라(#263) 줍기가 통째로 죽는다.)
    // 대상 자신의 콜라이더는 가림으로 치지 않는다 — 폭탄은 몸통(Default)이 배선(Interactable)을 감싼다.
    private bool HasLineOfSight(Vector3 origin, Vector3 point, Transform target)
    {
        Vector3 toPoint = point - origin;
        float distance = toPoint.magnitude - k_losEndMargin;
        if (distance <= 0f)
        {
            return true; // 코앞 — 가릴 것이 들어갈 틈이 없다
        }

        return !Physics.Raycast(
                origin,
                toPoint / toPoint.magnitude,
                out RaycastHit blocker,
                distance,
                m_losBlockMask,
                QueryTriggerInteraction.Ignore
            )
            || blocker.transform.root == target.root;
    }

    private void HandleInteract()
    {
        // 커서가 풀려 있으면(인벤토리 편집 등 UI 조작 중) 월드 상호작용은 막는다 — 아이템 사용과 동일 (#352)
        if (CursorLock.IsUnlocked)
            return;

        // 행동불능(HP 다운·오검거 매달기) 중엔 상호작용 불가 — 이동·아이템과 동일하게 게이트한다 (#101/#105)
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return;

        // 밧줄 놓기는 **조준 대상 기준**이다 (#390). 여러 명을 동시에 끌 수 있어 "끌고 있으면 무조건 놓기"로는
        // 무엇을 놓을지 정할 수 없고, 끄는 동안 다른 대상에게 E(제압·끌기 재개)를 쓸 방법도 사라진다.
        // (PlayerEscorter가 따로 입력을 구독하면 놓기+제압이 한 입력에 동시 발동하는 이중 소비가 생긴다)
        NpcController aimed = CurrentTarget != null
            ? CurrentTarget.GetComponentInParent<NpcController>()
            : null;
        if (m_escorter != null && m_escorter.IsDraggingNpc(aimed))
        {
            // 예외: 인계 단말처럼 '끌고 온 상태에서만 의미 있는' 대상은 놓기보다 우선한다 (#414).
            // CanInteract를 함께 보므로 조준 윤곽선이 켜진 조건과 실제로 E가 먹히는 조건이 일치하고,
            // 조건이 어긋나면 아래 놓기로 흘러가 끌던 NPC를 놓을 방법이 사라지지 않는다.
            IInteractable priority = CurrentInteractable;
            if (priority != null && priority.TakesPriorityOverRelease(gameObject) && priority.CanInteract(gameObject))
            {
                priority.Interact(gameObject);
                return;
            }

            // ReleaseDrag 직접 호출은 서버 가드에 막힌다 — 요청 API로 서버에 넘긴다 (#118)
            Debug.Log($"E 입력 — 밧줄 끌기 놓기 요청: {aimed.name}");
            m_escorter.RequestRelease(aimed);
            return;
        }

        CurrentInteractable?.Interact(gameObject);
    }

    // E 뗌 — 도주 제압 홀드 중이면 취소한다. 서버가 채널링 종류(m_subdueChanneling)로 가드하므로
    // 홀드 중이 아닐 때의 E 뗌은 무동작이고, 수갑 채널링(좌클릭)을 오발로 끊지도 않는다 (#332)
    private void HandleInteractReleased()
    {
        m_escorter?.RequestCancelSubdue();
    }
}
