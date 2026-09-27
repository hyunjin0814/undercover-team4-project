/// <summary>
/// 씬 식별자. 실제 씬 이름 매핑은 AppHelper.ToSceneName — 빌드 인덱스에 결합하지 않는다.
/// </summary>
public enum EScene
{
    None,
    Title,
    Lobby,
    Shop,
    Game,
    Tutorial,
}

[LocalizedEnum("ItemTable", "Item.Name.", nameof(EInstallable.None))]
[LocalizedEnum("ItemTable", "Item.Description.", nameof(EInstallable.None))]
public enum EInstallable
{
    None,
    SignalDecoder,
    JailSirenButton,
}

public enum EShopSlotStatus
{
    Available,
    SoldOut,
    Owned,
}

[LocalizedEnum("LobbyTable", "Lobby.Voice.")]
public enum EVoiceState
{
    Idle,
    LoggingIn,
    Joining,
    Connected,
    Failed,
}

[LocalizedEnum("ItemTable", "Item.Feedback.")]
public enum EItemFeedback
{
    ScannerBlackout,
    AlreadyScanned,
    ScanFailedBlackout,
    ScanStoppedBlackout,
    ScanFailedOutOfRange,
    BatteryFull,
    ScannerBatteryFull,
}

[LocalizedEnum("ShopTable", "Shop.Reply.")]
public enum EShopReply
{
    AlreadyOwned,
    SoldOut,
    InsufficientFunds,
    OrderPlaced,
}

[LocalizedEnum("TitleTable", "Title.ConnectionLost.", nameof(EConnectionLostReason.None))]
public enum EConnectionLostReason
{
    None,
    NetworkDropped,
    SessionClosed,
}

public enum EEffect
{
    None,
    ImpactDust,
}

public enum EAudioClip
{
    None,
    BatonSwing,
    BatonHitMetal,
    BatonHitFlesh,
    BatonHitWorld,
    TaserHit,

    FootstepWalk,
    FootstepRun,
    JumpTakeoff,
    JumpLand,

    ScannerScan,
    UiSuccess,
    UiFail,

    TaserFire,
    RopeBind,

    JailSiren,

    UiClick,

    VehicleEngine,
    VehicleHorn,

    LightningStrike,
    RainLoop,

    NpcHurtHuman,

    BombCountdownSlow,
    BombCountdownFast,
    BombExplosion,

    ShopPurchase,

    ReviveLoop,

    JailAlarm,

    AreaScanHit,
    AreaScanMiss,

    NpcAttackSwing,
    NpcAttackHitRobot,

    GachaSpin,
    GachaReveal,

    HammerHit,
    HammerCrit,

    DroneApproach,
    CrateLand,
    CrateOpen,

    KillConfirm,
    KillFriendly,

    HomeRunHit,

    HealPackUse,

    HomeRunCharge,
}

public enum EBgm
{
    None,
    Title,
    Shop,
    Round,
}

public enum EFx
{
    None,
    BatonSwing,
    BatonHitMetal,
    BatonHitFlesh,
    BatonHitWorld,
    TaserHit,

    TaserFire,
    RopeBind,

    VehicleHorn,

    AreaScanHit,
    AreaScanMiss,

    HammerHit,
    HammerCrit,

    HomeRunHit,

    HealPackUse,
}

public enum EExecutionOrder
{
    Bootstrap = -400,
    BaseManagement = -300,
    UIManagement = -200,
    UIContent = -150,
    UIPanel = -100,
}

public enum EBodyPart
{
    Head,
    Torso,
    Legs,
}

public enum EAccessorySlot
{
    Headwear,
    FacialHair,
    Hair,
    Eyewear,
    Facewear,
    Earwear,
}

[System.Flags]
public enum EAccessorySlotMask
{
    None = 0,
    Headwear = 1 << EAccessorySlot.Headwear,
    FacialHair = 1 << EAccessorySlot.FacialHair,
    Hair = 1 << EAccessorySlot.Hair,
    Eyewear = 1 << EAccessorySlot.Eyewear,
    Facewear = 1 << EAccessorySlot.Facewear,
    Earwear = 1 << EAccessorySlot.Earwear,
}

public enum EWindowMode
{
    Windowed,
    Borderless,
    Fullscreen,
}
