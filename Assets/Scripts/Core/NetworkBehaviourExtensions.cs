using Unity.Netcode;

/// <summary>
/// <see cref="NetworkBehaviour"/> 공통 조회 헬퍼.
/// </summary>
public static class NetworkBehaviourExtensions
{
    /// <summary>
    /// 서버 권위 경로를 직접 실행해도 되는 피어인지 — 서버(호스트)이거나 오프라인.
    /// 스폰 전(오프라인)엔 <c>IsServer</c> 캐시가 아직 갱신되지 않아 false일 수 있으므로 스폰 여부를 함께 본다.
    ///
    /// 컴포넌트가 아니라 <b>확장 메서드</b>인 이유: 상태가 없는 한 줄짜리 판정이라 오브젝트에 붙일 것이
    /// 없고, <c>GetComponent</c> 의존이 없어 스폰 전 경로(<see cref="HealPack"/>·<see cref="ReviveKit"/>의
    /// 오프라인 사용)에서도 그대로 안전하다. 덕분에 <see cref="HealPack"/>은 능력 컴포넌트가 0개다.
    /// </summary>
    public static bool HasServerAuthority(this NetworkBehaviour self) =>
        !self.IsSpawned || self.IsServer;
}
