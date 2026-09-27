using System;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Core.Environments;
using UnityEngine;

[LocalizedEnum("TitleTable", "Title.AccountLock.", nameof(EAccountLock.None))]
public enum EAccountLock
{
    None = 0,
    InSession = 1,
    Switching = 2,
}

[LocalizedEnum("TitleTable", "Title.AccountAction.")]
public enum EAccountAction
{
    Link = 0,
    Switch = 1,
}

/// <summary>
/// UGS 초기화·로그인·계정 연동 상태를 관리하는 단일 창구. App.Net.Auth로 접근한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class AuthBootstrap : CommonManagerBase
{
    [SerializeField]
    private bool m_signInOnStart = true;

    [SerializeField]
    private string m_environmentName = "production";

    [SerializeField]
    private string m_profile = string.Empty;

    public Func<bool> CanSignOut;

    public event Action OnSignedIn;
    public event Action OnSignedOut;
    public event Action OnNicknameChanged;

    public event Action OnSigningInChanged;

    private const string k_nicknamePrefKeyPrefix = "player.nickname.";

    private const string k_gatePassedPrefKeyPrefix = "auth.gatepassed.";

    private const string k_table = "TitleTable";

    private string m_accountUsername = string.Empty;

    private bool m_accountStateKnown;

    #region 상태 조회
    public bool IsSignedIn =>
        UnityServices.State == ServicesInitializationState.Initialized
        && AuthenticationService.Instance.IsSignedIn;

    public string PlayerId => IsSignedIn ? AuthenticationService.Instance.PlayerId : string.Empty;
    public string PlayerName =>
        IsSignedIn ? AuthenticationService.Instance.PlayerName : string.Empty;

    public string Nickname => NicknameRules.StripDiscriminator(PlayerName);

    public string Profile => m_profile;

    public bool HasPassedAuthGate { get; private set; }

    public bool RememberedAuthGate => PlayerPrefs.GetInt(GatePassedPrefKey, 0) == 1;

    public bool IsLinked => m_accountStateKnown && !string.IsNullOrEmpty(m_accountUsername);

    public string AccountUsername => m_accountUsername;

    public bool SessionTokenExists =>
        UnityServices.State == ServicesInitializationState.Initialized
        && AuthenticationService.Instance.SessionTokenExists;

    public bool IsSigningIn { get; private set; }

    public bool IsNetworkConnected
    {
        get
        {
            var nm = NetworkManager.Singleton;
            return nm != null && (nm.IsClient || nm.IsServer);
        }
    }

    private string NicknamePrefKey =>
        k_nicknamePrefKeyPrefix + (string.IsNullOrWhiteSpace(m_profile) ? "default" : m_profile);

    private string GatePassedPrefKey =>
        k_gatePassedPrefKeyPrefix + (string.IsNullOrWhiteSpace(m_profile) ? "default" : m_profile);
    #endregion

    /// <summary>로그인 관문 통과를 표시하고 PlayerPrefs에 남겨 다음 실행부터 건너뛰게 한다.</summary>
    public void MarkAuthGatePassed()
    {
        HasPassedAuthGate = true;
        PlayerPrefs.SetInt(GatePassedPrefKey, 1);
        PlayerPrefs.Save();
    }

    /// <summary>실행 간 관문 기억을 지운다 — 로그아웃·토큰 삭제와 한 쌍이다.</summary>
    private void ForgetAuthGate()
    {
        PlayerPrefs.DeleteKey(GatePassedPrefKey);
        PlayerPrefs.Save();
    }

    #region 초기화 · 익명 로그인
    private void Start()
    {
        if (m_signInOnStart)
        {
            SignInOnStartAsync().Forget();
        }
    }

    private async UniTaskVoid SignInOnStartAsync()
    {
        try
        {
            await InitializeAndSignInAsync(m_profile);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[AuthBootstrap] 시작 시 로그인 실패: {ex.Message}");
        }
    }

    public async UniTask InitializeAndSignInAsync(string profile = null)
    {
        bool wasSignedIn = IsSignedIn;
        SetSigningIn(true);
        try
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                var options = new InitializationOptions();
                if (!string.IsNullOrWhiteSpace(m_environmentName))
                {
                    options.SetEnvironmentName(m_environmentName);
                }
                if (!string.IsNullOrWhiteSpace(profile))
                {
                    options.SetProfile(profile);
                }

                await UnityServices.InitializeAsync(options);
                Debug.Log(
                    $"[AuthBootstrap] UnityServices 초기화 완료 / env: {m_environmentName}, profile: {m_profile}"
                );
            }

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                try
                {
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                    await AuthenticationService.Instance.GetPlayerNameAsync();
                    Debug.Log($"[AuthBootstrap] 익명 로그인 완료 / playerId: {PlayerId}");
                }
                catch (AuthenticationException ex)
                {
                    Debug.LogError($"[AuthBootstrap] 인증 실패: {ex.Message}");
                    throw;
                }
                catch (RequestFailedException ex)
                {
                    Debug.LogError($"[AuthBootstrap] 오류: {ex.Message}");
                    throw;
                }
            }

            if (!wasSignedIn && IsSignedIn)
            {
                await RefreshAccountStateAsync();
                await RestoreCachedNicknameAsync();
                CosmeticsSaveService.RestoreAsync().Forget();
            }
        }
        finally
        {
            SetSigningIn(false);
        }

        if (!wasSignedIn && IsSignedIn)
            OnSignedIn?.Invoke();
    }

    private void SetSigningIn(bool value)
    {
        if (IsSigningIn == value)
            return;

        IsSigningIn = value;
        OnSigningInChanged?.Invoke();
    }
    #endregion

    #region 닉네임 (#249)
    /// <summary>닉네임 변경 — 서버 반영에 성공했을 때만 로컬 캐시를 갱신한다.</summary>
    public async UniTask SetPlayerNameAsync(string name)
    {
        if (!IsSignedIn)
            return;

        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed == Nickname)
            return;

        ENicknameValidation nicknameResult = NicknameRules.Validate(trimmed);
        if (nicknameResult != ENicknameValidation.Ok)
            throw new LocalizedMessageException(NicknameRules.Describe(nicknameResult));

        await AuthenticationService.Instance.UpdatePlayerNameAsync(trimmed);

        PlayerPrefs.SetString(NicknamePrefKey, trimmed);
        PlayerPrefs.Save();

        OnNicknameChanged?.Invoke();
    }

    /// <summary>로그인 직후 로컬 캐시와 서버 닉네임을 맞춘다(캐시가 정본).</summary>
    private async UniTask RestoreCachedNicknameAsync()
    {
        if (IsLinked)
        {
            CacheNickname();
            return;
        }

        if (!m_accountStateKnown)
            return;

        string cached = PlayerPrefs.GetString(NicknamePrefKey, string.Empty);

        if (string.IsNullOrEmpty(cached))
        {
            CacheNickname();
            return;
        }

        if (cached == Nickname)
            return;

        ENicknameValidation cachedResult = NicknameRules.Validate(cached);
        if (cachedResult != ENicknameValidation.Ok)
        {
            Debug.LogWarning($"[AuthBootstrap] 캐시된 닉네임이 규칙 위반이라 폐기: {cachedResult}");
            PlayerPrefs.DeleteKey(NicknamePrefKey);
            PlayerPrefs.Save();
            return;
        }

        try
        {
            await AuthenticationService.Instance.UpdatePlayerNameAsync(cached);
            Debug.Log($"[AuthBootstrap] 캐시된 닉네임 복원: {cached}");
            OnNicknameChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[AuthBootstrap] 닉네임 복원 실패: {ex.Message}");
        }
    }

    /// <summary>서버 닉네임을 로컬 캐시에 씨딩 — 서버가 정본인 경로에서만 쓴다 (§4).</summary>
    private void CacheNickname()
    {
        if (string.IsNullOrEmpty(Nickname))
            return;

        PlayerPrefs.SetString(NicknamePrefKey, Nickname);
        PlayerPrefs.Save();
    }
    #endregion

    #region 계정 연동 (#384)
    /// <summary>익명 계정에 아이디·비밀번호를 연동한다. 이미 연동돼 있으면 새 익명 계정으로 갈아탄 뒤 연동한다.</summary>
    public async UniTask LinkAccountAsync(string username, string password)
    {
        if (!IsSignedIn)
            throw new LocalizedMessageException(Message("Title.Account.RequiresSignInToLink"));
        ThrowIfAccountLocked(EAccountAction.Link);

        string id = username?.Trim() ?? string.Empty;
        string pw = password ?? string.Empty;

        ThrowIfInvalid(AccountCredentials.Validate(id, pw));

        string abandoned = IsLinked ? m_accountUsername : string.Empty;
        if (IsLinked)
            await StartNewAnonymousAccountAsync();

        try
        {
            await AuthenticationService.Instance.AddUsernamePasswordAsync(
                id,
                AccountCredentials.ToProviderPassword(pw)
            );
        }
        catch (RequestFailedException ex) when (!string.IsNullOrEmpty(abandoned))
        {
            throw new LocalizedMessageException(
                Message(
                    "Title.Account.SubAccountAborted",
                    AccountCredentials.DescribeError(ex),
                    abandoned
                )
            );
        }

        m_accountUsername = id;
        m_accountStateKnown = true;
        Debug.Log($"[AuthBootstrap] 계정 연동 완료 / playerId: {PlayerId}");

        string cached = PlayerPrefs.GetString(NicknamePrefKey, string.Empty);
        if (
            !string.IsNullOrEmpty(cached)
            && cached != Nickname
            && NicknameRules.Validate(cached) == ENicknameValidation.Ok
        )
        {
            try
            {
                await AuthenticationService.Instance.UpdatePlayerNameAsync(cached);
                OnNicknameChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AuthBootstrap] 승격 후 닉네임 반영 실패: {ex.Message}");
            }
        }
    }

    /// <summary>익명 로그인에서 로그아웃한 뒤 아이디로 로그인한다.</summary>
    public async UniTask SignInWithAccountAsync(string username, string password)
    {
        ThrowIfAccountLocked(EAccountAction.Switch);

        string id = username?.Trim() ?? string.Empty;
        string pw = password ?? string.Empty;

        ThrowIfInvalid(AccountCredentials.Validate(id, pw));

        if (UnityServices.State != ServicesInitializationState.Initialized)
            await InitializeAndSignInAsync(m_profile);

        bool wasSignedIn = IsSignedIn;
        if (wasSignedIn)
            AuthenticationService.Instance.SignOut();

        try
        {
            await AuthenticationService.Instance.SignInWithUsernamePasswordAsync(
                id,
                AccountCredentials.ToProviderPassword(pw)
            );
            await AuthenticationService.Instance.GetPlayerNameAsync();
        }
        catch
        {
            if (wasSignedIn)
            {
                try
                {
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                    await AuthenticationService.Instance.GetPlayerNameAsync();
                }
                catch (Exception restoreEx)
                {
                    Debug.LogWarning($"[AuthBootstrap] 익명 복귀 실패: {restoreEx.Message}");
                }
            }
            throw;
        }

        m_accountUsername = id;
        m_accountStateKnown = true;

        CacheNickname();

        Debug.Log($"[AuthBootstrap] 계정 로그인 완료 / playerId: {PlayerId}");
        OnSignedIn?.Invoke();
        OnNicknameChanged?.Invoke();
    }

    /// <summary>서버에서 계정 연동 상태를 확인한다(로그인당 1회, 닉네임 복원보다 먼저).</summary>
    private async UniTask RefreshAccountStateAsync()
    {
        try
        {
            var info = await AuthenticationService.Instance.GetPlayerInfoAsync();
            m_accountUsername = info?.Username ?? string.Empty;
            m_accountStateKnown = true;
            Debug.Log($"[AuthBootstrap] 연동 상태: {(IsLinked ? m_accountUsername : "미연동")}");
        }
        catch (Exception ex)
        {
            m_accountUsername = string.Empty;
            m_accountStateKnown = false;
            Debug.LogWarning($"[AuthBootstrap] 연동 상태 조회 실패: {ex.Message}");
        }
    }
    #endregion

    #region 로그아웃 · 계정 전환
    /// <summary>계정 조작이 가능한지 확인한다. 불가하면 사유, 가능하면 null.</summary>
    private EAccountLock GetAccountLock()
    {
        if (IsNetworkConnected)
            return EAccountLock.InSession;

        if (CanSignOut != null && !CanSignOut())
            return EAccountLock.Switching;

        return EAccountLock.None;
    }

    /// <summary>계정 조작이 잠겨 있으면 로컬라이즈 키 사유로 예외를 던진다.</summary>
    private void ThrowIfAccountLocked(EAccountAction action)
    {
        EAccountLock lockReason = GetAccountLock();
        if (lockReason == EAccountLock.None)
            return;

        throw new LocalizedMessageException(
            Message(
                "Title.AccountLock." + lockReason,
                Message("Title.AccountAction." + action)
            )
        );
    }

    /// <summary>형식 위반이면 사유를 담아 던진다 — 연동·로그인이 같은 검사를 쓴다.</summary>
    private static void ThrowIfInvalid(EAccountValidation result)
    {
        if (result != EAccountValidation.Ok)
            throw new LocalizedMessageException(AccountCredentials.Describe(result));
    }

    private static LocalizedMessage Message(string key, params object[] args) =>
        LocalizedMessage.Of(k_table, key, args);

    /// <summary>이 기기에서 계정을 분리하고 새 익명 계정으로 다시 로그인한다.</summary>
    public async UniTask StartNewAnonymousAccountAsync()
    {
        ThrowIfAccountLocked(EAccountAction.Switch);
        if (UnityServices.State != ServicesInitializationState.Initialized)
            throw new LocalizedMessageException(Message("Title.Account.RequiresSignInToSwitch"));

        PlayerPrefs.DeleteKey(NicknamePrefKey);
        PlayerPrefs.Save();

        ClearSessionToken();

        await InitializeAndSignInAsync(m_profile);
        Debug.Log($"[AuthBootstrap] 새 익명 계정으로 전환 / playerId: {PlayerId}");
    }

    public void SignOut(bool clearCredentials = false)
    {
        EAccountLock signOutLock = GetAccountLock();
        if (signOutLock != EAccountLock.None)
        {
            Debug.LogWarning($"[AuthBootstrap] SignOut 거부 — {signOutLock}");
            return;
        }

        if (!IsSignedIn)
            return;

        AuthenticationService.Instance.SignOut(clearCredentials);

        m_accountUsername = string.Empty;
        m_accountStateKnown = false;

        HasPassedAuthGate = false;
        ForgetAuthGate();

        CosmeticsSaveService.OnSignedOut();
        OnSignedOut?.Invoke();
        Debug.Log("[AuthBootstrap] SignOut 완료");
    }

    public void ClearSessionToken()
    {
        EAccountLock clearLock = GetAccountLock();
        if (clearLock != EAccountLock.None)
        {
            Debug.LogWarning($"[AuthBootstrap] ClearSessionToken 거부 — {clearLock}");
            return;
        }

        if (UnityServices.State != ServicesInitializationState.Initialized)
            return;

        if (IsSignedIn)
        {
            m_accountUsername = string.Empty;
            m_accountStateKnown = false;

            AuthenticationService.Instance.SignOut();

            HasPassedAuthGate = false;
            ForgetAuthGate();

            OnSignedOut?.Invoke();
        }

        AuthenticationService.Instance.ClearSessionToken();
        Debug.Log("[AuthBootstrap] ClearSessionToken 완료");
    }
    #endregion
}
