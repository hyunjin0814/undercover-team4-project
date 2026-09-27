using Unity.Services.Core;
using UnityEngine;

[LocalizedEnum("TitleTable", "Title.AccountValidation.", nameof(EAccountValidation.Ok))]
public enum EAccountValidation
{
    Ok = 0,
    UsernameEmpty = 1,
    UsernameLength = 2,
    UsernameCharset = 3,
    PasswordEmpty = 4,
    PasswordLength = 5,
    PasswordCharset = 6,

    PasswordComposition = 7,
}

[LocalizedEnum("TitleTable", "Title.AccountError.")]
public enum EAccountError
{
    UsernameTaken = 0,
    AlreadyLinked = 1,
    InvalidFormat = 2,
    Unclassified = 3,
}

/// <summary>
/// 계정 아이디·비밀번호의 형식 규칙 검사와 오류 분류. 사유는 enum으로 돌려준다.
/// </summary>
public static class AccountCredentials
{
    private const int k_minUsernameLength = 3;
    private const int k_maxUsernameLength = 20;
    private const int k_minPasswordLength = 8;

    private const int k_maxPasswordLength = 30 - 1;

    private const string k_providerPasswordSuffix = "A";

    private const string k_table = "TitleTable";
    private const string k_validationPrefix = "Title.AccountValidation.";
    private const string k_errorPrefix = "Title.AccountError.";

    public static int MaxUsernameLength => k_maxUsernameLength;
    public static int MaxPasswordLength => k_maxPasswordLength;

    /// <summary>형식 검사 — 통과면 <see cref="EAccountValidation.Ok"/>.</summary>
    public static EAccountValidation Validate(string username, string password)
    {
        if (string.IsNullOrEmpty(username))
            return EAccountValidation.UsernameEmpty;

        if (username.Length < k_minUsernameLength || username.Length > k_maxUsernameLength)
            return EAccountValidation.UsernameLength;

        foreach (char c in username)
        {
            bool allowed =
                (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || c == '.'
                || c == '-'
                || c == '@'
                || c == '_';
            if (!allowed)
                return EAccountValidation.UsernameCharset;
        }

        if (string.IsNullOrEmpty(password))
            return EAccountValidation.PasswordEmpty;

        if (password.Length < k_minPasswordLength || password.Length > k_maxPasswordLength)
            return EAccountValidation.PasswordLength;

        bool hasLower = false;
        bool hasDigit = false;
        bool hasSymbol = false;
        foreach (char c in password)
        {
            if (c < '!' || c > '~')
                return EAccountValidation.PasswordCharset;

            if (c >= 'a' && c <= 'z')
                hasLower = true;
            else if (c >= '0' && c <= '9')
                hasDigit = true;
            else if (c < 'A' || c > 'Z')
                hasSymbol = true;
        }

        if (!hasLower || !hasDigit || !hasSymbol)
            return EAccountValidation.PasswordComposition;

        return EAccountValidation.Ok;
    }

    /// <summary>UGS로 보낼 비밀번호를 만든다 — 대문자가 없을 때만 접미어를 붙인다.</summary>
    public static string ToProviderPassword(string password)
    {
        string pw = password ?? string.Empty;
        return HasUpper(pw) ? pw : pw + k_providerPasswordSuffix;
    }

    private static bool HasUpper(string password)
    {
        foreach (char c in password)
        {
            if (c >= 'A' && c <= 'Z')
                return true;
        }

        return false;
    }

    /// <summary>검사 결과를 표시용 사유 메시지로 바꾼다. 통과면 빈 값.</summary>
    public static LocalizedMessage Describe(EAccountValidation result) =>
        result switch
        {
            EAccountValidation.Ok => LocalizedMessage.None,
            EAccountValidation.UsernameLength => Message(
                result,
                k_minUsernameLength,
                k_maxUsernameLength
            ),
            EAccountValidation.PasswordLength => Message(
                result,
                k_minPasswordLength,
                k_maxPasswordLength
            ),
            _ => Message(result),
        };

    private static LocalizedMessage Message(EAccountValidation result, params object[] args) =>
        LocalizedMessage.Of(k_table, k_validationPrefix + result, args);

    private const int k_errorInvalidFormat = 10002;
    private const int k_errorUsernameTaken = 10003;
    private const int k_errorAlreadyLinked = 10004;

    /// <summary>UGS 에러를 사용자에게 보일 분류로.</summary>
    public static EAccountError ClassifyError(RequestFailedException ex)
    {
        switch (ex.ErrorCode)
        {
            case k_errorUsernameTaken:
                return EAccountError.UsernameTaken;
            case k_errorAlreadyLinked:
                return EAccountError.AlreadyLinked;
            case k_errorInvalidFormat:
                return EAccountError.InvalidFormat;
        }

        Debug.LogWarning(
            $"[AccountCredentials] 미분류 인증 오류 code={ex.ErrorCode}: {ex.Message}"
        );
        return EAccountError.Unclassified;
    }

    /// <summary>오류 분류를 표시용 사유로.</summary>
    public static LocalizedMessage Describe(EAccountError error) =>
        LocalizedMessage.Of(k_table, k_errorPrefix + error);

    /// <summary>UGS 에러를 곧바로 표시용 사유로 — 분류를 따로 쓸 일이 없는 호출부용.</summary>
    public static LocalizedMessage DescribeError(RequestFailedException ex) =>
        Describe(ClassifyError(ex));
}
