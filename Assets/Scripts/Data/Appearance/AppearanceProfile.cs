using System;
using Unity.Netcode;

[LocalizedEnum("NpcTable", "Npc.Axis.")]
public enum AppearanceAxis
{
    HairStyle = 0,
    HairColor = 1,
    SkinColor = 2,
    FacialHair = 3,
    Headwear = 4,
    Eyewear = 5,
}

public struct RevealedAxisSet : INetworkSerializable, IEquatable<RevealedAxisSet>
{
    private byte m_mask;

    public bool IsEmpty => m_mask == 0;

    public int Count
    {
        get
        {
            int count = 0;
            for (int i = 0; i < AppearanceProfile.k_axisCount; i++)
            {
                if (Contains((AppearanceAxis)i))
                    count++;
            }
            return count;
        }
    }

    public bool Contains(AppearanceAxis axis) => (m_mask & Bit(axis)) != 0;

    public void Add(AppearanceAxis axis) => m_mask |= Bit(axis);

    public void Clear() => m_mask = 0;

    private static byte Bit(AppearanceAxis axis) => (byte)(1 << (int)axis);

    /// <summary>축 선언 순서로 순회한다 — foreach 전용(할당 없는 구조체 열거자).</summary>
    public Enumerator GetEnumerator() => new Enumerator(this);

    public struct Enumerator
    {
        private readonly RevealedAxisSet m_set;
        private int m_index;

        public Enumerator(RevealedAxisSet set)
        {
            m_set = set;
            m_index = -1;
            Current = default;
        }

        public AppearanceAxis Current { get; private set; }

        public bool MoveNext()
        {
            while (++m_index < AppearanceProfile.k_axisCount)
            {
                var axis = (AppearanceAxis)m_index;
                if (!m_set.Contains(axis))
                    continue;

                Current = axis;
                return true;
            }
            return false;
        }
    }

    public void NetworkSerialize<TBuffer>(BufferSerializer<TBuffer> serializer)
        where TBuffer : IReaderWriter => serializer.SerializeValue(ref m_mask);

    public bool Equals(RevealedAxisSet other) => m_mask == other.m_mask;

    public override bool Equals(object obj) => obj is RevealedAxisSet other && Equals(other);

    public override int GetHashCode() => m_mask;
}

/// <summary>
/// NPC 한 명의 외형 특징 조합 — 축별 옵션 인덱스만 담아 네트워크로 전송한다.
/// </summary>
[Serializable]
public struct AppearanceProfile : INetworkSerializable, IEquatable<AppearanceProfile>
{
    public const int k_axisCount = 6;

    public int HairStyleIndex;
    public int HairColorIndex;
    public int SkinColorIndex;
    public int FacialHairIndex;
    public int HeadwearIndex;
    public int EyewearIndex;

    public static AppearanceProfile Unassigned =>
        new AppearanceProfile
        {
            HairStyleIndex = -1,
            HairColorIndex = -1,
            SkinColorIndex = -1,
            FacialHairIndex = -1,
            HeadwearIndex = -1,
            EyewearIndex = -1,
        };

    public bool IsAssigned => HairColorIndex >= 0;

    public int GetIndex(AppearanceAxis axis) =>
        axis switch
        {
            AppearanceAxis.HairStyle => HairStyleIndex,
            AppearanceAxis.HairColor => HairColorIndex,
            AppearanceAxis.SkinColor => SkinColorIndex,
            AppearanceAxis.FacialHair => FacialHairIndex,
            AppearanceAxis.Headwear => HeadwearIndex,
            AppearanceAxis.Eyewear => EyewearIndex,
            _ => -1,
        };

    public void SetIndex(AppearanceAxis axis, int value)
    {
        switch (axis)
        {
            case AppearanceAxis.HairStyle:
                HairStyleIndex = value;
                break;
            case AppearanceAxis.HairColor:
                HairColorIndex = value;
                break;
            case AppearanceAxis.SkinColor:
                SkinColorIndex = value;
                break;
            case AppearanceAxis.FacialHair:
                FacialHairIndex = value;
                break;
            case AppearanceAxis.Headwear:
                HeadwearIndex = value;
                break;
            case AppearanceAxis.Eyewear:
                EyewearIndex = value;
                break;
        }
    }

    /// <summary>지정한 축만 남기고 나머지를 미배정(-1)으로 지운 사본을 돌려준다(네트워크 전송용).</summary>
    public AppearanceProfile Masked(RevealedAxisSet axes)
    {
        AppearanceProfile masked = Unassigned;
        foreach (AppearanceAxis axis in axes)
            masked.SetIndex(axis, GetIndex(axis));
        return masked;
    }

    public bool MatchesOn(in AppearanceProfile other, RevealedAxisSet axes)
    {
        foreach (AppearanceAxis axis in axes)
        {
            if (GetIndex(axis) != other.GetIndex(axis))
                return false;
        }
        return true;
    }

    public void NetworkSerialize<TBuffer>(BufferSerializer<TBuffer> serializer)
        where TBuffer : IReaderWriter
    {
        serializer.SerializeValue(ref HairStyleIndex);
        serializer.SerializeValue(ref HairColorIndex);
        serializer.SerializeValue(ref SkinColorIndex);
        serializer.SerializeValue(ref FacialHairIndex);
        serializer.SerializeValue(ref HeadwearIndex);
        serializer.SerializeValue(ref EyewearIndex);
    }

    public bool Equals(AppearanceProfile other) =>
        HairStyleIndex == other.HairStyleIndex
        && HairColorIndex == other.HairColorIndex
        && SkinColorIndex == other.SkinColorIndex
        && FacialHairIndex == other.FacialHairIndex
        && HeadwearIndex == other.HeadwearIndex
        && EyewearIndex == other.EyewearIndex;

    public override bool Equals(object obj) => obj is AppearanceProfile other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(
            HairStyleIndex,
            HairColorIndex,
            SkinColorIndex,
            FacialHairIndex,
            HeadwearIndex,
            EyewearIndex
        );
}
