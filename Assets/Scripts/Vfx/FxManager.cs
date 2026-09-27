using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 일회성 연출의 단일 창구 — EFx 하나로 이펙트와 소리를 함께 재생하고 필요하면 전 피어에 전파한다.
/// App.Game.Fx로 접근하며, 조합표는 인스펙터에 있다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class FxManager : NetworkedManagerBase
{
    [Serializable]
    public class Entry
    {
        [Tooltip("이 조합을 가리키는 키 — 코드에서 App.Game.Fx.PlayEverywhere(Id, ...)로 부른다")]
        public EFx Id;

        [Tooltip("함께 낼 파티클. None이면 소리만 난다")]
        public EEffect Effect;

        [Tooltip("함께 낼 소리. None이면 파티클만 난다")]
        public EAudioClip Sound;
    }

    [Tooltip("순간 → 파티클·소리 조합표. 같은 Id가 둘 이상이면 먼저 오는 것만 쓰이고 경고한다")]
    [SerializeField] private Entry[] m_entries;

    private readonly Dictionary<EFx, Entry> m_index = new();

    private readonly HashSet<EFx> m_warned = new();

    protected override void Awake()
    {
        base.Awake();

        BuildIndex();
    }

    /// <summary>연출을 전 피어에서 1회 재생한다(서버 판정 지점용). 세션 밖이면 로컬로만 재생한다.</summary>
    public void PlayEverywhere(
        EFx id,
        Vector3 position,
        Vector3 normal = default,
        float volumeScale = 1f
    )
    {
        if (id == EFx.None)
            return;

        if (!IsSpawned)
        {
            PlayHere(id, position, normal, volumeScale);
            return;
        }

        if (!IsServer)
        {
            WarnOnce(id, $"{id}를 클라이언트에서 전파 요청했다 — 서버 판정 지점에서 부를 것");
            PlayHere(id, position, normal, volumeScale);
            return;
        }

        PlayRpc(id, position, normal, volumeScale);
    }

    /// <summary>부른 피어에서만 1회 재생한다(이미 전 피어에서 도는 경로용).</summary>
    public void PlayHere(
        EFx id,
        Vector3 position,
        Vector3 normal = default,
        float volumeScale = 1f
    )
    {
        if (id == EFx.None)
            return;

        if (!m_index.TryGetValue(id, out Entry entry))
        {
            WarnOnce(id, $"조합표에 {id} 항목이 없다 — 연출을 건너뛴다");
            return;
        }

        App.Game.Effect?.Play(entry.Effect, position, normal);
        App.Sound?.PlaySfxAt(entry.Sound, position, volumeScale);
    }

    [Rpc(SendTo.Everyone)]
    private void PlayRpc(EFx id, Vector3 position, Vector3 normal, float volumeScale) =>
        PlayHere(id, position, normal, volumeScale);

    private void BuildIndex()
    {
        if (m_entries == null)
        {
            Debug.LogWarning($"[FxManager] 조합표가 비어 있다 — 모든 연출이 나오지 않는다. {name}에 채울 것", this);
            return;
        }

        foreach (Entry entry in m_entries)
        {
            if (entry == null || entry.Id == EFx.None)
                continue;

            if (!m_index.TryAdd(entry.Id, entry))
                Debug.LogError($"[FxManager] 조합표에 {entry.Id}가 중복 등록됐다 — 먼저 오는 항목만 쓰인다", this);
        }
    }

    private void WarnOnce(EFx id, string reason)
    {
        if (m_warned.Add(id))
            Debug.LogWarning($"[FxManager] {reason}", this);
    }
}
