using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 오너 화면 전용 — 쓰러진 동료를 조준하면 구조·키트·뒤지기 안내를, 내가 쓰러지면 상태 문구를 공용 프롬프트에 띄운다.
/// </summary>
[RequireComponent(typeof(PlayerReviver))]
public class PlayerReviveHud : NetworkBehaviour
{
    [Header("상태 문구")]
    [Tooltip(
        "동료가 나를 구조하는 중 — HudTable/Hud.Revive.BeingRevived. 유예 시계가 멈췄다는 신호"
    )]
    [SerializeField]
    private LocalizedString m_beingRevivedPrompt;

    [Tooltip("내가 기능 정지(Die) — HudTable/Hud.Revive.SelfDead")]
    [SerializeField]
    private LocalizedString m_selfDeadPrompt;

    [Tooltip(
        "부활 키트를 들고 Down·Die 중 — HudTable/Hud.Revive.SelfKit. {0}=상호작용 키(홀드). (#820)"
    )]
    [SerializeField]
    private LocalizedString m_selfReviveHintPrompt;

    [Header("행동 문구")]
    [Tooltip("다운된 아군을 조준 중 — HudTable/Hud.Revive.Hint. {0}=일으키기 키, {1}=뒤지기 키")]
    [SerializeField]
    private LocalizedString m_revivePrompt;

    [Tooltip(
        "기능 정지된 아군을 조준 중 — HudTable/Hud.Revive.DeadTarget. {0}=부활 키트 키, {1}=뒤지기 키"
    )]
    [SerializeField]
    private LocalizedString m_deadTargetPrompt;

    private PlayerReviver m_reviver;
    private PlayerIncapacitation m_incapacitation;
    private PlayerInputHandler m_input;
    private PlayerSelfRevive m_selfRevive;

    private LocalizedString m_shown;

    private string m_shownKey;
    private string m_shownKey2;

    private bool m_isLocalPlayer;

    public override void OnNetworkSpawn()
    {
        m_isLocalPlayer = IsOwner;

        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        m_reviver = GetComponent<PlayerReviver>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_input = GetComponent<PlayerInputHandler>();
        m_selfRevive = GetComponent<PlayerSelfRevive>();
    }

    public override void OnNetworkDespawn()
    {
        if (m_isLocalPlayer)
        {
            ClearPrompt();
            App.UI.DamageVignette?.HideDownCountdown();
        }
    }

    private void Update()
    {
        if (
            App.UI.Current != null
            && App.UI.Current.TryGetPanel(out SettlementPanel settlement)
            && settlement.IsOpened
        )
        {
            ClearPrompt();
            App.UI.DamageVignette?.HideDownCountdown();
            return;
        }

        bool isDowned = m_incapacitation != null && m_incapacitation.IsDowned;
        if (!isDowned)
            App.UI.DamageVignette?.HideDownCountdown();

        if (isDowned)
        {
            App.UI.DamageVignette?.ShowDownCountdown(
                Mathf.CeilToInt(m_incapacitation.RemainingUntilDie)
            );

            if (m_incapacitation.IsBeingRevived)
                SetPrompt(m_beingRevivedPrompt);
            else if (m_selfRevive != null && m_selfRevive.CanSelfRevive)
                SetPrompt(m_selfReviveHintPrompt, InteractKey);
            else
                ClearPrompt();
            return;
        }

        if (m_incapacitation != null && m_incapacitation.IsDead)
        {
            if (m_selfRevive != null && m_selfRevive.CanSelfRevive)
                SetPrompt(m_selfReviveHintPrompt, InteractKey);
            else if (m_incapacitation.IsRevivable)
                SetPrompt(m_selfDeadPrompt);
            else
                ClearPrompt();
            return;
        }

        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
        {
            ClearPrompt();
            return;
        }

        if (App.UI.InteractPrompt != null && App.UI.InteractPrompt.IsPromptShowing)
        {
            ClearPrompt();
            return;
        }

        if (m_reviver != null && m_reviver.CurrentReviveTarget != null)
        {
            SetPrompt(m_revivePrompt, InteractKey, LootKey);
            return;
        }

        if (m_reviver != null && m_reviver.CurrentDeadTarget != null)
        {
            SetPrompt(m_deadTargetPrompt, UseItemKey, LootKey);
            return;
        }

        ClearPrompt();
    }

    private string InteractKey => m_input != null ? m_input.InteractBinding : "?";

    private string UseItemKey => m_input != null ? m_input.UseItemBinding : "?";

    private string LootKey => m_input != null ? m_input.LootBinding : "?";

    /// <summary>keyLabel(들)이 있으면 문구의 인자({0}, {1})에 끼운다.</summary>
    private void SetPrompt(LocalizedString prompt, string keyLabel = null, string keyLabel2 = null)
    {
        if (prompt == null || prompt.IsEmpty)
            return;

        if (ReferenceEquals(m_shown, prompt))
        {
            if (keyLabel != m_shownKey || keyLabel2 != m_shownKey2)
            {
                m_shownKey = keyLabel;
                m_shownKey2 = keyLabel2;
                ApplyArguments(prompt, keyLabel, keyLabel2);
                prompt.RefreshString();
            }
            return;
        }

        ApplyArguments(prompt, keyLabel, keyLabel2);

        m_shown = prompt;
        m_shownKey = keyLabel;
        m_shownKey2 = keyLabel2;
        App.UI.Prompt?.Show(prompt);
    }

    private static void ApplyArguments(LocalizedString prompt, string keyLabel, string keyLabel2)
    {
        if (keyLabel == null)
            return;

        prompt.Arguments =
            keyLabel2 == null ? new object[] { keyLabel } : new object[] { keyLabel, keyLabel2 };
    }

    private void ClearPrompt()
    {
        if (m_shown == null)
            return;

        App.UI.Prompt?.Hide(m_shown);
        m_shown = null;
        m_shownKey = null;
        m_shownKey2 = null;
    }
}
