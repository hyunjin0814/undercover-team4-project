using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 오너 전용 감정표현 입력 — 휠 열기, 마우스 델타 누적 조준, 발동, 이동 시 취소.
/// </summary>
[RequireComponent(typeof(PlayerEmote))]
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerEmoteInput : MonoBehaviour
{
    private const float k_aimPixelsToFull = 220f;

    private const float k_moveCancelThreshold = 0.2f;

    [SerializeField]
    private EmoteWheelView m_wheelView;

    private PlayerEmote m_emote;
    private PlayerInputHandler m_inputHandler;
    private PlayerLook m_look;

    private EmoteLoadout m_slots;
    private Vector2 m_aim;

    public bool IsWheelOpen { get; private set; }

    public Vector2 WheelDirection => m_aim;

    private void Awake()
    {
        m_emote = GetComponent<PlayerEmote>();
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_look = GetComponent<PlayerLook>();
        m_slots = new EmoteLoadout(App.Net.Auth != null ? App.Net.Auth.PlayerId : null);
        m_slots.Load();
        FillDefaultSlotsIfEmpty();
    }

    /// <summary>저장된 구성이 전혀 없으면 카탈로그 앞에서부터 8칸을 채운다.</summary>
    private void FillDefaultSlotsIfEmpty()
    {
        EmoteCatalog catalog = m_emote.Catalog;
        if (catalog == null)
            return;

        for (int slot = 0; slot < EmoteLoadout.k_slotCount; slot++)
        {
            if (!string.IsNullOrEmpty(m_slots.GetSlot(slot)))
                return;
        }

        int count = Mathf.Min(EmoteLoadout.k_slotCount, catalog.Count);
        for (int slot = 0; slot < count; slot++)
        {
            EmoteDefinition definition = catalog.Get(slot);
            if (definition != null)
                m_slots.SetSlot(slot, definition.Id);
        }
    }

    private void OnEnable()
    {
        m_inputHandler.OnEmoteWheelOpened += OpenWheel;
        m_inputHandler.OnEmoteWheelClosed += CloseWheelAndFire;
    }

    private void OnDisable()
    {
        m_inputHandler.OnEmoteWheelOpened -= OpenWheel;
        m_inputHandler.OnEmoteWheelClosed -= CloseWheelAndFire;

        if (IsWheelOpen)
            CloseWheel();
    }

    private void Update()
    {
        if (IsWheelOpen)
        {
            AccumulateAim();
            return;
        }

        if (m_emote.IsEmoting && ShouldCancel())
            m_emote.CancelEmote();
    }

    private void OpenWheel()
    {
        if (m_emote.IsEmoting)
            m_emote.CancelEmote();

        if (!IsWheelOpen && m_look != null)
            m_look.PushLookSuspend();

        IsWheelOpen = true;
        m_aim = Vector2.zero;

        if (m_wheelView != null)
            m_wheelView.Open(m_slots, m_emote.Catalog, this);
    }

    private void CloseWheelAndFire()
    {
        if (!IsWheelOpen)
            return;

        int slot = EmoteWheelGeometry.SlotFromDirection(m_aim);
        CloseWheel();

        if (slot < 0)
            return;

        string emoteId = m_slots.GetSlot(slot);
        if (string.IsNullOrEmpty(emoteId))
            return;

        EmoteCatalog catalog = m_emote.Catalog;
        if (catalog == null)
            return;

        int index = catalog.IndexOf(emoteId);
        if (index < 0)
            return;

        if (!CanStartHere())
            return;

        m_emote.RequestEmote(index);
    }

    private void CloseWheel()
    {
        if (IsWheelOpen && m_look != null)
            m_look.PopLookSuspend();

        IsWheelOpen = false;

        if (m_wheelView != null)
            m_wheelView.Close();
    }

    private void AccumulateAim()
    {
        if (Mouse.current == null)
            return;

        m_aim += Mouse.current.delta.ReadValue() / k_aimPixelsToFull;
        m_aim = Vector2.ClampMagnitude(m_aim, 1f);
    }

    private bool CanStartHere() => m_inputHandler.MoveInput.magnitude <= k_moveCancelThreshold;

    private bool ShouldCancel() => m_inputHandler.MoveInput.magnitude > k_moveCancelThreshold;
}
