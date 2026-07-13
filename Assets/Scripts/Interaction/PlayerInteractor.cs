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
    }

    private void HandleInteract()
    {
        // 연행 중 E는 놓기가 최우선 — 다른 대상을 겨냥하고 있어도 이번 입력은 놓기로 소비한다 (#91)
        // (PlayerEscorter가 따로 입력을 구독하면 놓기+제압이 한 입력에 동시 발동하는 이중 소비가 생긴다)
        if (m_escorter != null && m_escorter.IsEscorting)
        {
            m_escorter.Release();
            return;
        }

        CurrentInteractable?.Interact(gameObject);
    }
}
