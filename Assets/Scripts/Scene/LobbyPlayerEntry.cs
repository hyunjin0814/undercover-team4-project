using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 로비 접속자 1명의 정보(닉네임·PlayerId·음소거) — 각 클라가 자기 값을 보고해 채운다.
/// </summary>
public struct LobbyPlayerEntry : INetworkSerializable, IEquatable<LobbyPlayerEntry>
{
    public ulong ClientId;
    public FixedString64Bytes Nickname;
    public FixedString64Bytes PlayerId;
    public bool MicMuted;

    public PlayerColorSet Colors;

    public AccessorySet Accessories;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref ClientId);
        serializer.SerializeValue(ref Nickname);
        serializer.SerializeValue(ref PlayerId);
        serializer.SerializeValue(ref MicMuted);
        Colors.NetworkSerialize(serializer);
        Accessories.NetworkSerialize(serializer);
    }

    public bool Equals(LobbyPlayerEntry other) =>
        ClientId == other.ClientId
        && Nickname == other.Nickname
        && PlayerId == other.PlayerId
        && MicMuted == other.MicMuted
        && Colors.Equals(other.Colors)
        && Accessories.Equals(other.Accessories);

    public override bool Equals(object obj) => obj is LobbyPlayerEntry other && Equals(other);

    public override int GetHashCode() => ClientId.GetHashCode();
}
