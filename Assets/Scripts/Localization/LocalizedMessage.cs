using System;

/// <summary>
/// 표시 문구를 완성 문장 대신 테이블 키 + 인자로 나르는 값.
/// 표시하는 쪽이 원할 때 현재 언어로 읽으며, 인자로 중첩 LocalizedMessage를 넣을 수 있다.
/// </summary>
public readonly struct LocalizedMessage
{
    private readonly string m_table;
    private readonly string m_key;
    private readonly object[] m_args;
    private readonly string m_literal;

    private LocalizedMessage(string table, string key, object[] args, string literal)
    {
        m_table = table;
        m_key = key;
        m_args = args;
        m_literal = literal;
    }

    public static LocalizedMessage None => default;

    public static LocalizedMessage Of(string table, string key, params object[] args) =>
        new LocalizedMessage(table, key, args, null);

    /// <summary>번역하지 않고 그대로 띄울 원문 — 우리 테이블에 없는 외부 문구(UGS SDK 메시지 등).</summary>
    public static LocalizedMessage Literal(string text) =>
        new LocalizedMessage(null, null, null, text);

    public bool IsEmpty => string.IsNullOrEmpty(m_key) && string.IsNullOrEmpty(m_literal);

    public string KeyPath =>
        m_literal ?? (string.IsNullOrEmpty(m_key) ? "(없음)" : m_table + "/" + m_key);

    /// <summary>지금 언어로 읽는다. 비어 있으면 빈 문자열.</summary>
    public string Resolve()
    {
        if (!string.IsNullOrEmpty(m_literal))
            return m_literal;

        if (string.IsNullOrEmpty(m_key))
            return string.Empty;

        return LocalizedStrings.Get(m_table, m_key, ResolveArgs());
    }

    private object[] ResolveArgs()
    {
        if (m_args == null || m_args.Length == 0)
            return Array.Empty<object>();

        var resolved = new object[m_args.Length];
        for (int i = 0; i < m_args.Length; i++)
            resolved[i] = m_args[i] is LocalizedMessage nested ? nested.Resolve() : m_args[i];
        return resolved;
    }
}

public class LocalizedMessageException : Exception
{
    public LocalizedMessageException(LocalizedMessage reason)
        : base(reason.KeyPath) => Reason = reason;

    public LocalizedMessage Reason { get; }
}
