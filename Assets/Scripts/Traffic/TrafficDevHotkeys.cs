using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 폭주 차량 테스트용 에디터 전용 단축키 — O 내 앞에서 차 호출, P 나를 수직으로 띄우고 그 자리로 차 보내기.
/// 클라이언트에서도 요청을 서버로 넘겨 동작한다.
/// </summary>
public class TrafficDevHotkeys : NetworkBehaviour
{
#if UNITY_EDITOR
    [Tooltip("끄면 단축키가 듣지 않는다 — 같은 키를 쓰는 다른 테스트를 할 때 잠깐 내린다")]
    [SerializeField] private bool m_enabled = true;

    [Header("키 (인스펙터 조절)")]
    [Tooltip("내 앞에서 차가 나를 향해 달려온다")]
    [SerializeField] private Key m_carKey = Key.O;

    [Tooltip("콤보 — 나를 위로 날리고, 공중에 있는 동안 차가 내 자리를 지나간다")]
    [SerializeField] private Key m_comboKey = Key.P;

    [Header("차량")]
    [Tooltip(
        "차가 출발할 거리(m) — 나로부터 이만큼 뒤에서 나를 향해 온다. "
        + "⚠ <b>콤보의 성패가 이 값에 달려 있다</b>: 도착 시간(거리÷속도)이 <b>정착 전에</b> 끝나야 한다. "
        + "늦으면 몸이 이미 정착해 기상 블렌드에 들어가는데, 그 상태에서는 EnterRagdoll이 "
        + "'일어나는 중'으로 보고 임펄스를 통째로 버려 <b>제자리에서 죽는다</b>. "
        + "실측: 30m÷20m/s=1.5초는 늦어서 실패, 12m=0.6초면 아직 공중이라 잘 맞는다"
    )]
    [SerializeField] private float m_carDistance = 12f;

    [Tooltip("차 속도(m/s) — 도착까지 걸리는 시간은 거리÷속도다. 콤보의 선행 시간을 이 둘로 맞춘다")]
    [SerializeField] private float m_carSpeed = 20f;

    [Tooltip("차가 나를 지나친 뒤 더 달릴 거리(m) — 짧으면 내 앞에서 멈춰 판정 전에 사라진다")]
    [SerializeField] private float m_carOverrun = 20f;

    [Header("콤보 — 수직 발사")]
    [Tooltip(
        "위로 쏘아 올리는 속도(m/s) — <b>높이 띄우는 것이 목적이 아니다.</b> 차 판정 박스는 높이 "
        + "1.8m라, 최고점(≈속도²÷19.6 m)이 그보다 높으면 차가 몸 밑으로 지나가 아무 일도 안 난다. "
        + "필요한 것은 '아직 정착하지 않은 래그돌'이고 착지 후 구르는 동안도 그 상태다 — 4면 "
        + "최고점 0.8m로 충분하다"
    )]
    [SerializeField] private float m_launchUpSpeed = 4f;

    [Tooltip("비행 상태의 서버 상한(초) — 정착 통보가 안 올 때의 안전장치 (BombBlast와 같은 뜻)")]
    [SerializeField] private float m_launchMaxSeconds = 6f;

    [Tooltip(
        "발사보다 차를 이만큼(초) 먼저 출발시킨다 — 음수면 차가 먼저다. "
        + "차 도착 시각(거리÷속도)과 내 최고점(≈ 상승속도÷9.81초)이 겹치게 맞추는 값이다"
    )]
    [SerializeField] private float m_carHeadStartSeconds;

    private TrafficManager m_traffic;

    private bool IsAuthority => !IsSpawned || IsServer;

    private bool m_carPending;
    private float m_carDueAt;
    private Vector3 m_carTarget;
    private Vector3 m_carDirection;

    private bool m_launchPending;
    private float m_launchDueAt;
    private Transform m_launchPlayer;

    private void Awake() =>
        m_traffic = FindFirstObjectByType<TrafficManager>();

    private void Update()
    {
        if (!m_enabled)
            return;

        WarnIfNotSpawned();

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard[m_carKey].wasPressedThisFrame)
                RequestCar();
            if (keyboard[m_comboKey].wasPressedThisFrame)
                RequestCombo();
        }

        if (IsAuthority)
            TickPending();
    }

    private bool m_warnedNotSpawned;

    private void WarnIfNotSpawned()
    {
        if (m_warnedNotSpawned || IsSpawned)
            return;

        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsListening)
            return;

        m_warnedNotSpawned = true;
        Debug.LogError(
            "[차량/개발용] 이 컴포넌트가 스폰되지 않았다 — NetworkObject가 없는 오브젝트에 붙어 있다. "
                + "이대로면 클라이언트 입력이 서버로 가지 못해 <b>호스트에서만</b> 동작한다. "
                + "SuddenEvents 프리팹(BombDevHotkeys와 같은 오브젝트)으로 옮길 것.",
            this
        );
    }

    private void RequestCar()
    {
        if (IsSpawned && !IsServer)
            CarRpc();
        else
            ServerCar(DevPlayerLookup.LocalPlayer());
    }

    private void RequestCombo()
    {
        if (IsSpawned && !IsServer)
            ComboRpc();
        else
            ServerCombo(DevPlayerLookup.LocalPlayer());
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void CarRpc(RpcParams rpcParams = default) =>
        ServerCar(DevPlayerLookup.ResolvePlayer(rpcParams.Receive.SenderClientId));

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void ComboRpc(RpcParams rpcParams = default) =>
        ServerCombo(DevPlayerLookup.ResolvePlayer(rpcParams.Receive.SenderClientId));

    private void ServerCar(Transform player)
    {
        if (!TryBuildRun(player, out Vector3 target, out Vector3 direction))
            return;

        SpawnRunaway(target, direction);
    }

    private void ServerCombo(Transform player)
    {
        if (!TryBuildRun(player, out Vector3 target, out Vector3 direction))
            return;

        if (m_carHeadStartSeconds > 0f)
        {
            SpawnRunaway(target, direction);
            m_launchPending = true;
            m_launchDueAt = Time.time + m_carHeadStartSeconds;
            m_launchPlayer = player;
            return;
        }

        ServerLaunchUp(player);
        m_carPending = true;
        m_carDueAt = Time.time - m_carHeadStartSeconds;
        m_carTarget = target;
        m_carDirection = direction;
    }

    private void ServerLaunchUp(Transform player)
    {
        if (player == null)
            return;

        PlayerIncapacitation incap = player.GetComponent<PlayerIncapacitation>();
        if (incap == null || incap.IsIncapacitated)
        {
            Debug.LogWarning("[차량/개발용] 이미 무력화된 대상이라 날리지 않는다", this);
            return;
        }

        incap.ServerLaunch(m_launchMaxSeconds);

        Vector3 impulse = Vector3.up * m_launchUpSpeed;
        PlayerRagdoll ragdoll = player.GetComponent<PlayerRagdoll>();

        if (!IsSpawned)
        {
            ragdoll?.EnterRagdoll(impulse);
            return;
        }

        if (player.TryGetComponent(out NetworkObject victim))
            LaunchRpc(victim, impulse, RpcTarget.Single(victim.OwnerClientId, RpcTargetUse.Temp));
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void LaunchRpc(NetworkObjectReference victim, Vector3 impulse, RpcParams rpcParams)
    {
        if (victim.TryGet(out NetworkObject resolved)
            && resolved.TryGetComponent(out PlayerRagdoll ragdoll))
            ragdoll.EnterRagdoll(impulse);
    }

    private void TickPending()
    {
        if (m_carPending && Time.time >= m_carDueAt)
        {
            m_carPending = false;
            SpawnRunaway(m_carTarget, m_carDirection);
        }

        if (m_launchPending && Time.time >= m_launchDueAt)
        {
            m_launchPending = false;
            Transform player = m_launchPlayer;
            m_launchPlayer = null;
            ServerLaunchUp(player);
        }
    }

    private bool TryBuildRun(Transform player, out Vector3 target, out Vector3 direction)
    {
        target = default;
        direction = default;

        if (player == null)
        {
            Debug.LogWarning("[차량/개발용] 요청자의 플레이어를 찾지 못했다", this);
            return false;
        }
        if (m_traffic == null)
        {
            Debug.LogWarning("[차량/개발용] 씬에 TrafficManager가 없다", this);
            return false;
        }

        target = player.position;

        Vector3 facing = player.forward;
        facing.y = 0f;
        direction = facing.sqrMagnitude > 0.0001f ? -facing.normalized : Vector3.back;
        return true;
    }

    private void SpawnRunaway(Vector3 target, Vector3 direction)
    {
        if (m_traffic == null)
            return;

        Vector3 start = target - direction * m_carDistance;
        m_traffic.DevSpawnRunaway(start, direction, m_carDistance + m_carOverrun, m_carSpeed);

        Debug.Log(
            $"[차량/개발용] 배출 — {m_carDistance}m 뒤에서 {m_carSpeed}m/s "
                + $"(도착까지 약 {m_carDistance / Mathf.Max(0.01f, m_carSpeed):0.00}초)",
            this
        );
    }

#endif
}
