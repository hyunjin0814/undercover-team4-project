using Unity.Netcode;

/// <summary>
/// NGO 네임드 메시지 수신 등록·해제 헬퍼 — 클라이언트 시작 시점에 맞춰 등록한다.
/// </summary>
public sealed class NamedMessageSubscription
{
    private readonly string m_name;
    private readonly CustomMessagingManager.HandleNamedMessageDelegate m_handler;
    private bool m_registered;

    public NamedMessageSubscription(
        string name, CustomMessagingManager.HandleNamedMessageDelegate handler)
    {
        m_name = name;
        m_handler = handler;
    }

    /// <summary>Start에서 부른다.</summary>
    public void Attach()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
            return;

        nm.OnClientStarted += Register;
        if (nm.IsListening)
            Register();
    }

    /// <summary>OnDestroy에서 부른다 — 등록한 적이 있을 때만 해제한다.</summary>
    public void Detach()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
            return;

        nm.OnClientStarted -= Register;
        if (m_registered && nm.CustomMessagingManager != null)
            nm.CustomMessagingManager.UnregisterNamedMessageHandler(m_name);
    }

    private void Register()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || nm.CustomMessagingManager == null)
            return;

        nm.CustomMessagingManager.RegisterNamedMessageHandler(m_name, m_handler);
        m_registered = true;
    }
}
