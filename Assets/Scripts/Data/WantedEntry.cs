using System;
using Unity.Netcode;

/// <summary>
/// 본부 수배 리스트(#58)의 한 항목 — 검거 조건(#669) 1건의 표시·매칭 데이터.
/// 서버가 만들어 NetworkList로 전 클라이언트에 동기화한다 (서버 권위, #52/#56).
///
/// 정답은 개체가 아니라 <b>조건</b>이다 (#669) — 이 항목의 조건에 맞는 외형이면 어느 NPC를 잡아도
/// 진범 판정이 난다. 그래서 개체를 가리키는 이름을 두지 않는다 — 이름을 실으면 그 한 개체만 정답인
/// 것처럼 읽혀 조건 부합의 뜻과 어긋난다. 위조범 대조(#320)는 스캔·인명부 쪽 별개 축이라 이 항목과 무관하다.
///
/// 몽타주는 <b>완성 문장이 아니라 원본 데이터</b>로 담는다 — 외형 프로필 인덱스 + 조건 축.
/// 문장은 표시하는 피어가 <see cref="AppearanceDatabase.BuildMontageText"/>로 조립하므로,
/// 호스트와 클라이언트의 언어 설정이 갈려도 각자 자기 언어로 본다 (#497).
/// 조건 축은 라운드마다 서버에서 랜덤 결정되므로 프로필만으로는 부족해 항목에 함께 싣는다 —
/// 항목 하나가 자족적이면 리스트 갱신과 조건 축 동기화의 도착 순서를 신경 쓸 일도 없다.
/// </summary>
public struct WantedEntry : INetworkSerializable, IEquatable<WantedEntry>
{
    // 조건의 기준이 된 NPC의 NetworkObjectId — 항목을 특정하는 고유 키다. 이 NPC 자신이 정답인 것은
    // 아니다(조건 부합이면 누구든 정답) — 검거 시 어느 항목을 닫을지, 그 개체가 탈옥했을 때 어느
    // 항목을 되살릴지를 가리키는 용도로만 쓴다 (ArrestJudge.ArrestResult.MatchedWantedId 참고).
    public ulong NpcId;

    // 몽타주 원본 — 축별 옵션 인덱스. 수배 조건 축만 담고 조건 아닌 축은 미배정(-1)이다
    // (AppearanceProfile.Masked). 서버 전용 원본 외형은 조건이 아닌 축이라도 실어 보내지 않는다.
    public AppearanceProfile Appearance;

    public RevealedAxisSet RevealedAxes; // 수배 조건이 걸린 축들 — 부합 판정·표시는 이 축들만 본다

    // 이 조건의 현상금 (#395) — 본부 수배 화면에 함께 띄운다. 원본은 서버 전용 값
    // (CitizenIdentity.Bounty)이라, 본부에 보여주려면 이렇게 항목에 실어 보내야 한다.
    public int Bounty;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref NpcId);
        serializer.SerializeValue(ref Appearance);
        serializer.SerializeValue(ref RevealedAxes);
        serializer.SerializeValue(ref Bounty);
    }

    public bool Equals(WantedEntry other) => NpcId == other.NpcId; // 제거 매칭 용도

    public override bool Equals(object obj) => obj is WantedEntry other && Equals(other);

    public override int GetHashCode() => NpcId.GetHashCode();
}
