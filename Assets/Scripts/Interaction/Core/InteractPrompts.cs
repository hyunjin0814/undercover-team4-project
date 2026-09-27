using UnityEngine.Localization;

/// <summary>
/// IInteractable.PromptLabel이 돌려줄 조준 안내 문구를 static으로 캐시한 모음.
/// </summary>
public static class InteractPrompts
{
    private const string k_table = "HudTable";

    public static readonly LocalizedString DoorOpen = Of("Hud.Interact.DoorOpen");
    public static readonly LocalizedString DoorClose = Of("Hud.Interact.DoorClose");

    public static readonly LocalizedString JailAdmit = Of("Hud.Interact.JailAdmit");
    public static readonly LocalizedString JailEnter = Of("Hud.Interact.JailEnter");
    public static readonly LocalizedString JailExit = Of("Hud.Interact.JailExit");

    public static readonly LocalizedString Dispatch = Of("Hud.Interact.Dispatch");
    public static readonly LocalizedString Directory = Of("Hud.Interact.Directory");
    public static readonly LocalizedString TipCall = Of("Hud.Interact.TipCall");
    public static readonly LocalizedString RoundEnd = Of("Hud.Interact.RoundEnd");
    public static readonly LocalizedString CctvPower = Of("Hud.Interact.CctvPower");
    public static readonly LocalizedString CctvSwitch = Of("Hud.Interact.CctvSwitch");
    public static readonly LocalizedString CctvKeypad = Of("Hud.Interact.CctvKeypad");
    public static readonly LocalizedString CctvKeypadConfirm = Of("Hud.Interact.CctvKeypadConfirm");
    public static readonly LocalizedString CctvInfrared = Of("Hud.Interact.CctvInfrared");
    public static readonly LocalizedString WantedPage = Of("Hud.Interact.WantedPage");
    public static readonly LocalizedString RemoteDoorSelect = Of("Hud.Interact.RemoteDoorSelect");
    public static readonly LocalizedString RemoteDoorOpen = Of("Hud.Interact.RemoteDoorOpen");
    public static readonly LocalizedString FactionSymbol = Of("Hud.Interact.FactionSymbol");
    public static readonly LocalizedString MapSelect = Of("Hud.Interact.MapSelect");
    public static readonly LocalizedString BlackoutRecovery = Of("Hud.Interact.BlackoutRecovery");

    public static readonly LocalizedString Cosmetics = Of("Hud.Interact.Cosmetics");
    public static readonly LocalizedString Gacha = Of("Hud.Interact.Gacha");

    public static readonly LocalizedString Pickup = Of("Hud.Interact.Pickup");
    public static readonly LocalizedString OpenCrate = Of("Hud.Interact.OpenCrate");
    public static readonly LocalizedString Shop = Of("Hud.Interact.Shop");
    public static readonly LocalizedString Charge = Of("Hud.Interact.Charge");
    public static readonly LocalizedString Decoder = Of("Hud.Interact.Decoder");
    public static readonly LocalizedString Siren = Of("Hud.Interact.Siren");

    public static readonly LocalizedString Scan = Of("Hud.Interact.Scan");
    public static readonly LocalizedString RopeBind = Of("Hud.Interact.RopeBind");
    public static readonly LocalizedString RopeJoin = Of("Hud.Interact.RopeJoin");
    public static readonly LocalizedString RopeResume = Of("Hud.Interact.RopeResume");
    public static readonly LocalizedString CatalogOpen = Of("Hud.Interact.CatalogOpen");

    public static readonly LocalizedString NpcUnrope = Of("Hud.Interact.NpcUnrope");
    public static readonly LocalizedString NpcUnropeMine = Of("Hud.Interact.NpcUnropeMine");
    public static readonly LocalizedString NpcRelease = Of("Hud.Interact.NpcRelease");
    public static readonly LocalizedString NpcHalt = Of("Hud.Interact.NpcHalt");
    public static readonly LocalizedString NpcEscortResume = Of("Hud.Interact.NpcEscortResume");
    public static readonly LocalizedString NpcJailRelease = Of("Hud.Interact.NpcJailRelease");

    public static readonly LocalizedString HandOverBody = Of("Hud.Interact.HandOverBody");
    public static readonly LocalizedString PutDownBody = Of("Hud.Interact.PutDownBody");
    public static readonly LocalizedString Revive = Of("Hud.Interact.Revive");
    public static readonly LocalizedString ReleaseAll = Of("Hud.Interact.ReleaseAll");

    public static readonly LocalizedString ReasonLocked = Of("Hud.Interact.Reason.Locked");
    public static readonly LocalizedString ReasonEscorting = Of("Hud.Interact.Reason.Escorting");
    public static readonly LocalizedString ReasonNoCustody = Of("Hud.Interact.Reason.NoCustody");
    public static readonly LocalizedString ReasonInUse = Of("Hud.Interact.Reason.InUse");

    private static LocalizedString Of(string key) => new LocalizedString(k_table, key);
}
