using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerInteractor : NetworkBehaviour
{
    [Header("레이캐스트")]
    [SerializeField] private Camera m_camera;
    [SerializeField] private float m_range = 3f;
    [SerializeField] private LayerMask m_interactMask = ~0;

    public IInteractable CurrentInteractable { get; private set; }
    public GameObject CurrentTarget { get; private set; } // 아이템 타겟팅/UI용

    /// <summary>조준 대상이 바뀔 때 발행 — 조준 피드백(아웃라인·크로스헤어)용. null = 대상 없음. (#184)</summary>
    public event System.Action<GameObject> OnTargetChanged;

    /// <summary>상호작용 레이캐스트 사거리(m). 서버 줍기 거리 검증(#147)이 같은 값을 재사용한다.</summary>
    public float Range => m_range;

    /// <summary>레이캐스트 기준점(카메라 위치). 카메라 미배정 시 플레이어 루트로 대체.
    /// 서버 줍기 거리 검증(#147)이 클라이언트 조준과 동일한 기준점을 쓰기 위해 참조한다.</summary>
    public Transform AimOrigin => m_camera != null ? m_camera.transform : transform;

    private PlayerInputHandler m_inputHandler;
    private PlayerEscorter m_escorter;

    public override void OnNetworkSpawn()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
        // 연행 중 E 입력의 "놓기" 선점 판정용 — 없는 구성(테스트 등)이면 null (#91)
        m_escorter = GetComponent<PlayerEscorter>();
        if (m_camera == null) m_camera = Camera.main;

        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        m_inputHandler.OnInteractPerformed += HandleInteract;
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner) return;

        m_inputHandler.OnInteractPerformed -= HandleInteract;
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
        if (Physics.Raycast(ray, out RaycastHit hit, m_range, m_interactMask))
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

    private void HandleInteract()
    {
        // 연행 중 E는 놓기가 최우선 — 다른 대상을 겨냥하고 있어도 이번 입력은 놓기로 소비한다 (#91)
        // (PlayerEscorter가 따로 입력을 구독하면 놓기+제압이 한 입력에 동시 발동하는 이중 소비가 생긴다)
        if (m_escorter != null && m_escorter.IsEscorting)
        {
            // Release() 직접 호출은 서버 가드에 막힌다 — 요청 API로 서버에 넘긴다 (#118)
            Debug.Log("E 입력 — 연행 놓기 요청");
            m_escorter.RequestRelease();
            return;
        }

        CurrentInteractable?.Interact(gameObject);
    }
}
