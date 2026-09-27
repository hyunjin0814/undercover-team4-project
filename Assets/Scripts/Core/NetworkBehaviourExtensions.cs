using Unity.Netcode;

/// <summary>
/// <see cref="NetworkBehaviour"/> 공통 조회 헬퍼.
/// </summary>
public static class NetworkBehaviourExtensions
{
    /// <summary>서버 권위 경로를 실행해도 되는 피어(서버·호스트 또는 오프라인)인지 판정한다.</summary>
    public static bool HasServerAuthority(this NetworkBehaviour self) =>
        !self.IsSpawned || self.IsServer;
}
