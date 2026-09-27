using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 본부 시민 인명부 — 라운드에 스폰된 전 NPC의 정본 신원을 서버가 채워 NetworkList로 동기화한다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class DirectoryManager : NetworkedManagerBase
{
    private CriminalAssigner Assigner => App.Game.CriminalAssigner;
    private NpcSpawner Spawner => App.Game.NpcSpawner;

    private readonly NetworkList<DirectoryEntry> m_directory = new NetworkList<DirectoryEntry>();

    public NetworkList<DirectoryEntry> Directory => m_directory;

    public event Action OnListReady;

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            m_directory.Clear();

            if (Assigner != null)
                Assigner.OnCriminalAssigned += HandleAssigned;
            else
                Debug.LogWarning(
                    "DirectoryManager: CriminalAssigner를 찾지 못해 인명부를 채울 수 없다",
                    this
                );
        }

        OnListReady?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        if (Assigner != null)
            Assigner.OnCriminalAssigned -= HandleAssigned;
    }

    private void HandleAssigned(IReadOnlyList<NpcController> _) => BuildDirectory();

    private void BuildDirectory()
    {
        m_directory.Clear();

        if (Spawner == null)
        {
            Debug.LogWarning(
                "DirectoryManager: NpcSpawner를 찾지 못해 인명부를 채울 수 없다",
                this
            );
            return;
        }

        foreach (NpcController npc in Spawner.SpawnedNpcs)
        {
            if (npc == null)
                continue;

            CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
            CitizenProfile profile = identity != null ? identity.Profile : null;
            if (profile == null)
                continue;

            m_directory.Add(DirectoryEntry.FromProfile(profile));
        }

        Debug.Log($"[인명부] {m_directory.Count}명 등재 완료");
    }
}
