/// <summary>
/// 플레이어 상태 enum — GDD 10-2 기준. (#105, #364)
/// GDD 원문은 Die만 두었으나 HP 0은 '사망'이 아니라 '다운(구조 가능)'이라 Down을 먼저 도입했고(GDD 7-5),
/// 다운을 제한시간 안에 구조받지 못하면 넘어가는 Die가 뒤이어 생겼다(#364) — 둘은 다른 상태다.
/// Die도 로봇이므로 파괴는 아니다: 현장 구조만 막히고 본부 이송 부활(#365)로 복구된다.
/// (애니메이터 번호 매핑이 생기면 값 순서 = Animator 번호이므로 그때 순서 확정)
/// </summary>
public enum PlayerState
{
    Idle,
    Walk,
    Run,
    Attack,
    Down,
    Die,
    Dance,
}
