using System;
using Unity.Collections;
using Unity.Netcode;

/// <summary>
/// 본부 인명부 항목 — 스폰된 시민 1명의 정본 신원. 서버가 채워 전 클라에 동기화한다.
/// </summary>
public struct DirectoryEntry : INetworkSerializable, IEquatable<DirectoryEntry>
{
    public FixedString64Bytes Name;
    public OfficialRecords.CitizenType Type;
    public OfficialRecords.Faction Faction;

    public static DirectoryEntry FromProfile(CitizenProfile profile)
    {
        return new DirectoryEntry
        {
            Name = (profile != null ? profile.CitizenName : null).ToFixed64(),
            Type = profile != null ? profile.CitizenType : default,
            Faction = profile != null ? profile.Faction : default,
        };
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref Name);
        serializer.SerializeValue(ref Type);
        serializer.SerializeValue(ref Faction);
    }

    public bool Equals(DirectoryEntry other) =>
        Name.Equals(other.Name) && Type == other.Type && Faction == other.Faction;

    public override bool Equals(object obj) => obj is DirectoryEntry other && Equals(other);

    public override int GetHashCode() => Name.GetHashCode();
}
