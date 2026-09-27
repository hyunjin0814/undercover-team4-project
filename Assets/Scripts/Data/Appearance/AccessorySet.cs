using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 한 사람의 치장 — 슬롯별 카탈로그 인덱스 묶음. 0은 "안 씀"이다.
/// </summary>
[Serializable]
public struct AccessorySet : INetworkSerializable, IEquatable<AccessorySet>
{
    public byte Headwear;
    public byte FacialHair;
    public byte Hair;
    public byte Eyewear;
    public byte Facewear;
    public byte Earwear;

    public byte this[EAccessorySlot slot]
    {
        get
        {
            switch (slot)
            {
                case EAccessorySlot.Headwear:
                    return Headwear;
                case EAccessorySlot.FacialHair:
                    return FacialHair;
                case EAccessorySlot.Hair:
                    return Hair;
                case EAccessorySlot.Eyewear:
                    return Eyewear;
                case EAccessorySlot.Facewear:
                    return Facewear;
                default:
                    return Earwear;
            }
        }
        set
        {
            switch (slot)
            {
                case EAccessorySlot.Headwear:
                    Headwear = value;
                    break;
                case EAccessorySlot.FacialHair:
                    FacialHair = value;
                    break;
                case EAccessorySlot.Hair:
                    Hair = value;
                    break;
                case EAccessorySlot.Eyewear:
                    Eyewear = value;
                    break;
                case EAccessorySlot.Facewear:
                    Facewear = value;
                    break;
                default:
                    Earwear = value;
                    break;
            }
        }
    }

    /// <summary>지금 내가 고른 치장 — 값의 출처는 <see cref="GameSettings"/> 하나다.</summary>
    public static AccessorySet FromSettings()
    {
        var set = new AccessorySet();
        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
            set[slot] = ToIndex(CosmeticLoadout.GetAccessory(slot));

        return set;
    }

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref Headwear);
        serializer.SerializeValue(ref FacialHair);
        serializer.SerializeValue(ref Hair);
        serializer.SerializeValue(ref Eyewear);
        serializer.SerializeValue(ref Facewear);
        serializer.SerializeValue(ref Earwear);
    }

    public bool Equals(AccessorySet other) =>
        Headwear == other.Headwear
        && FacialHair == other.FacialHair
        && Hair == other.Hair
        && Eyewear == other.Eyewear
        && Facewear == other.Facewear
        && Earwear == other.Earwear;

    public override bool Equals(object obj) => obj is AccessorySet other && Equals(other);

    public override int GetHashCode()
    {
        int hash = 17;
        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
            hash = (hash * 31) + this[slot];

        return hash;
    }

    private static byte ToIndex(int index) => (byte)Mathf.Clamp(index, 0, byte.MaxValue);
}
