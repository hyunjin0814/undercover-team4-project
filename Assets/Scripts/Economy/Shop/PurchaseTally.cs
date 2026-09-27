using System;
using Unity.Netcode;

/// <summary>
/// 구매 집계 한 줄 — ShopCatalog 인덱스별 세션 누적 구매 수와 남은 수.
/// </summary>
public struct PurchaseTally : INetworkSerializable, IEquatable<PurchaseTally>
{
    public ushort CatalogIndex;

    public ushort Bought;

    public ushort Remaining;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer)
        where T : IReaderWriter
    {
        serializer.SerializeValue(ref CatalogIndex);
        serializer.SerializeValue(ref Bought);
        serializer.SerializeValue(ref Remaining);
    }

    public bool Equals(PurchaseTally other) =>
        CatalogIndex == other.CatalogIndex && Bought == other.Bought && Remaining == other.Remaining;

    public override bool Equals(object obj) => obj is PurchaseTally other && Equals(other);

    public override int GetHashCode() => (CatalogIndex << 16) ^ (Bought << 8) ^ Remaining;
}
