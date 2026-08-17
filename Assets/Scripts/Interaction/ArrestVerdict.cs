/// <summary>
/// 검거 판정 결과 — 본부로 인계된 NPC가 올바른 체포 대상이었는지. (GDD 7-2/7-3/9-1, #41)
/// 인계 NPC의 외형이 열려 있는 수배 조건에 부합하는지로 결정된다 (#669, 개체가 아니라 조건 대조).
///
/// 값 이름이 곧 판정 배너 문구의 키다 (<c>Hud.Verdict.</c> + 이름) — 값을 추가하면
/// <c>HudTable</c>에 같은 이름의 키를 함께 넣을 것. 아래 선언이 그 규약이고,
/// 에디터 메뉴 <i>Tools ▸ Localization ▸ 규약 키 검증</i>이 빠진 키를 잡아 준다.
/// </summary>
[LocalizedEnum("HudTable", "Hud.Verdict.")]
public enum ArrestVerdict
{
    /// <summary>현상수배범 — 실제 범인 검거. 보상 지급 대상 (GDD 9-1: 10,000원).</summary>
    WantedCriminal,

    /// <summary>
    /// 오검거 — 진범이 아닌 대상을 검거함. 보상 없음, 개인별 오검거 기록 대상 (GDD 7-3).
    /// 도주·저항한 무고 시민을 잡아도 이 판정이다 — 도주/저항은 진범을 헷갈리게 하는 미끼 행동일 뿐,
    /// 진범(WantedCriminal) 외의 검거는 순응·도주·저항을 가리지 않고 전부 오검거로 성립한다. (#78)
    /// </summary>
    WrongfulArrest,

    /// <summary>
    /// 경범죄 — 돌발 이벤트로 등장한 난동꾼(거리 난동자·나체 난동꾼 등)을 제압·연행해 즉결 처리함. (GDD 6-4, #106)
    /// 진범 수사 대상이 아니므로 라운드 할당량에는 포함되지 않고, 오검거도 아니다 — 별도 소액 수익을 준다.
    /// 대상은 <see cref="MisdemeanorOffender"/> 마커로 식별한다 (신원 IsCriminal 대조를 타지 않는다).
    /// </summary>
    Misdemeanor,
}
