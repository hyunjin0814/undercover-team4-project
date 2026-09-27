/// <summary>
/// 검거 판정 결과. 값 이름이 판정 배너 로컬라이즈 키(Hud.Verdict.+이름)다.
/// </summary>
[LocalizedEnum("HudTable", "Hud.Verdict.")]
public enum ArrestVerdict
{
    WantedCriminal,

    WrongfulArrest,

    Misdemeanor,

    ConditionUnmet,
}

public static class ArrestVerdictRules
{
    /// <summary>원장에 계상되는 판정인가 — 거짓이면 감옥에 들이지 않고 문 앞에 남긴다.</summary>
    public static bool IsCredited(this ArrestVerdict verdict) =>
        verdict == ArrestVerdict.WantedCriminal || verdict == ArrestVerdict.Misdemeanor;
}
