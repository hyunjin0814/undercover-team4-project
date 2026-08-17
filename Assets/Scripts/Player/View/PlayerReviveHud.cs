using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 쓰러진 동료 관련 온스크린 프롬프트 — 오너 화면 전용. (#105/#493)
/// 기능 정지된 아군을 조준하면 부활 키트 안내를, 내가 기능 정지되면 키트를 기다리라는 메시지를 띄운다 (#613).
/// 현장 구조(다운) 쪽 분기는 #524로 휴면 상태지만, 되살릴 때 그대로 쓰도록 남겨 뒀다.
/// 문구는 HUD의 공용 프롬프트(<see cref="PromptView"/>)에 얹는다 — 이 클래스는 상태를 보고
/// 어떤 문구를 띄울지만 고른다.
///
/// <b>월드 아이콘(채워지는 해골)을 시도했다가 되돌렸다</b>: 평면 스프라이트라 옆에서 보면 보이지
/// 않고, 상태를 알리는 수단으로 텍스트보다 나을 게 없었다. 시각화로 다시 갈 거면 평면 이미지가
/// 아닌 방식(캐릭터 자체의 연출 등)이어야 한다.
/// </summary>
[RequireComponent(typeof(PlayerReviver))]
public class PlayerReviveHud : NetworkBehaviour
{
    [Header("상태 문구")]
    [Tooltip(
        "내가 다운됨 — HudTable/Hud.Revive.Downed. #524로 다운이 발생하지 않아 현재는 뜨지 않는다"
    )]
    [SerializeField]
    private LocalizedString m_downedPrompt;

    [Tooltip("내가 기능 정지(Die) — HudTable/Hud.Revive.SelfDead")]
    [SerializeField]
    private LocalizedString m_selfDeadPrompt;

    [Header("행동 문구")]
    [Tooltip("다운된 아군을 조준 중 — HudTable/Hud.Revive.Hint")]
    [SerializeField]
    private LocalizedString m_revivePrompt;

    [Tooltip("기능 정지된 아군을 조준 중 — HudTable/Hud.Revive.DeadTarget")]
    [SerializeField]
    private LocalizedString m_deadTargetPrompt;

    private PlayerReviver m_reviver;
    private PlayerIncapacitation m_incapacitation;
    private PlayerInputHandler m_input; // 문구에 끼울 키 표기 (#664)

    // 지금 띄워 둔 문구 — 매 프레임 같은 것을 다시 띄워 재구독하지 않도록 기억한다
    private LocalizedString m_shown;

    // 문구에 마지막으로 끼운 키 표기. null은 키를 적지 않는 문구. (#664)
    private string m_shownKey;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false; // 남의 플레이어 것이 내 화면에 그려지지 않게 (오너 전용 HUD)
            return;
        }

        m_reviver = GetComponent<PlayerReviver>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_input = GetComponent<PlayerInputHandler>();
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner)
            ClearPrompt(); // 퇴장·씬 전환으로 사라질 때 문구가 화면에 남지 않게
    }

    private void Update() // 비오너는 OnNetworkSpawn에서 비활성화되므로 오너만 돈다
    {
        // 라운드 정산 화면이 떠 있으면 그 위로 겹쳐 그리지 않는다.
        // (전원 다운으로 라운드가 끝나면 나는 여전히 무력화 상태라 "다운됨"이 정산 위로 샌다)
        if (App.UI.Current != null
            && App.UI.Current.TryGetPanel(out SettlementPanel settlement)
            && settlement.IsOpened)
        {
            ClearPrompt();
            return;
        }

        // 내가 다운된 경우 — 구조 대기 메시지.
        // IsIncapacitated가 아니라 IsDowned를 본다 (#252): 기절·오검거 매달기도 무력화지만 스스로
        // 풀리므로 구조를 기다리라는 안내가 거짓이 된다. 아무도 오지 않는데 기다리게 만든다.
        // #524 이후 Down은 발생하지 않아 이 분기는 휴면 상태다 — 현장 구조를 되살릴 때 같이 깨어난다.
        // (Die까지 남은 시간을 함께 보여주던 카운트다운은 제한시간 자체가 사라져 문구에서도 빠졌다)
        if (m_incapacitation != null && m_incapacitation.IsDowned)
        {
            SetPrompt(m_downedPrompt);
            return;
        }

        // 내가 기능 정지(Die)된 경우 — 동료의 부활 키트를 기다려야 한다는 안내 (#364/#613)
        if (m_incapacitation != null && m_incapacitation.IsDead)
        {
            SetPrompt(m_selfDeadPrompt);
            return;
        }

        // 무력화 중에는 조준 안내를 적지 않는다 (#664). 위 두 분기가 걸러 낸 다운·기능 정지 말고도
        // 기절·페널티·납치가 여기로 내려오는데, 그동안은 좌클릭도 E도 가드에 막혀 아무것도 못 한다.
        // 조준 안내(InteractionFeedback)가 같은 상황에서 사라지므로 기준을 맞춘다.
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
        {
            ClearPrompt();
            return;
        }

        // 여기부터는 조준 중에 뜨는 안내다 — 조준 안내(#664)가 이미 떠 있으면 띄우지 않는다.
        // 같은 화면에 두 줄이 뜨고, 심하면 서로 다른 말을 한다(밧줄을 들고 기능 정지된 동료를
        // 겨누면 실제로 먹히는 것은 '업기'인데 이쪽은 '부활 키트를 들고'를 계속 적는다).
        if (App.UI.InteractPrompt != null && App.UI.InteractPrompt.IsPromptShowing)
        {
            ClearPrompt();
            return;
        }

        // 다운된 아군을 조준 중이면 구조 키 프롬프트 (#524로 휴면 — 위 IsDowned 분기와 같은 이유)
        if (m_reviver != null && m_reviver.CurrentReviveTarget != null)
        {
            SetPrompt(m_revivePrompt, keyLabel: InteractKey);
            return;
        }

        // 기능 정지된 아군을 조준 중 — 구조 채널링이 아니라 부활 키트가 답이다 (#364/#613).
        // 여기만 아이템 사용 키다 — 키트는 들고 쓰는 물건이라 E가 아니라 좌클릭이 답이다.
        if (m_reviver != null && m_reviver.CurrentDeadTarget != null)
        {
            SetPrompt(m_deadTargetPrompt, keyLabel: UseItemKey);
            return;
        }

        ClearPrompt();
    }

    // 키 표기 — 입력 처리기가 없는 구성(단독 테스트 등)이면 문구에 빈칸이 남지 않게 물음표를 적는다.
    private string InteractKey => m_input != null ? m_input.InteractBinding : "?";

    private string UseItemKey => m_input != null ? m_input.UseItemBinding : "?";

    /// <summary>
    /// keyLabel이 있으면 문구의 인자({0}) 자리에 키 표기를 끼운다. (#664)
    /// 남은 초를 끼우던 갈래는 없앴다 — 제한시간이 사라져 인자는 빠졌는데(#524) 문구에 {0}이 남아
    /// "기능 정지까지 초"로 렌더되고 있었다. 다운을 되살릴 땐 문구와 인자를 같이 되돌린다.
    /// </summary>
    private void SetPrompt(LocalizedString prompt, string keyLabel = null)
    {
        if (prompt == null || prompt.IsEmpty)
            return;

        if (ReferenceEquals(m_shown, prompt))
        {
            // 같은 문구다 — 키 표기만 바뀌었으면(재바인딩) 재구독 없이 갱신한다.
            // 매 프레임 Show를 다시 부르면 초당 수십 번 구독을 갈아치운다.
            if (keyLabel != m_shownKey)
            {
                m_shownKey = keyLabel;
                ApplyArguments(prompt, keyLabel);
                prompt.RefreshString(); // 이미 구독 중이므로 다시 포맷만 시킨다
            }
            return;
        }

        // 인자를 먼저 넣어야 구독 시점의 첫 발화부터 올바른 문장이 나온다
        ApplyArguments(prompt, keyLabel);

        m_shown = prompt;
        m_shownKey = keyLabel;
        App.UI.Prompt?.Show(prompt);
    }

    private static void ApplyArguments(LocalizedString prompt, string keyLabel)
    {
        if (keyLabel != null)
            prompt.Arguments = new object[] { keyLabel };
    }

    private void ClearPrompt()
    {
        if (m_shown == null)
            return;

        // 내가 띄운 것만 지운다 — 그 사이 다른 곳이 덮어썼으면 건드리지 않는다
        App.UI.Prompt?.Hide(m_shown);
        m_shown = null;
        m_shownKey = null;
    }
}
