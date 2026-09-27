using UnityEngine;

/// <summary>
/// 다운·사망 중 E 홀드 입력을 소지한 부활 키트로 넘겨 자가 부활을 요청하는 입력 훅.
/// </summary>
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerSelfRevive : MonoBehaviour
{
    private PlayerInputHandler m_inputHandler;
    private PlayerLoadout m_loadout;
    private PlayerIncapacitation m_incapacitation;

    private void Awake()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_loadout = GetComponent<PlayerLoadout>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
    }

    private void OnEnable()
    {
        m_inputHandler.OnInteractStarted += HandleInteractStarted;
        m_inputHandler.OnInteractCanceled += HandleInteractCanceled;
    }

    private void OnDisable()
    {
        m_inputHandler.OnInteractStarted -= HandleInteractStarted;
        m_inputHandler.OnInteractCanceled -= HandleInteractCanceled;
    }

    public ReviveKit HeldKit => m_loadout != null ? m_loadout.HeldReviveKit : null;

    public bool CanSelfRevive =>
        m_incapacitation != null
        && (m_incapacitation.IsDowned || m_incapacitation.IsRevivable)
        && HeldKit != null;

    private void HandleInteractStarted()
    {
        if (CursorLock.IsUnlocked)
            return;

        ReviveKit kit = HeldKit;
        if (kit == null || !CanSelfRevive)
            return;

        kit.RequestSelfRevive();
    }

    private void HandleInteractCanceled()
    {
        HeldKit?.RequestCancelSelfRevive();
    }
}
