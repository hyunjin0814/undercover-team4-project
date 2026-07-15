using System;
using UnityEngine;
using Cysharp.Threading.Tasks;
using Unity.Services.Multiplayer;

public class SessionManager : MonoBehaviour
{
    [SerializeField] private int m_maxPlayer = 6;
    [SerializeField] private AuthBootstrap m_auth; // 인스펙터로 연결
    public AuthBootstrap Auth => m_auth;

    private ISession m_session;
    public ISession CurrentSession => m_session;

    public event Action<string> OnSessionJoined; // 인자: session.Id
    public event Action OnSessionLeft;

    private void OnEnable()
    {
        if (m_auth != null) m_auth.CanSignOut = () => m_session == null && !m_isBusy;   // 세션에 접속 중이 아니면 로그아웃 가능
    }

    private void OnDisable()
    {
        if (m_auth != null) m_auth.CanSignOut = null;
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

    public async UniTask<string> CreateSessionAsync(int maxPlayer)
    {
        await EnsureSignedInAsync();
        var options = new SessionOptions { MaxPlayers = maxPlayer, Type = "Session" }.WithRelayNetwork();
        ISession session = await MultiplayerService.Instance.CreateSessionAsync(options);
        AdoptSession(session);
        Debug.Log($"[SessionManager] 세션과 호스트 만들어짐 / Id: {session.Id}, Code = {session.Code}");

        return session.Code;
    }

    public async UniTask JoinByCodeAsync(string code)
    {
        await EnsureSignedInAsync();
        ISession session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code);
        AdoptSession(session);
        Debug.Log($"[SessionManager] 세션 참가 완료 / Id: {session.Id}, Code: {session.Code}");
    }

    public async UniTask LeaveAsync()
    {
        if (m_session == null)
        {
            Debug.Log($"[SessionManager] 나갈 세션 없음");
            return;
        }

        ISession leaving = m_session;
        try
        {
            await leaving.LeaveAsync();
        }
        finally
        {
            UnsubscribeSessionEvents(leaving);
            if (ReferenceEquals(m_session, leaving))
                m_session = null;

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

        OnSessionJoined?.Invoke(session.Id);
    }

    private void SubscribeSessionEvents(ISession session)
    {
        if (session == null) return;

        session.PlayerJoined += OnPlayerJoined;
        session.Changed += OnSessionChanged;
        session.SessionPropertiesChanged += OnSessionPropertiesChanged;
    }

    private void UnsubscribeSessionEvents(ISession session)
    {
        if (session == null) return;

        session.PlayerJoined -= OnPlayerJoined;
        session.Changed -= OnSessionChanged;
        session.SessionPropertiesChanged -= OnSessionPropertiesChanged;
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

    private void OnDestroy()
    {
        // 파괴 시 이벤트만 정리한다 (파괴된 객체로 세션 콜백이 유입되는 것을 막는다).
        // TODO(#51/#55/#56): 씬 전환 통합 시, 씬 언로드 전에 await LeaveAsync()로
        //   세션을 실제로 나가는 라이프사이클 처리가 필요하다. OnDestroy에서의
        //   async leave는 완료가 보장되지 않으므로 여기서 부르지 않는다.
        if (m_session != null)
            UnsubscribeSessionEvents(m_session);
    }

    [SerializeField] private float m_guiTopOffset = 10f;

    private string m_joinCodeInput = string.Empty;
    private bool m_isBusy;
    private string m_status = "대기 중 - 세션을 만들거나 코드로 참가하세요.";

    private void OnGUI()
    {
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
        if (m_isBusy) return;
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
        if (m_isBusy) return;
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
        if (m_isBusy) return;
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
}
