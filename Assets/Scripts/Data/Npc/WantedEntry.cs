using System;
using Unity.Collections;
using Unity.Netcode;

/// <summary>
/// 본부 수배 리스트의 한 항목 — 서버가 만들어 NetworkList로 동기화한다.
/// 몽타주는 외형 프로필 인덱스 + 공개 축으로 담고, 문장은 표시하는 피어가 조립한다.
/// </summary>
public struct WantedEntry : INetworkSerializable, IEquatable<WantedEntry>
{
    public ulong NpcId;

    public FixedString64Bytes Name;

    public AppearanceProfile Appearance;

    public RevealedAxisSet RevealedAxes;

    public int Bounty;

    public WantedCondition Condition;

    public bool Missing;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref NpcId);
        serializer.SerializeValue(ref Name);
        serializer.SerializeValue(ref Appearance);
        serializer.SerializeValue(ref RevealedAxes);
        serializer.SerializeValue(ref Bounty);
        serializer.SerializeValue(ref Condition);
        serializer.SerializeValue(ref Missing);
    }

    public bool Equals(WantedEntry other) => NpcId == other.NpcId;

    public override bool Equals(object obj) => obj is WantedEntry other && Equals(other);

    public override int GetHashCode() => NpcId.GetHashCode();
}
