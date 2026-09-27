using System;
using System.Text;
using UnityEngine;

/// <summary>
/// 네트워크 호환성 식별자(버전) 단일 출처 — 세션 프로퍼티와 연결 승인 양쪽에서 버전이 다른 빌드의 합류를 막는다.
/// </summary>
public static class NetworkProtocol
{
    public const int k_protocolVersion = 2;

    public static string VersionString => $"{Application.version}#{k_protocolVersion}";

    public const string k_unknownVersion = "?";

    private const string k_mismatchReasonPrefix = "VER#";
    private const char k_payloadSeparator = '\n';

    /// <summary>NetworkConfig.ConnectionData / 승인 Payload에 실을 바이트 (#628, sha는 #622).</summary>
    public static byte[] EncodePayload() =>
        Encoding.UTF8.GetBytes(VersionString + k_payloadSeparator + BuildStamp.Sha);

    /// <summary>연결 페이로드를 PeerStamp(버전·sha)로 디코드한다.</summary>
    public static PeerStamp DecodePayload(byte[] payload)
    {
        if (payload == null || payload.Length == 0)
            return new PeerStamp(k_unknownVersion, BuildStamp.k_unknownSha);

        try
        {
            string decoded = Encoding.UTF8.GetString(payload);
            int separatorIndex = decoded.IndexOf(k_payloadSeparator);
            return separatorIndex < 0
                ? new PeerStamp(decoded, BuildStamp.k_unknownSha)
                : new PeerStamp(decoded[..separatorIndex], decoded[(separatorIndex + 1)..]);
        }
        catch (Exception)
        {
            return new PeerStamp(k_unknownVersion, BuildStamp.k_unknownSha);
        }
    }

    /// <summary>사람이 읽는 문장이 아니라 <see cref="TryParseMismatchReason"/>이 되돌려 파싱할 값이다.</summary>
    public static string BuildMismatchReason(string hostVersion) =>
        k_mismatchReasonPrefix + hostVersion;

    public static bool TryParseMismatchReason(string reason, out string hostVersion)
    {
        if (!string.IsNullOrEmpty(reason) && reason.StartsWith(k_mismatchReasonPrefix))
        {
            hostVersion = reason.Substring(k_mismatchReasonPrefix.Length);
            return true;
        }

        hostVersion = null;
        return false;
    }
}

public readonly struct PeerStamp
{
    public string Version { get; }
    public string Sha { get; }

    public PeerStamp(string version, string sha)
    {
        Version = version;
        Sha = sha;
    }
}

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
