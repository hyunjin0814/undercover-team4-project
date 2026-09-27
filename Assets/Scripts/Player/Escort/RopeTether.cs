using System;
using Unity.Netcode;

/// <summary>
/// 밧줄 연결 하나(대상 NPC와 끌기 여부) — PlayerEscorter의 NetworkList로 동기화된다.
/// </summary>
public struct RopeTether : INetworkSerializable, IEquatable<RopeTether>
{
    public ulong NpcId;

    public bool Dragging;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref NpcId);
        serializer.SerializeValue(ref Dragging);
    }

    /// <summary>모든 필드를 비교한다(NGO 변경 감지에 쓰이므로 필드를 빠뜨리지 말 것).</summary>
    public bool Equals(RopeTether other) => NpcId == other.NpcId && Dragging == other.Dragging;

    public override bool Equals(object obj) => obj is RopeTether other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(NpcId, Dragging);
}
