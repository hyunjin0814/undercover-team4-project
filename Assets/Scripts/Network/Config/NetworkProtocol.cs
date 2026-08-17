using System;
using UnityEngine;

/// <summary>
/// 네트워크 호환성 식별자의 단일 출처 (#586) — 버전이 다른 빌드가 한 세션에 섞이는 것을 막는다.
/// 섞이면 NetworkVariable 역직렬화·RPC 시그니처·네트워크 프리팹 해시·씬 인덱스가 어긋나는데,
/// 참가 시점엔 조용히 지나가고 인게임에서야 "나만 이상함"으로 터진다.
/// </summary>
public static class NetworkProtocol
{
    /// <summary>
    /// 네트워크 호환성이 깨지는 변경마다 손으로 +1 — NetworkVariable 추가/삭제, RPC 시그니처 변경,
    /// DefaultNetworkPrefabs 목록 변경, 씬 추가/순서 변경.
    /// 마케팅 버전(bundleVersion)은 이런 변경에 따라 오르지 않으므로 별도로 둔다.
    /// </summary>
    // 1 → 2 (#669): WantedEntry.Name(FixedString64Bytes) 필드 제거 — 수배 항목 직렬화가 바뀌었다.
    public const int k_protocolVersion = 2;

    /// <summary>세션 프로퍼티로 심고 비교하는 값.</summary>
    public static string VersionString => $"{Application.version}#{k_protocolVersion}";
}

/// <summary>
/// 참가한 세션의 게임 버전이 로컬과 달라 참가를 물렸을 때 (#586).
/// 전용 타입인 이유는 UI가 이 실패만 다르게 말해야 하기 때문이다 — 사용자가 할 일이
/// "새 빌드를 받아라"로 정해져 있어 예외 메시지를 그대로 보여 줄 필요가 없다.
/// </summary>
public class SessionVersionMismatchException : Exception
{
    public string LocalVersion { get; }
    public string SessionVersion { get; }

    public SessionVersionMismatchException(string localVersion, string sessionVersion)
        : base($"게임 버전 불일치 — 내 버전 {localVersion} / 방 버전 {sessionVersion}")
    {
        LocalVersion = localVersion;
        SessionVersion = sessionVersion;
    }
}
