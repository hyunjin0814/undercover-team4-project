using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 권위 피어가 시뮬레이션한 래그돌 자세를 전 뼈 단위로 원격에 스트리밍하고, 원격은 이를 재생한다.
/// 골반은 항상 월드 좌표로 보내며, 물리·래그돌 진입 시점은 소유자가 결정한다.
/// </summary>
[DefaultExecutionOrder(100)]
[DisallowMultipleComponent]
public class RagdollPoseStreamer : NetworkBehaviour
{
    public enum PoseAuthority
    {
        Server,
        Owner,
    }

    private const int k_maxSnapshots = 4;

    private const ushort k_sequenceHalfRange = 32768;

    [Header("권위")]
    [Tooltip("이 시체의 자세를 누가 정하는가 — NPC는 Server, 플레이어는 Owner.\n\n" +
             "⚠ 루트 NetworkTransform의 AuthorityMode와 <b>반드시 같아야</b> 한다. 어긋나면 " +
             "몸과 루트가 서로 다른 피어에서 계산돼 시체가 이름표를 두고 떠난다")]
    [SerializeField] private PoseAuthority m_authority = PoseAuthority.Server;

    [Header("송신")]
    [Tooltip("몇 번의 물리 스텝마다 한 번 보내는가 — 50Hz 기준 2면 25Hz, 4면 12.5Hz.\n\n" +
             "<b>대역폭의 유일한 1차 손잡이다</b>(계획서 §1-6). 실측으로 시체 1구당 원격 1인 기준 " +
             "<b>약 2KB/s</b>(25Hz · 페이로드 82B)이고, 서버 업링크는 여기에 <b>동시 시체 수 × 원격 " +
             "수</b>가 곱해진다. 예산이 빠듯하면 여기부터 올린다 — 잃는 것은 보간 지연뿐이고 " +
             "정착 자세는 그대로다")]
    [SerializeField] private int m_sendEveryFixedSteps = 2;

    [Tooltip("몇 번의 물리 스텝마다 <b>뼈 길이</b>를 한 번 보내는가 — 50Hz 기준 25면 2Hz. " +
             "<b>0이면 끈다</b>(정착 패킷만 나르던 예전 동작).\n\n" +
             "권위 쪽 뼈는 무너지는 동안 관절이 늘어나는데 원격은 물리를 안 굴려 바인드 그대로다. " +
             "예전에는 그 차를 <b>정착 패킷 한 번</b>으로 몰아 넘겼고, 원격은 그것을 한 프레임에 " +
             "통째로 입혀 <b>정착 순간 상체가 내려앉았다</b>(실측 3.9cm · 2026-09-02, 클라에서만 " +
             "보인다). 드리프트는 몇 초에 걸쳐 자라는 값이라 0.5초마다 갱신하면 보정 한 번이 1cm " +
             "미만으로 쪼개지고, 그 구간은 몸이 빨라 눈에 안 띈다.\n\n" +
             "<b>비용은 208B × 주기</b>(뼈 17 기준). 2Hz면 약 0.4KB/s로 시체 1구·원격 1인 기준 " +
             "2.1 → 2.5KB/s다. <b>자세 패킷(86B·25Hz)은 안 바뀐다</b> — 별도 RPC다")]
    [SerializeField] private int m_lengthEveryFixedSteps = 25;

    [Header("수신")]
    [Tooltip("원격이 얼마나 뒤처진 시점을 그리는가(초) — 송신 주기의 2배가 기본값이다.\n\n" +
             "이만큼 늦게 그려야 다음 스냅샷이 이미 도착해 있어 <b>보간할 두 점</b>이 생긴다. " +
             "짧으면 패킷 하나만 늦어도 재생이 끝점에 부딪혀 시체가 멈칫하고, 길면 그만큼 " +
             "화면이 늦는다")]
    [SerializeField] private float m_interpolationDelay = 0.08f;

    private RagdollRig m_rig;

    private bool m_streaming;
    private ushort m_sequence;
    private int m_stepsSinceSend;
    private int m_stepsSinceLengths;
    private Quaternion[] m_sendBuffer;
    private Vector3[] m_lengthBuffer;
    private uint[] m_packedBuffer;
    private Quaternion[] m_unpackBuffer;

    private Snapshot[] m_snapshots;
    private int m_snapshotCount;

    private ushort m_newestSequence;
    private bool m_haveSequence;

    private bool m_streamDriven;

    private Quaternion[] m_applyBuffer;

    private bool m_expectingStream;
    private bool m_hasReceivedPose;
    private bool m_warnedBoneMismatch;

    private bool m_streamEnded;

    private ushort m_newestLengthSequence;
    private bool m_haveLengthSequence;

    private struct Snapshot
    {
        public float Time;
        public Vector3 HipsWorld;
        public Quaternion[] Rotations;
    }

    private bool IsPoseAuthority
    {
        get
        {
            if (!IsSpawned)
                return true;

            return m_authority == PoseAuthority.Server ? IsServer : IsOwner;
        }
    }

    public bool IsStreamDriven => m_streamDriven;

    public bool IsAwaitingFirstPose => m_expectingStream && !m_hasReceivedPose;

    public event System.Action OnSettledPoseReceived;

    private void Awake()
    {
        m_rig = GetComponentInChildren<RagdollRig>(true);
        if (m_rig == null)
        {
            Debug.LogWarning(
                $"RagdollPoseStreamer: RagdollRig를 찾지 못해 자세 스트리밍을 끈다 — {name}",
                this
            );
            enabled = false;
            return;
        }

        m_rig.EnsureCollected();
    }

    /// <summary>자세 스트리밍을 시작한다(멱등). 권위가 아니면 무동작이다.</summary>
    public void BeginStreaming()
    {
        if (m_rig == null || !m_rig.IsValid)
            return;

        m_expectingStream = true;
        m_hasReceivedPose = false;

        m_streamEnded = false;
        m_haveLengthSequence = false;

        if (!IsPoseAuthority)
            return;

        m_streaming = true;
        m_stepsSinceSend = 0;
        m_stepsSinceLengths = 0;
    }

    /// <summary>스트림을 끊고 마지막 자세를 신뢰 전송으로 한 번 더 보낸다(멱등).</summary>
    public void EndStreaming()
    {
        if (!m_streaming)
            return;

        m_streaming = false;
        m_expectingStream = false;

        if (!IsSpawned || m_rig == null || !m_rig.IsValid || m_rig.Hips == null)
            return;

        EnsureSendBuffer();
        if (!m_rig.CaptureLocalPose(m_sendBuffer, out _))
            return;

        m_rig.CaptureBoneLengths(m_lengthBuffer);

        m_sequence = unchecked((ushort)(m_sequence + 1));
        FinalPoseRpc(m_sequence, m_rig.Hips.position, Pack(m_sendBuffer), m_lengthBuffer);
    }

    /// <summary>잠든 몸이 다시 움직이면 자세 스트림을 재개한다(멱등).</summary>
    public void ResumeStreaming()
    {
        if (m_streaming || m_streamEnded || !IsPoseAuthority)
            return;

        m_streaming = true;
        m_expectingStream = true;
        m_stepsSinceSend = 0;
        m_stepsSinceLengths = 0;
    }

    /// <summary>시체를 순간이동시켰을 때 원격 스냅샷 버퍼를 비우고 새 자세만 보낸다.</summary>
    public void SendTeleportPose()
    {
        if (!IsPoseAuthority || !IsSpawned || m_rig == null || !m_rig.IsValid || m_rig.Hips == null)
            return;

        EnsureSendBuffer();
        if (!m_rig.CaptureLocalPose(m_sendBuffer, out _))
            return;

        m_rig.CaptureBoneLengths(m_lengthBuffer);

        m_sequence = unchecked((ushort)(m_sequence + 1));
        TeleportPoseRpc(m_sequence, m_rig.Hips.position, Pack(m_sendBuffer), m_lengthBuffer);
    }

    public void StopStreaming()
    {
        m_streaming = false;
        m_expectingStream = false;

        m_streamDriven = false;
        m_snapshotCount = 0;
        m_haveSequence = false;
        m_streamEnded = true;
    }

    private void FixedUpdate()
    {
        if (!m_streaming || !IsSpawned || !IsPoseAuthority)
            return;

        if (m_lengthEveryFixedSteps > 0 && ++m_stepsSinceLengths >= m_lengthEveryFixedSteps)
        {
            m_stepsSinceLengths = 0;
            SendLengths();
        }

        m_stepsSinceSend++;
        if (m_stepsSinceSend < m_sendEveryFixedSteps)
            return;

        m_stepsSinceSend = 0;
        SendSnapshot();
    }

    private void SendSnapshot()
    {
        if (m_rig == null || !m_rig.IsValid || m_rig.Hips == null)
            return;

        EnsureSendBuffer();

        if (!m_rig.CaptureLocalPose(m_sendBuffer, out _))
            return;

        m_sequence = unchecked((ushort)(m_sequence + 1));
        StreamPoseRpc(m_sequence, m_rig.Hips.position, Pack(m_sendBuffer));
    }

    /// <summary>뼈 길이를 자세 패킷과 별도로, 낮은 주기의 언리라이어블로 보낸다.</summary>
    private void SendLengths()
    {
        if (m_rig == null || !m_rig.IsValid)
            return;

        EnsureSendBuffer();
        if (!m_rig.CaptureBoneLengths(m_lengthBuffer))
            return;

        m_sequence = unchecked((ushort)(m_sequence + 1));
        StreamLengthsRpc(m_sequence, m_lengthBuffer);
    }

    private void EnsureSendBuffer()
    {
        if (m_sendBuffer == null || m_sendBuffer.Length != m_rig.BoneCount)
            m_sendBuffer = new Quaternion[m_rig.BoneCount];

        if (m_packedBuffer == null || m_packedBuffer.Length != m_rig.BoneCount)
            m_packedBuffer = new uint[m_rig.BoneCount];

        if (m_lengthBuffer == null || m_lengthBuffer.Length != m_rig.BoneCount)
            m_lengthBuffer = new Vector3[m_rig.BoneCount];
    }

    private uint[] Pack(Quaternion[] rotations)
    {
        for (int i = 0; i < rotations.Length; i++)
        {
            Quaternion rotation = rotations[i];
            m_packedBuffer[i] = QuaternionCompressor.CompressQuaternion(ref rotation);
        }

        return m_packedBuffer;
    }

    private Quaternion[] Unpack(uint[] packed)
    {
        if (m_unpackBuffer == null || m_unpackBuffer.Length != packed.Length)
            m_unpackBuffer = new Quaternion[packed.Length];

        for (int i = 0; i < packed.Length; i++)
            QuaternionCompressor.DecompressQuaternion(ref m_unpackBuffer[i], packed[i]);

        return m_unpackBuffer;
    }

    /// <summary>압축된 자세 스냅샷을 언리라이어블로 수신한다.</summary>
    [Rpc(SendTo.NotMe, Delivery = RpcDelivery.Unreliable)]
    private void StreamPoseRpc(ushort sequence, Vector3 hipsWorld, uint[] packed)
        => ReceivePose(sequence, hipsWorld, packed, terminal: false);

    /// <summary>뼈 길이 스냅샷을 언리라이어블로 수신한다.</summary>
    [Rpc(SendTo.NotMe, Delivery = RpcDelivery.Unreliable)]
    private void StreamLengthsRpc(ushort sequence, Vector3[] lengths)
        => ReceiveLengths(sequence, lengths);

    /// <summary>스트림의 마지막 자세와 뼈 길이를 신뢰 전송으로 수신한다.</summary>
    [Rpc(SendTo.NotMe)]
    private void FinalPoseRpc(ushort sequence, Vector3 hipsWorld, uint[] packed, Vector3[] lengths)
        => ReceivePose(sequence, hipsWorld, packed, terminal: true, lengths);

    /// <summary>시체를 통째로 옮겼다 — 보간을 끊고 이 자세만 남긴다. (<see cref="SendTeleportPose"/>)</summary>
    [Rpc(SendTo.NotMe)]
    private void TeleportPoseRpc(ushort sequence, Vector3 hipsWorld, uint[] packed, Vector3[] lengths)
    {
        m_snapshotCount = 0;
        m_haveSequence = false;
        ReceivePose(sequence, hipsWorld, packed, terminal: false, lengths);
    }

    /// <summary>받은 자세를 스냅샷 버퍼에 넣는다. 스트림과 정착 패킷이 같은 경로를 탄다.</summary>
    private void ReceivePose(
        ushort sequence,
        Vector3 hipsWorld,
        uint[] packed,
        bool terminal,
        Vector3[] lengths = null
    )
    {
        if (m_rig == null || !m_rig.IsValid || packed == null)
            return;

        if (m_streamEnded)
            return;

        if (packed.Length != m_rig.BoneCount)
        {
            if (!m_warnedBoneMismatch)
            {
                m_warnedBoneMismatch = true;
                Debug.LogWarning(
                    $"RagdollPoseStreamer: 뼈 수가 달라 자세를 버린다 — {name} "
                        + $"받은={packed.Length} 내리그={m_rig.BoneCount}. 피어마다 리그가 다른 프리팹이다",
                    this
                );
            }

            return;
        }

        m_hasReceivedPose = true;

        if (m_haveSequence && !IsNewer(sequence, m_newestSequence))
        {
            return;
        }

        m_newestSequence = sequence;
        m_haveSequence = true;
        m_streamDriven = true;

        if (lengths != null && lengths.Length == m_rig.BoneCount && m_rig.ApplyBoneLengths(lengths))
        {
            m_newestLengthSequence = sequence;
            m_haveLengthSequence = true;
        }

        PushSnapshot(hipsWorld, Unpack(packed));

        if (!terminal)
            return;

        m_expectingStream = false;
        OnSettledPoseReceived?.Invoke();
    }

    /// <summary>받은 뼈 길이를 보간 없이 즉시 적용한다.</summary>
    private void ReceiveLengths(ushort sequence, Vector3[] lengths)
    {
        if (m_rig == null || !m_rig.IsValid || lengths == null)
            return;

        if (m_streamEnded)
            return;

        if (lengths.Length != m_rig.BoneCount)
            return;

        if (m_haveLengthSequence && !IsNewer(sequence, m_newestLengthSequence))
            return;

        m_newestLengthSequence = sequence;
        m_haveLengthSequence = true;

        m_rig.ApplyBoneLengths(lengths);
    }

    private void PushSnapshot(Vector3 hipsWorld, Quaternion[] rotations)
    {
        EnsureSnapshotBuffers();

        if (m_snapshotCount == k_maxSnapshots)
        {
            Snapshot oldest = m_snapshots[0];
            for (int i = 0; i < k_maxSnapshots - 1; i++)
                m_snapshots[i] = m_snapshots[i + 1];

            m_snapshots[k_maxSnapshots - 1] = oldest;
            m_snapshotCount = k_maxSnapshots - 1;
        }

        Snapshot slot = m_snapshots[m_snapshotCount];
        slot.Time = Time.time;
        slot.HipsWorld = hipsWorld;
        for (int i = 0; i < rotations.Length; i++)
            slot.Rotations[i] = rotations[i];

        m_snapshots[m_snapshotCount] = slot;
        m_snapshotCount++;
    }

    private void EnsureSnapshotBuffers()
    {
        int bones = m_rig.BoneCount;

        if (m_snapshots == null || m_snapshots.Length != k_maxSnapshots)
        {
            m_snapshots = new Snapshot[k_maxSnapshots];
            m_snapshotCount = 0;
        }

        for (int i = 0; i < m_snapshots.Length; i++)
        {
            if (m_snapshots[i].Rotations == null || m_snapshots[i].Rotations.Length != bones)
                m_snapshots[i].Rotations = new Quaternion[bones];
        }

        if (m_applyBuffer == null || m_applyBuffer.Length != bones)
            m_applyBuffer = new Quaternion[bones];
    }

    private void Update()
    {
        if (!m_streamDriven || IsPoseAuthority)
            return;

        TickApply();
    }

    private void TickApply()
    {
        if (m_snapshotCount == 0)
            return;

        float renderTime = Time.time - m_interpolationDelay;

        if (m_snapshotCount == 1 || renderTime <= m_snapshots[0].Time)
        {
            ApplySnapshot(m_snapshots[0]);
            return;
        }

        for (int i = 0; i < m_snapshotCount - 1; i++)
        {
            Snapshot from = m_snapshots[i];
            Snapshot to = m_snapshots[i + 1];

            if (renderTime > to.Time)
                continue;

            float span = to.Time - from.Time;
            float t = span > 0.0001f ? Mathf.Clamp01((renderTime - from.Time) / span) : 1f;
            ApplyBlend(from, to, t);
            return;
        }

        ApplySnapshot(m_snapshots[m_snapshotCount - 1]);
    }

    private void ApplyBlend(Snapshot from, Snapshot to, float t)
    {
        for (int i = 0; i < m_applyBuffer.Length; i++)
            m_applyBuffer[i] = Quaternion.Slerp(from.Rotations[i], to.Rotations[i], t);

        ApplyPose(m_applyBuffer, Vector3.Lerp(from.HipsWorld, to.HipsWorld, t));
    }

    private void ApplySnapshot(Snapshot snapshot) => ApplyPose(snapshot.Rotations, snapshot.HipsWorld);

    private void ApplyPose(Quaternion[] rotations, Vector3 hipsWorld)
    {
        Transform hips = m_rig.Hips;
        if (hips == null)
            return;

        m_rig.ApplyLocalPose(rotations, hips.localPosition);
        hips.position = hipsWorld;
    }

    private static bool IsNewer(ushort candidate, ushort current)
        => unchecked((ushort)(candidate - current)) is > 0 and < k_sequenceHalfRange;
}
