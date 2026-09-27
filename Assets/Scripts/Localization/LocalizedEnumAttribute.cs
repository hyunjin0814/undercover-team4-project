using System;

/// <summary>
/// enum 값 이름이 문자열 테이블 키(접두 + 값 이름)의 일부라는 선언.
/// LocalizedEnumValidator가 이 선언을 근거로 모든 값의 키 존재를 검사한다.
/// </summary>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = true)]
public sealed class LocalizedEnumAttribute : Attribute
{
    public LocalizedEnumAttribute(string table, string keyPrefix, params string[] except)
    {
        Table = table;
        KeyPrefix = keyPrefix;
        Except = except ?? Array.Empty<string>();
    }

    public string Table { get; }

    public string KeyPrefix { get; }

    public string[] Except { get; }
}
