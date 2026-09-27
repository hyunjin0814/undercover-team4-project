using System;
using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 세력별 진짜 문양 index를 세션 시작 시 한 번 뽑아 전 클라이언트에 동기화하는 세션 상주 오브젝트.
/// 정직한 시민은 진짜 문양을, 위조범은 가짜 문양을 단다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class FactionSymbolManager : NetworkedManagerBase
{
    [Header("공식 기록 (세력 문양 세트 조회용)")]
    [SerializeField] private OfficialRecords m_officialRecords;

    private readonly NetworkList<byte> m_realIndices = new NetworkList<byte>();

    public event Action OnRealIndicesChanged;

    public override void OnNetworkSpawn()
    {
        m_realIndices.OnListChanged += HandleListChanged;

        if (IsServer)
            RollRealIndices();

        OnRealIndicesChanged?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        m_realIndices.OnListChanged -= HandleListChanged;
    }

    private void RollRealIndices()
    {
        if (m_officialRecords == null)
        {
            Debug.LogWarning("[FactionSymbolManager] OfficialRecords가 없어 진짜 문양을 선정할 수 없다.", this);
            return;
        }

        m_realIndices.Clear();
        foreach (OfficialRecords.Faction faction in Enum.GetValues(typeof(OfficialRecords.Faction)))
        {
            int count = m_officialRecords.GetVariantsCount(faction);
            byte real = count > 0 ? (byte)Random.Range(0, count) : (byte)0;
            m_realIndices.Add(real);
        }
    }

    /// <summary>지정 세력의 이번 세션 진짜 문양 index. 미동기화·미선정이면 0.</summary>
    public int RealIndex(OfficialRecords.Faction faction)
    {
        int i = (int)faction;
        return i >= 0 && i < m_realIndices.Count ? m_realIndices[i] : 0;
    }

    private void HandleListChanged(NetworkListEvent<byte> _) => OnRealIndicesChanged?.Invoke();
}
