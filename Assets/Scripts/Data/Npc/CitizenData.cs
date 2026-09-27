using System;
using Unity.Collections;
using Unity.Netcode;

/// <summary>
/// CitizenIdentity의 신원 동기화 페이로드 — 스캔으로 공개 가능한 이름·타입·세력만 담는다.
/// 정답(IsCriminal)·검거 반응은 서버 전용이라 제외한다.
/// </summary>
public struct CitizenData : INetworkSerializable, IEquatable<CitizenData>
{
    public FixedString64Bytes Name;
    public FixedString64Bytes NameView;
    public OfficialRecords.CitizenType Type;
    public OfficialRecords.Faction Faction;
    public byte SymbolIndex;

    public bool IsAssigned => !Name.IsEmpty;

    /// <summary>서버 배정 프로필에서 공개 가능한 부분만 추려 담는다. (배정 측 전용)</summary>
    public static CitizenData FromProfile(CitizenProfile profile)
    {
        if (profile == null)
            return default;

        var name = profile.CitizenName.ToFixed64();

        var nameView = (profile.m_nameView ?? profile.CitizenName).ToFixed64();

        return new CitizenData
        {
            Name = name,
            NameView = nameView,
            Type = profile.CitizenType,
            Faction = profile.Faction,
            SymbolIndex = (byte)(profile.m_symbolIndexView < 0 ? 0 : profile.m_symbolIndexView),
        };
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref Name);
        serializer.SerializeValue(ref NameView);
        serializer.SerializeValue(ref Type);
        serializer.SerializeValue(ref Faction);
        serializer.SerializeValue(ref SymbolIndex);
    }

    public bool Equals(CitizenData other) =>
        Name.Equals(other.Name)
        && NameView.Equals(other.NameView)
        && Type == other.Type
        && Faction == other.Faction
        && SymbolIndex == other.SymbolIndex;

    public override bool Equals(object obj) => obj is CitizenData other && Equals(other);

    public override int GetHashCode() => Name.GetHashCode();
}
