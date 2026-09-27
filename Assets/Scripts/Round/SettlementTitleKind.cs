/// <summary>
/// 정산 화면 개인 칭호 종류. 선언 순서가 우선순위이며, 값 이름이 로컬라이즈 키(Settlement.Title.+이름)다.
/// </summary>
[LocalizedEnum("SettlementTable", "Settlement.Title.", nameof(SettlementTitleKind.None))]
public enum SettlementTitleKind
{
    None,
    TopArrester,
    TopOffender,
    TopInnocentKiller,
    TopDowns,
    TopRescuer,
}
