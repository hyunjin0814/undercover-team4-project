using UnityEngine.Localization;

/// <summary>
/// 조준 안내 문구 모음 — <see cref="IInteractable.PromptLabel"/>이 돌려줄 문구를 모아 캐시한다. (#664)
/// 인스펙터 배선 대신 코드에 두는 이유: 대상이 20종이고 씬·프리팹에 여러 벌 놓여 있어 문구 하나
/// 고치는 데 전부 열어야 한다. 개체마다 달라야 하면 그 구현체만 SerializeField로 받으면 된다.
/// static readonly로 한 벌만 쓴다 — 매 프레임 불리고, 표시 쪽 재구독 판정이 참조 비교다.
/// </summary>
public static class InteractPrompts
{
    private const string k_table = "HudTable";

    // ---- 문 ----
    public static readonly LocalizedString DoorOpen = Of("Hud.Interact.DoorOpen");
    public static readonly LocalizedString DoorClose = Of("Hud.Interact.DoorClose");

    // ---- 유치장 ----
    public static readonly LocalizedString JailAdmit = Of("Hud.Interact.JailAdmit");
    public static readonly LocalizedString JailEnter = Of("Hud.Interact.JailEnter");
    public static readonly LocalizedString JailExit = Of("Hud.Interact.JailExit");
    public static readonly LocalizedString JailLock = Of("Hud.Interact.JailLock");

    // ---- 본부 단말 ----
    public static readonly LocalizedString Dispatch = Of("Hud.Interact.Dispatch");
    public static readonly LocalizedString Directory = Of("Hud.Interact.Directory");
    public static readonly LocalizedString TipCall = Of("Hud.Interact.TipCall");
    public static readonly LocalizedString RoundEnd = Of("Hud.Interact.RoundEnd");
    public static readonly LocalizedString CctvPower = Of("Hud.Interact.CctvPower");
    public static readonly LocalizedString CctvSwitch = Of("Hud.Interact.CctvSwitch");
    public static readonly LocalizedString RemoteDoorSelect = Of("Hud.Interact.RemoteDoorSelect");
    public static readonly LocalizedString RemoteDoorOpen = Of("Hud.Interact.RemoteDoorOpen");
    public static readonly LocalizedString FactionSymbol = Of("Hud.Interact.FactionSymbol");
    public static readonly LocalizedString MapSelect = Of("Hud.Interact.MapSelect");

    // ---- 아이템 ----
    public static readonly LocalizedString Pickup = Of("Hud.Interact.Pickup");
    public static readonly LocalizedString Shop = Of("Hud.Interact.Shop");
    public static readonly LocalizedString Charge = Of("Hud.Interact.Charge");
    public static readonly LocalizedString Decoder = Of("Hud.Interact.Decoder");
    public static readonly LocalizedString Siren = Of("Hud.Interact.Siren");

    // ---- 아이템 좌클릭 (밧줄은 줄을 거는 쪽, E는 손을 떼는 쪽 — #513) ----
    public static readonly LocalizedString Scan = Of("Hud.Interact.Scan");
    public static readonly LocalizedString RopeBind = Of("Hud.Interact.RopeBind");
    public static readonly LocalizedString RopeJoin = Of("Hud.Interact.RopeJoin");
    public static readonly LocalizedString RopeResume = Of("Hud.Interact.RopeResume");

    // ---- 신병 (E는 손을 떼는 쪽이다 — 줄을 거는 쪽은 밧줄 좌클릭, #513) ----
    public static readonly LocalizedString NpcUnrope = Of("Hud.Interact.NpcUnrope");
    public static readonly LocalizedString NpcUnropeMine = Of("Hud.Interact.NpcUnropeMine");
    public static readonly LocalizedString NpcRelease = Of("Hud.Interact.NpcRelease");
    public static readonly LocalizedString NpcHalt = Of("Hud.Interact.NpcHalt");
    public static readonly LocalizedString NpcEscortResume = Of("Hud.Interact.NpcEscortResume");
    public static readonly LocalizedString NpcJailRelease = Of("Hud.Interact.NpcJailRelease");

    // ---- 동료 ----
    public static readonly LocalizedString Loot = Of("Hud.Interact.Loot");
    public static readonly LocalizedString HandOverBody = Of("Hud.Interact.HandOverBody");
    public static readonly LocalizedString PutDownBody = Of("Hud.Interact.PutDownBody");
    public static readonly LocalizedString Revive = Of("Hud.Interact.Revive");
    public static readonly LocalizedString ReleaseAll = Of("Hud.Interact.ReleaseAll");

    // ---- 막힌 사유 ----
    public static readonly LocalizedString ReasonLocked = Of("Hud.Interact.Reason.Locked");
    public static readonly LocalizedString ReasonEscorting = Of("Hud.Interact.Reason.Escorting");
    public static readonly LocalizedString ReasonNoCustody = Of("Hud.Interact.Reason.NoCustody");

    private static LocalizedString Of(string key) => new LocalizedString(k_table, key);
}
