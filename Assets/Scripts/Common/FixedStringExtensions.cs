using Unity.Collections;

/// <summary>
/// string을 FixedString으로 변환하는 공용 확장 — 용량을 넘으면 잘라 담고, null은 빈 문자열로 취급한다.
/// </summary>
public static class FixedStringExtensions
{
    public static FixedString64Bytes ToFixed64(this string value)
    {
        var result = new FixedString64Bytes();
        result.CopyFromTruncated(value ?? string.Empty);
        return result;
    }
}
