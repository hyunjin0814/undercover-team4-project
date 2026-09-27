[LocalizedEnum("TitleTable", "Title.NicknameValidation.", nameof(ENicknameValidation.Ok))]
public enum ENicknameValidation
{
    Ok = 0,
    Empty = 1,
    Whitespace = 2,
    TooLong = 3,
}

/// <summary>
/// 닉네임 형식 규칙 검사와 표시용 이름 변환. 사유는 enum으로 돌려준다.
/// </summary>
public static class NicknameRules
{
    private const int k_maxLength = 12;

    private const string k_table = "TitleTable";
    private const string k_validationPrefix = "Title.NicknameValidation.";

    public static int MaxLength => k_maxLength;

    /// <summary>입력 규칙 검사 — 통과면 <see cref="ENicknameValidation.Ok"/>.</summary>
    public static ENicknameValidation Validate(string trimmed)
    {
        if (string.IsNullOrEmpty(trimmed))
            return ENicknameValidation.Empty;

        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c))
                return ENicknameValidation.Whitespace;
        }

        if (trimmed.Length > k_maxLength)
            return ENicknameValidation.TooLong;

        return ENicknameValidation.Ok;
    }

    /// <summary>검사 결과를 표시용 사유로. 통과면 빈 값. 길이 인자는 상수의 주인인 여기서 채운다.</summary>
    public static LocalizedMessage Describe(ENicknameValidation result) =>
        result switch
        {
            ENicknameValidation.Ok => LocalizedMessage.None,
            ENicknameValidation.TooLong => LocalizedMessage.Of(
                k_table,
                k_validationPrefix + result,
                k_maxLength
            ),
            _ => LocalizedMessage.Of(k_table, k_validationPrefix + result),
        };

    /// <summary>UGS가 PlayerName에 자동으로 붙이는 #1234 판별자를 떼어낸 표시용 이름.</summary>
    public static string StripDiscriminator(string playerName)
    {
        if (string.IsNullOrEmpty(playerName))
            return string.Empty;

        int hash = playerName.LastIndexOf('#');
        return hash >= 0 ? playerName.Substring(0, hash) : playerName;
    }
}
