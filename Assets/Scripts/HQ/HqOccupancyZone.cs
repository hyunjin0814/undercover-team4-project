using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 본부 구역 트리거 안의 플레이어 수와 무인 지속 시간을 센다. 서버(또는 오프라인) 전용.
/// 무인일 때의 규칙은 이 값을 읽는 쪽이 판단한다.
/// </summary>
[RequireComponent(typeof(Collider))]
public class HqOccupancyZone : MonoBehaviour
{
    private readonly HashSet<PlayerHealth> m_occupants = new HashSet<PlayerHealth>();

    private float m_unmannedSince;

    public bool IsUnmanned => m_occupants.Count == 0;

    public float UnmannedSeconds => IsUnmanned ? Time.time - m_unmannedSince : 0f;

    /// <summary>이 플레이어가 본부 구역 안에 있는지 돌려준다. 서버(또는 오프라인) 전용.</summary>
    public bool Contains(PlayerHealth player) => player != null && m_occupants.Contains(player);

    public event Action<int> OnOccupantCountChanged;

    private void Awake()
    {
        m_unmannedSince = Time.time;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsAuthority)
            return;

        PlayerHealth player = other.GetComponentInParent<PlayerHealth>();
        if (player == null)
            return;

        if (!m_occupants.Add(player))
            return;

        Debug.Log($"[본부] 상주 진입: {player.name} — 현재 {m_occupants.Count}명");
        OnOccupantCountChanged?.Invoke(m_occupants.Count);
    }

    private void OnTriggerExit(Collider other)
    {
        if (!IsAuthority)
            return;

        PlayerHealth player = other.GetComponentInParent<PlayerHealth>();
        if (player == null)
            return;

        if (!m_occupants.Remove(player))
            return;

        if (m_occupants.Count == 0)
            m_unmannedSince = Time.time;

        Debug.Log($"[본부] 상주 이탈: {player.name} — 현재 {m_occupants.Count}명");
        OnOccupantCountChanged?.Invoke(m_occupants.Count);
    }

    private void Update()
    {
        if (!IsAuthority || m_occupants.Count == 0)
            return;

        if (m_occupants.RemoveWhere(player => player == null) <= 0)
            return;

        if (m_occupants.Count == 0)
            m_unmannedSince = Time.time;

        Debug.Log($"[본부] 파괴된 상주자 정리 — 현재 {m_occupants.Count}명");
        OnOccupantCountChanged?.Invoke(m_occupants.Count);
    }

    private static bool IsAuthority =>
        NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;
}
