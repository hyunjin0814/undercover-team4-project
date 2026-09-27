using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Multiplayer;
using UnityEngine;

/// <summary>
/// UGS 멀티플레이 세션 생성·참가·이탈과 연결 끊김 감지를 담당하는 상주 매니저.
/// 세션 프로퍼티로 게임 버전을 비교하고, 연결 승인 게이트를 소유한다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class SessionManager : CommonManagerBase
{
    private const string k_versionProperty = "ver";

    private const string k_shaProperty = "sha";

    [SerializeField]
    private int m_maxPlayer = 6;

    [SerializeField]
    private AuthBootstrap m_auth;
    public AuthBootstrap Auth => m_auth;

    private ISession m_session;
    public ISession CurrentSession => m_session;

    private readonly ConnectionApprovalGate m_approvalGate = new();
    public ConnectionApprovalGate Approval => m_approvalGate;

    public event Action<string> OnSessionJoined;
    public event Action OnSessionLeft;
    public event Action<EConnectionLostReason> OnConnectionLost;
    private bool m_isLeaving;

    public SessionVersionMismatchException PendingVersionMismatch { get; private set; }

    /// <summary>안내를 띄우면서 비운다 — 다음에 타이틀에 올 때 지난 실패가 다시 뜨지 않게.</summary>
    public SessionVersionMismatchException TakePendingVersionMismatch()
    {
        SessionVersionMismatchException pending = PendingVersionMismatch;
        PendingVersionMismatch = null;
        return pending;
    }

    private void OnEnable()
    {
        if (m_auth != null)
            m_auth.CanSignOut = () => m_session == null && !m_isBusy;
    }

    private void OnDisable()
    {
        if (m_auth != null)
            m_auth.CanSignOut = null;
    }

    public async UniTask EnsureSignedInAsync()
    {
        if (m_auth == null)
        {
            Debug.LogError($"[SessionManager] AuthBootstrap 참조가 없습니다.");
            throw new InvalidOperationException("AuthBootstrap not assigned");
        }

        await m_auth.InitializeAndSignInAsync();
    }

    /// <summary>인스펙터의 최대 인원(m_maxPlayer)으로 세션을 생성한다 — 세션 관문 UI용.</summary>
    public UniTask<string> CreateSessionAsync() => CreateSessionAsync(m_maxPlayer);

    public async UniTask<string> CreateSessionAsync(int maxPlayer)
    {
        await EnsureSignedInAsync();
        PrepareApprovalGate(NetworkManager.Singleton);
        var options = new SessionOptions
        {
            MaxPlayers = maxPlayer,
            Type = "Session",
            SessionProperties = new Dictionary<string, SessionProperty>
            {
                [k_versionProperty] = new SessionProperty(
                    NetworkProtocol.VersionString,
                    VisibilityPropertyOptions.Public
                ),
                [k_shaProperty] = new SessionProperty(
                    BuildStamp.Sha,
                    VisibilityPropertyOptions.Public
                ),
            },
        }.WithRelayNetwork();
        ISession session = await MultiplayerService.Instance.CreateSessionAsync(options);
        AdoptSession(session);
        Debug.Log(
            $"[SessionManager] 세션과 호스트 만들어짐 / Id: {session.Id}, Code = {session.Code}, 버전: {NetworkProtocol.VersionString}, sha: {BuildStamp.Sha}"
        );

        return session.Code;
    }

    public async UniTask JoinByCodeAsync(string code)
    {
        await EnsureSignedInAsync();
        NetworkManager nm = NetworkManager.Singleton;
        PrepareApprovalGate(nm);

        string rejectReason = null;
        void CaptureReason(ulong _) => rejectReason = nm?.DisconnectReason;
        if (nm != null)
            nm.OnClientDisconnectCallback += CaptureReason;

        ISession session;
        try
        {
            session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code);
        }
        catch (Exception) when (NetworkProtocol.TryParseMismatchReason(rejectReason, out string hostVersion))
        {
            Debug.LogWarning(
                $"[SessionManager] 승인 단계 버전 불일치로 거부됨(B층) / 내 버전: {NetworkProtocol.VersionString}, 호스트 버전: {hostVersion}"
            );
            var mismatch = new SessionVersionMismatchException(NetworkProtocol.VersionString, hostVersion);
            PendingVersionMismatch = mismatch;
            throw mismatch;
        }
        finally
        {
            if (nm != null)
                nm.OnClientDisconnectCallback -= CaptureReason;
        }

        string sessionVersion = ReadVersion(session);
        if (sessionVersion != NetworkProtocol.VersionString)
        {
            Debug.LogWarning(
                $"[SessionManager] 버전 불일치로 참가 취소 / 내 버전: {NetworkProtocol.VersionString}, 방 버전: {sessionVersion}"
            );
            var mismatch = new SessionVersionMismatchException(
                NetworkProtocol.VersionString,
                sessionVersion
            );
            PendingVersionMismatch = mismatch;
            Abandon(session);
            throw mismatch;
        }

        AdoptSession(session);
        Debug.Log($"[SessionManager] 세션 참가 완료 / Id: {session.Id}, Code: {session.Code}");

        string sessionSha = ReadSha(session);
        if (sessionSha != BuildStamp.Sha)
        {
            Debug.LogWarning(
                $"[SessionManager] 버전은 같은데 커밋이 다름(참가 유지) / 버전: {NetworkProtocol.VersionString}, 내 sha: {BuildStamp.Sha}, 방 sha: {sessionSha}"
            );
        }
        else
        {
            Debug.Log($"[SessionManager] 커밋 일치 / sha: {BuildStamp.Sha}");
        }
    }

    private void PrepareApprovalGate(NetworkManager nm)
    {
        if (nm == null)
        {
            Debug.LogError("[SessionManager] NetworkManager.Singleton이 없어 버전 게이트를 걸 수 없습니다.");
            return;
        }

        ConnectionApprovalGate.StampLocalPayload(nm);
        m_approvalGate.Install(nm);
    }

    private static string ReadVersion(ISession session)
    {
        if (
            session.Properties != null
            && session.Properties.TryGetValue(k_versionProperty, out SessionProperty property)
            && !string.IsNullOrEmpty(property.Value)
        )
        {
            return property.Value;
        }

        return NetworkProtocol.k_unknownVersion;
    }

    private static string ReadSha(ISession session)
    {
        if (
            session.Properties != null
            && session.Properties.TryGetValue(k_shaProperty, out SessionProperty property)
            && !string.IsNullOrEmpty(property.Value)
        )
        {
            return property.Value;
        }

        return BuildStamp.k_unknownSha;
    }

    /// <summary>버전이 다른 세션에서 NGO를 먼저 끊고 즉시 물러난다.</summary>
    private static void Abandon(ISession session)
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.Shutdown();

        LeaveQuietlyAsync(session).Forget();
        ReturnToTitleAsync().Forget();
    }

    /// <summary>버전 불일치로 물러난 뒤 NGO가 내려가면 타이틀로 복귀한다.</summary>
    private static async UniTaskVoid ReturnToTitleAsync()
    {
        await SessionFlow.WaitForNetworkShutdownAsync();
        if (App.CurrentScene != EScene.Title)
            App.LoadScene(EScene.Title);
    }

    /// <summary>버전 불일치로 물러난 세션의 뒷정리 — 실패해도 사용자에게 전할 말은 버전 불일치다.</summary>
    private static async UniTaskVoid LeaveQuietlyAsync(ISession session)
    {
        try
        {
            await session.LeaveAsync();
            Debug.Log("[SessionManager] 버전 불일치 세션에서 나감");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SessionManager] 버전 불일치 세션 나가기 실패(무시): {ex.Message}");
        }
    }

    public async UniTask LeaveAsync()
    {
        if (m_session == null)
        {
            Debug.Log($"[SessionManager] 나갈 세션 없음");
            return;
        }

        ISession leaving = m_session;
        m_isLeaving = true;

        try
        {
            await leaving.LeaveAsync();
        }
        finally
        {
            UnsubscribeSessionEvents(leaving);
            UnsubscribeNetworkEvents();
            if (ReferenceEquals(m_session, leaving))
                m_session = null;

            m_isLeaving = false;
            OnSessionLeft?.Invoke();
        }
    }

    private void AdoptSession(ISession session)
    {
        if (m_session != null && !ReferenceEquals(m_session, session))
        {
            UnsubscribeSessionEvents(m_session);
        }

        m_session = session;
        SubscribeSessionEvents(m_session);
        UnsubscribeNetworkEvents();
        SubscribeNetworkEvents();

        OnSessionJoined?.Invoke(session.Id);
    }

    private void SubscribeSessionEvents(ISession session)
    {
        if (session == null)
            return;

        session.PlayerJoined += OnPlayerJoined;
        session.Changed += OnSessionChanged;
        session.SessionPropertiesChanged += OnSessionPropertiesChanged;
        session.Deleted += OnSessionDeleted;
    }

    private void UnsubscribeSessionEvents(ISession session)
    {
        if (session == null)
            return;

        session.PlayerJoined -= OnPlayerJoined;
        session.Changed -= OnSessionChanged;
        session.SessionPropertiesChanged -= OnSessionPropertiesChanged;
        session.Deleted -= OnSessionDeleted;
    }

    private void SubscribeNetworkEvents()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null)
            nm.OnClientDisconnectCallback += OnClientDisconnected;
    }

    private void UnsubscribeNetworkEvents()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null)
            nm.OnClientDisconnectCallback -= OnClientDisconnected;
    }

    private void OnClientDisconnected(ulong clientId)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null)
            return;
        if (nm.IsServer && clientId != nm.LocalClientId)
        {
            Debug.Log($"[SessionManager] 원격 클라 끊김: {clientId} (무시)");
            return;
        }

        HandleConnectionLost(EConnectionLostReason.NetworkDropped);
    }

    private void OnSessionDeleted() => HandleConnectionLost(EConnectionLostReason.SessionClosed);

    private void HandleConnectionLost(EConnectionLostReason reason)
    {
        if (m_isLeaving)
            return;
        if (m_session == null)
            return;

        Debug.Log($"[SessionManager] 연결 끊김 정규화: {reason}");
        ISession lost = m_session;
        UnsubscribeSessionEvents(lost);
        UnsubscribeNetworkEvents();
        m_session = null;

        OnConnectionLost?.Invoke(reason);

        TeardownLostSessionAsync(lost).Forget();
    }

    private static async UniTaskVoid TeardownLostSessionAsync(ISession lost)
    {
        try
        {
            await lost.LeaveAsync();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SessionManager] 끊긴 세션 SDK 정리 실패(무시): {ex.Message}");
        }
    }

    private void OnPlayerJoined(string playerId)
    {
        Debug.Log($"[SessionManager] 플레이어 참가: {playerId}");
    }

    private void OnSessionChanged()
    {
        Debug.Log($"[SessionManager] OnSessionChanged()");
    }

    private void OnSessionPropertiesChanged()
    {
        Debug.Log($"[SessionManager] OnSessionPropertiesChanged()");
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();

        if (m_session != null)
        {
            UnsubscribeSessionEvents(m_session);
            UnsubscribeNetworkEvents();
        }
    }

    [Tooltip(
        "OnGUI 디버그 패널 표시 — 테스트 씬 수동 세션 조작용. 정식 UI는 SessionPanel·SessionCodePanel (#247)"
    )]
    [SerializeField]
    private bool m_showDebugGui;

    [SerializeField]
    private float m_guiTopOffset = 10f;

    private string m_joinCodeInput = string.Empty;
    private bool m_isBusy;
    private string m_status = "대기 중 - 세션을 만들거나 코드로 참가하세요.";

    private void OnGUI()
    {
        if (!m_showDebugGui)
            return;

        GUILayout.BeginArea(new Rect(10, m_guiTopOffset, 380, 300));

        if (m_session == null)
        {
            DrawLobbyUI();
        }
        else
        {
            DrawInSessionUI();
        }

        GUILayout.Space(8);
        GUILayout.Label(m_status);

        GUILayout.EndArea();
    }

    private void DrawLobbyUI()
    {
        GUILayout.Label("세션 — 만들거나, 코드로 참가");

        GUI.enabled = !m_isBusy;

        if (GUILayout.Button("세션 만들기 (Create)"))
        {
            HandleCreateAsync().Forget();
        }

        GUILayout.Space(6);
        GUILayout.Label("Join 코드:");
        m_joinCodeInput = GUILayout.TextField(m_joinCodeInput ?? string.Empty);

        if (GUILayout.Button("코드로 참가 (Join)"))
        {
            HandleJoinAsync(m_joinCodeInput).Forget();
        }

        GUI.enabled = true;
    }

    private void DrawInSessionUI()
    {
        GUILayout.Label($"세션 Id: {m_session.Id}");

        GUILayout.Label("이 코드를 공유하세요:");
        GUILayout.TextField(m_session.Code ?? string.Empty);

        GUILayout.Space(8);
        GUI.enabled = !m_isBusy;
        if (GUILayout.Button("세션 나가기 (Leave)"))
        {
            HandleLeaveAsync().Forget();
        }
        GUI.enabled = true;
    }

    private async UniTaskVoid HandleCreateAsync()
    {
        if (m_isBusy)
            return;
        m_isBusy = true;
        m_status = "세션 생성 중...";
        try
        {
            string code = await CreateSessionAsync(m_maxPlayer);
            m_status = $"세션 생성됨. 공유 코드: {code}";
        }
        catch (Exception e)
        {
            m_status = $"세션 생성 실패: {e.Message}";
            Debug.LogError($"[SessionManager] 세션 생성 실패: {e}");
        }
        finally
        {
            m_isBusy = false;
        }
    }

    private async UniTaskVoid HandleJoinAsync(string code)
    {
        if (m_isBusy)
            return;
        m_isBusy = true;
        m_status = "세션 참가 중...";
        try
        {
            await JoinByCodeAsync(code);
            m_status = "세션에 참가했습니다.";
        }
        catch (Exception e)
        {
            m_status = $"세션 참가 실패: {e.Message}";
            Debug.LogError($"[SessionManager] 세션 참가 실패: {e}");
        }
        finally
        {
            m_isBusy = false;
        }
    }

    private async UniTaskVoid HandleLeaveAsync()
    {
        if (m_isBusy)
            return;
        m_isBusy = true;
        m_status = "세션 나가는 중...";
        try
        {
            await LeaveAsync();
            m_status = "세션에서 나갔습니다.";
        }
        catch (Exception e)
        {
            m_status = $"세션 나가기 실패: {e.Message}";
            Debug.LogError($"[SessionManager] 세션 나가기 실패: {e}");
        }
        finally
        {
            m_isBusy = false;
        }
    }

    /// <summary>끊긴 플레이어를 UGS 세션 명부에서 제거한다. 호스트 전용.</summary>
    public async UniTask RemovePlayerAsync(string playerId)
    {
        if (m_session == null || string.IsNullOrEmpty(playerId))
            return;

        try
        {
            await m_session.AsHost().RemovePlayerAsync(playerId);
            Debug.Log($"[SessionManager] 끊긴 플레이어를 세션에서 내림 / playerId: {playerId}");
        }
        catch (Exception ex)
        {
            Debug.Log($"[SessionManager] 세션 플레이어 제거 안 함(이미 없거나 실패): {ex.Message}");
        }
    }

    /// <summary>세션 잠금/해제 — 호스트 전용. 잠그면 코드 참가가 거부된다. (#214 게임 중 신규 접속 차단)</summary>
    public async UniTask SetLockedAsync(bool locked)
    {
        if (m_session == null)
            return;
        try
        {
            IHostSession host = m_session.AsHost();
            if (host.IsLocked == locked)
                return;
            host.IsLocked = locked;
            await host.SavePropertiesAsync();
            Debug.Log($"[SessionManager] 세션 {(locked ? "잠금(참가 차단)" : "해제(참가 허용)")}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SessionManager] 세션 잠금 변경 실패(무시): {ex.Message}");
        }
    }
}
