using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

/// <summary>
/// 폭탄 테스트용 에디터 전용 단축키 — [ 폭탄 배치, ] 즉시 폭발, \ 폭탄 옆으로 순간이동.
/// 배치된 폭탄은 기본적으로 제자리에 고정되며, 요청은 서버로 넘긴다.
/// </summary>
[RequireComponent(typeof(BombChaseEvent))]
public class BombDevHotkeys : NetworkBehaviour
{
#if UNITY_EDITOR
    [Tooltip("끄면 단축키가 듣지 않는다 — 같은 키를 쓰는 다른 테스트를 할 때 잠깐 내린다")]
    [SerializeField] private bool m_enabled = true;

    [Header("키 (인스펙터 조절)")]
    [Tooltip("내 앞에 폭탄을 놓는다")]
    [SerializeField] private Key m_spawnKey = Key.LeftBracket;

    [Tooltip("놓인 폭탄을 지금 터뜨린다 — 무장 전이면 무장부터 시킨다")]
    [SerializeField] private Key m_detonateKey = Key.RightBracket;

    [Tooltip("놓인 폭탄 옆으로 순간이동한다")]
    [SerializeField] private Key m_teleportKey = Key.Backslash;

    [Header("배치 (인스펙터 조절)")]
    [Tooltip("내 앞 몇 m에 놓을지 — 0에 가까우면 내 몸 안에 겹친다")]
    [SerializeField] private float m_spawnDistance = 3f;

    [Tooltip("순간이동으로 폭탄에서 몇 m 떨어져 설지 — 0이면 폭탄에 겹쳐 선다")]
    [SerializeField] private float m_teleportStandoff = 2f;

    [Tooltip("놓는 자리에서 이 거리(m) 안의 NavMesh를 찾아 그 위에 올려놓는다")]
    [SerializeField] private float m_navSampleMaxDistance = 5f;

    [Tooltip("켜면 놓자마자 무장한다 — 진짜 이벤트처럼 쫓아오고 30초 뒤 스스로 터진다. " +
             "끄면(기본) 제자리에 굳어 지형을 골라 세워 둘 수 있다")]
    [SerializeField] private bool m_armOnSpawn;

    [Tooltip("폭발 후 폭탄을 남겨 둘 시간(초) — BombChaseEvent의 잔류 시간과 같은 뜻")]
    [SerializeField] private float m_lingerSeconds;

    private BombChaseEvent m_event;

    private BombDevice m_bomb;
    private bool m_resolved;
    private float m_despawnAt;

    private bool IsAuthority => !IsSpawned || IsServer;

    private void Awake() => m_event = GetComponent<BombChaseEvent>();

    private void Update()
    {
        if (!m_enabled)
            return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard[m_spawnKey].wasPressedThisFrame)
                RequestSpawn();
            if (keyboard[m_detonateKey].wasPressedThisFrame)
                RequestDetonate();
            if (keyboard[m_teleportKey].wasPressedThisFrame)
                RequestTeleport();
        }

        if (IsAuthority)
            TickDespawn();
    }

    private void RequestSpawn()
    {
        if (IsSpawned && !IsServer)
            SpawnRpc();
        else
            ServerSpawn(DevPlayerLookup.LocalPlayer());
    }

    private void RequestDetonate()
    {
        if (IsSpawned && !IsServer)
            DetonateRpc();
        else
            ServerDetonate();
    }

    private void RequestTeleport()
    {
        if (IsSpawned && !IsServer)
            TeleportRpc();
        else
            ServerTeleport(DevPlayerLookup.LocalPlayer());
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SpawnRpc(RpcParams rpcParams = default) =>
        ServerSpawn(DevPlayerLookup.ResolvePlayer(rpcParams.Receive.SenderClientId));

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void DetonateRpc() => ServerDetonate();

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void TeleportRpc(RpcParams rpcParams = default) =>
        ServerTeleport(DevPlayerLookup.ResolvePlayer(rpcParams.Receive.SenderClientId));

    private void ServerSpawn(Transform player)
    {
        if (player == null)
        {
            Debug.LogWarning("[폭탄/개발용] 요청자의 플레이어를 찾지 못했다", this);
            return;
        }

        BombDevice prefab = m_event != null ? m_event.DevBombPrefab : null;
        if (prefab == null)
        {
            Debug.LogWarning("[폭탄/개발용] BombChaseEvent에 폭탄 프리팹이 물려 있지 않다", this);
            return;
        }

        if (BombDevice.Active != null)
        {
            Debug.Log($"[폭탄/개발용] 이미 폭탄이 있다 — {m_detonateKey}로 터뜨린 뒤 다시 누를 것");
            return;
        }

        Vector3 forward = Flat(player.forward, Vector3.forward);
        Vector3 wanted = player.position + forward * m_spawnDistance;
        if (!NavMesh.SamplePosition(wanted, out NavMeshHit hit, m_navSampleMaxDistance, NavMesh.AllAreas))
        {
            Debug.LogWarning($"[폭탄/개발용] 눈앞 {wanted}에서 NavMesh를 못 찾았다 — 인도 쪽을 보고 다시 누를 것", this);
            return;
        }

        m_bomb = Instantiate(prefab, hit.position, Quaternion.LookRotation(-forward));
        if (SuddenEventUtil.IsNetworkSessionActive)
            m_bomb.GetComponent<NetworkObject>().Spawn();

        m_bomb.OnExploded += HandleExploded;
        m_resolved = false;

        if (m_armOnSpawn)
            m_bomb.ServerArm();

        Debug.Log(
            $"[폭탄/개발용] {m_spawnKey} — 눈앞 {m_spawnDistance}m에 폭탄을 놓았다"
                + (m_armOnSpawn ? " (무장 — 쫓아온다)" : $" (제자리 고정 — {m_detonateKey}로 터뜨릴 것)")
        );
    }

    private void ServerDetonate()
    {
        BombDevice bomb = BombDevice.Active;
        if (bomb == null)
        {
            Debug.Log($"[폭탄/개발용] 터뜨릴 폭탄이 없다 — {m_spawnKey}로 먼저 놓을 것");
            return;
        }

        if (!bomb.IsCountingDown)
            bomb.ServerArm();

        bomb.ServerDetonate();
    }

    private void ServerTeleport(Transform player)
    {
        BombDevice bomb = BombDevice.Active;
        if (bomb == null)
        {
            Debug.Log($"[폭탄/개발용] 갈 폭탄이 없다 — {m_spawnKey}로 먼저 놓을 것");
            return;
        }

        if (player == null || !player.TryGetComponent(out PlayerMovement movement))
        {
            Debug.LogWarning("[폭탄/개발용] 요청자의 PlayerMovement를 찾지 못했다", this);
            return;
        }

        Vector3 origin = bomb.transform.position;
        Vector3 away = Flat(player.position - origin, bomb.transform.forward);
        Vector3 wanted = origin + away * m_teleportStandoff;

        Vector3 destination =
            NavMesh.SamplePosition(wanted, out NavMeshHit hit, m_navSampleMaxDistance, NavMesh.AllAreas)
                ? hit.position
                : wanted;

        movement.ServerTeleport(destination, Quaternion.LookRotation(-away));
        Debug.Log($"[폭탄/개발용] {m_teleportKey} — 폭탄 옆 {m_teleportStandoff}m로 이동했다");
    }

    private void HandleExploded()
    {
        if (m_resolved)
            return;

        m_resolved = true;
        m_despawnAt = Time.time + m_lingerSeconds;
    }

    private void TickDespawn()
    {
        if (m_bomb == null || !m_resolved || Time.time < m_despawnAt)
            return;

        m_bomb.OnExploded -= HandleExploded;
        SuddenEventUtil.DespawnOrDestroy(m_bomb.gameObject);
        m_bomb = null;
        m_resolved = false;
    }

    private static Vector3 Flat(Vector3 direction, Vector3 fallback)
    {
        direction.y = 0f;
        return direction.sqrMagnitude > 0.0001f ? direction.normalized : fallback;
    }
#endif
}
