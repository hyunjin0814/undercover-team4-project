using System.Collections.Generic;
using UnityEngine;

public enum NpcRepathChannel
{
    Repath = 0,
    ThreatScan = 1,
    TargetScan = 2,
    StuckCheck = 3,
}

/// <summary>
/// NPC 하나의 경로 재탐색 시점을 관리한다 — 거리 티어와 생성 시 흩뿌린 위상으로 주기를 정한다.
/// </summary>
public class NpcRepathScheduler
{
    private const int k_channelCount = 4;

    private static readonly List<Transform> s_players = new List<Transform>();
    private static int s_playersFrame = -1;

    private static NpcRepathConfig s_fallbackConfig;

    private readonly NpcRepathConfig m_config;
    private readonly Transform m_owner;
    private readonly float[] m_nextDue = new float[k_channelCount];

    private float m_tierDistance = float.MaxValue;
    private float m_nextTierSample;

    public NpcRepathScheduler(NpcRepathConfig config, Transform owner)
    {
        m_config = Resolve(config, owner);
        m_owner = owner;

        float now = Time.time;
        for (int i = 0; i < k_channelCount; i++)
            m_nextDue[i] = now + Random.Range(0f, BaseInterval((NpcRepathChannel)i));

        m_nextTierSample = now + Random.Range(0f, m_config.TierSampleInterval);
    }

    private static NpcRepathConfig Resolve(NpcRepathConfig config, Transform owner)
    {
        if (config != null)
            return config;

        s_fallbackConfig ??= ScriptableObject.CreateInstance<NpcRepathConfig>();
        Debug.LogError(
            $"NpcRepathConfig가 비어 있다 — 코드 기본값으로 대체한다. NpcController의 m_repathConfig를 채울 것: {owner.name}",
            owner
        );

        return s_fallbackConfig;
    }

    /// <summary>이 채널을 지금 돌 차례인지 돌려주고, true면 다음 만료를 예약한다.</summary>
    public bool Due(NpcRepathChannel channel)
    {
        float now = Time.time;
        int index = (int)channel;
        if (now < m_nextDue[index])
            return false;

        m_nextDue[index] = now + IntervalFor(channel);
        return true;
    }

    /// <summary>게이트를 거치지 않고 직접 경로를 계산했음을 표시한다.</summary>
    public void MarkDone(NpcRepathChannel channel)
    {
        m_nextDue[(int)channel] = Time.time + IntervalFor(channel);
    }

    /// <summary>다음 Due를 즉시 통과시킨다(상태 진입 시에만 사용).</summary>
    public void ForceDue(NpcRepathChannel channel)
    {
        m_nextDue[(int)channel] = 0f;
    }

    /// <summary>채널의 현재 주기(초) — 로그·디버그 표시용. 조회만 하고 아무것도 바꾸지 않는다.</summary>
    public float IntervalOf(NpcRepathChannel channel) =>
        channel == NpcRepathChannel.Repath ? PickTierInterval() : BaseInterval(channel);

    private float IntervalFor(NpcRepathChannel channel) =>
        channel == NpcRepathChannel.Repath ? TieredRepathInterval() : BaseInterval(channel);

    private float BaseInterval(NpcRepathChannel channel)
    {
        switch (channel)
        {
            case NpcRepathChannel.ThreatScan:
                return m_config.ThreatScanInterval;
            case NpcRepathChannel.TargetScan:
                return m_config.TargetScanInterval;
            case NpcRepathChannel.StuckCheck:
                return m_config.StuckCheckInterval;
            default:
                return m_config.MidInterval;
        }
    }

    private float TieredRepathInterval()
    {
        float now = Time.time;
        if (now >= m_nextTierSample)
        {
            m_nextTierSample = now + m_config.TierSampleInterval;
            m_tierDistance = NearestPlayerDistance();
        }

        return PickTierInterval();
    }

    private float PickTierInterval()
    {
        if (m_tierDistance <= m_config.NearDistance)
            return m_config.NearInterval;
        return m_tierDistance <= m_config.MidDistance ? m_config.MidInterval : m_config.FarInterval;
    }

    private float NearestPlayerDistance()
    {
        RefreshPlayers();
        if (s_players.Count == 0)
            return float.MaxValue;

        Vector3 origin = m_owner.position;
        float nearestSqr = float.MaxValue;
        for (int i = 0; i < s_players.Count; i++)
        {
            Transform player = s_players[i];
            if (player == null)
                continue;

            Vector3 delta = player.position - origin;
            delta.y = 0f;

            float sqr = delta.sqrMagnitude;
            if (sqr < nearestSqr)
                nearestSqr = sqr;
        }

        return nearestSqr == float.MaxValue ? float.MaxValue : Mathf.Sqrt(nearestSqr);
    }

    private static void RefreshPlayers()
    {
        if (s_playersFrame == Time.frameCount)
            return;
        s_playersFrame = Time.frameCount;

        s_players.Clear();

        IReadOnlyList<PlayerHealth> found = PlayerHealth.All;
        for (int i = 0; i < found.Count; i++)
            if (found[i] != null)
                s_players.Add(found[i].transform);
    }
}
