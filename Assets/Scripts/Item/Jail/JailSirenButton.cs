using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 본부 유치장 사이렌 버튼 설비 — 누르면 경보를 울리고 진행 중인 탈옥을 저지한다.
/// 쿨다운·먹통 판정은 서버가 한다.
/// </summary>
public class JailSirenButton : InstallableItem
{
    private const double k_noCooldown = -1d;

    private JailZone m_jailZone;

    [Tooltip("연타 방지 쿨다운(초). 서버가 강제한다")]
    [Min(0f)]
    [SerializeField]
    private float m_cooldownSeconds = 22f;

    private readonly NetworkVariable<double> m_cooldownEndSynced = new NetworkVariable<double>(k_noCooldown);
    private double m_cooldownEnd = k_noCooldown;

    private double Now => IsSpawned && NetworkManager != null ? NetworkManager.ServerTime.Time : Time.timeAsDouble;

    public float CooldownRemaining =>
        (float)Math.Max(0d, (IsSpawned ? m_cooldownEndSynced.Value : m_cooldownEnd) - Now);

    public bool IsOnCooldown => CooldownRemaining > 0f;

    public float CooldownSeconds => m_cooldownSeconds;

    public event Action<float> OnCooldownStarted;

    [Header("먹통 연동 (#434)")]
    [Tooltip("켜면 먹통 중 사이렌이 막힌다 — 화면도 경보도 없어 손쓸 방법이 사라진다")]
    [SerializeField]
    private bool m_blockedByBlackout;

    private bool IsJammed =>
        m_blockedByBlackout && (App.Game.SuddenEvent?.GetEvent<DeviceBlackoutEvent>()?.IsCommsBlackout ?? false);

    protected override void OnInstallableSpawn()
    {
        m_cooldownEndSynced.OnValueChanged += HandleCooldownEndChanged;

        if (IsOnCooldown)
            OnCooldownStarted?.Invoke(CooldownRemaining);
    }

    protected override void OnInstallableDespawn() => m_cooldownEndSynced.OnValueChanged -= HandleCooldownEndChanged;

    private void HandleCooldownEndChanged(double previous, double current) => RaiseCooldownStarted();

    /// <summary>미설치·쿨다운·먹통이면 윤곽선이 뜨지 않는다 — ServerFire가 거르는 조건과 같은 기준.</summary>
    public override bool CanInteract(GameObject interactor) =>
        base.CanInteract(interactor) && !IsOnCooldown && !IsJammed;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.Siren;

    protected override void OnInteract(GameObject interactor)
    {
        if (!IsSpawned || IsServer)
        {
            ServerFire();
            return;
        }

        RequestFireRpc();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestFireRpc() => ServerFire();

    private void ServerFire()
    {
        if (IsSpawned && !IsServer)
            return;
        if (!IsInstalled || IsOnCooldown || IsJammed)
            return;

        StartCooldown();

        bool repelled = App.Game.SuddenEvent?.GetEvent<JailbreakEvent>()?.ServerRepelIntruder() ?? false;
        Debug.Log(repelled ? "[사이렌] 침입자 저지" : "[사이렌] 울렸지만 제지할 침입 없음");

        if (!IsSpawned)
        {
            PlayLocal();
            return;
        }

        PlaySirenRpc();
    }

    private void StartCooldown()
    {
        m_cooldownEnd = Now + m_cooldownSeconds;

        if (IsSpawned && IsServer)
            m_cooldownEndSynced.Value = m_cooldownEnd;
        else if (!IsSpawned)
            RaiseCooldownStarted();
    }

    private void RaiseCooldownStarted()
    {
        float remaining = CooldownRemaining;
        if (remaining > 0f)
            OnCooldownStarted?.Invoke(remaining);
    }

    [Rpc(SendTo.Everyone)]
    private void PlaySirenRpc() => PlayLocal();

    private void PlayLocal()
    {
        if (m_jailZone == null)
            m_jailZone = App.Game.Jail;

        if (m_jailZone == null)
        {
            Debug.LogWarning("JailSirenButton: 유치장을 찾지 못해 경보음을 낼 자리가 없다", this);
            return;
        }

        App.Sound?.PlaySfxAt(EAudioClip.JailSiren, m_jailZone.PlayerEntryPoint.position);
    }
}
