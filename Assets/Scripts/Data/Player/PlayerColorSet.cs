using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 한 사람의 로봇 색 — 부위별 팔레트 인덱스 3개를 한 묶음으로 동기화한다.
/// </summary>
[Serializable]
public struct PlayerColorSet : INetworkSerializable, IEquatable<PlayerColorSet>
{
    public byte Head;
    public byte Torso;
    public byte Legs;

    public byte this[EBodyPart part]
    {
        get
        {
            switch (part)
            {
                case EBodyPart.Head:
                    return Head;
                case EBodyPart.Torso:
                    return Torso;
                default:
                    return Legs;
            }
        }
        set
        {
            switch (part)
            {
                case EBodyPart.Head:
                    Head = value;
                    break;
                case EBodyPart.Torso:
                    Torso = value;
                    break;
                default:
                    Legs = value;
                    break;
            }
        }
    }

    /// <summary>지금 내가 고른 색 — 값의 출처는 <see cref="GameSettings"/> 하나다.</summary>
    public static PlayerColorSet FromSettings() =>
        new PlayerColorSet
        {
            Head = ToIndex(CosmeticLoadout.GetPlayerColor(EBodyPart.Head)),
            Torso = ToIndex(CosmeticLoadout.GetPlayerColor(EBodyPart.Torso)),
            Legs = ToIndex(CosmeticLoadout.GetPlayerColor(EBodyPart.Legs)),
        };

    public int Key => Head | (Torso << 8) | (Legs << 16);

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref Head);
        serializer.SerializeValue(ref Torso);
        serializer.SerializeValue(ref Legs);
    }

    public bool Equals(PlayerColorSet other) =>
        Head == other.Head && Torso == other.Torso && Legs == other.Legs;

    public override bool Equals(object obj) => obj is PlayerColorSet other && Equals(other);

    public override int GetHashCode() => Key;

    private static byte ToIndex(int index) => (byte)Mathf.Clamp(index, 0, byte.MaxValue);
}
