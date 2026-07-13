using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
// using Unity.Netcode; // TODO: 네트워크 테스트 시 주석 해제

/// <summary>
/// 스캐너 아이템. 3초 채널링 후 대상 시민의 스캔 정보를 로그로 출력한다.
/// 배터리 충전식(IChargeable)이며, 스캔 1회당 배터리를 1 소모한다. (GDD 5-1/5-2)
/// </summary>
public class Scanner : ItemBase, IChargeable
{
    [Header("스캐너 설정")]
    [SerializeField]
    private float m_channelSeconds = 3f;

    [Tooltip("채널링 도중 대상이 이 거리(m)를 벗어나면 스캔 실패로 처리한다 (#91)")]
    [SerializeField]
    private float m_scanKeepRange = 5f;

    [SerializeField]
    private int m_maxBattery = 5;

    // TODO: 네트워크 테스트 시 m_currentBattery를 NetworkVariable<int>로 교체 (지금은 로컬 값이라 다른 클라에 동기화 안 됨)
    private int m_currentBattery;
    private bool m_isScanning;
    private CancellationTokenSource m_cts;

    // ---- IChargeable ----

    public int CurrentBattery => m_currentBattery;
    public int MaxBattery => m_maxBattery;
    public bool IsFullyCharged => m_currentBattery >= m_maxBattery;
    public bool IsDepleted => m_currentBattery <= 0;

    // TODO: 네트워크 테스트 시 OnCharged를 NetworkVariable.OnValueChanged로 구동 (전 클라 UI 갱신)
    public event Action<int> OnCharged;

    /// <summary>
    /// 스캔 채널링 성공 이벤트 — 조회된 시민 프로필을 전달한다. 스캔 결과 프레젠터(#39)가 구독한다.
    /// 인스턴스 이벤트이므로 구독자는 자기 스캐너의 결과만 받는다 — 스캔 결과는 본인 화면 전용. (GDD 5-4)
    /// </summary>
    // TODO: 네트워크 테스트(서버 권위 전환) 시 서버 실행 결과를 오너 클라에 RPC로 돌려준 뒤 그 수신 지점에서 발행
    public event Action<CitizenProfile> OnScanCompleted;

    // TODO: 네트워크 테스트 시 서버 권위로만 호출 (본부 충전기 → ServerRpc 요청 → 서버가 배터리 변경)
    public void Charge(int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        m_currentBattery = Mathf.Min(m_currentBattery + amount, m_maxBattery);
        OnCharged?.Invoke(m_currentBattery);
    }

    // ---- ItemBase ----

    // TODO: 네트워크 테스트 시 서버 권위로 재검증 (클라 CanUse 결과는 신뢰 불가)
    /// <summary>스캔 중이 아니고 배터리가 남아 있을 때만 사용 가능.</summary>
    public override bool CanUse() => !m_isScanning && !IsDepleted;

    // TODO: 네트워크 테스트 시 서버 권위로 실행 (오너 입력 → ServerRpc 요청 → 서버가 스캔 실행/검증 후 결과 동기화)
    public override void Use(GameObject target)
    {
        if (!CanUse())
        {
            if (IsDepleted)
            {
                Debug.Log($"스캐너 배터리 부족! (남은 배터리: {m_currentBattery})");
            }

            return;
        }

        // 겨냥한 대상에서 시민 프로필을 조회한다 (#34). 신원을 확인할 수 없으면 스캔을 시작하지 않는다.
        // 실패 사유를 단계별로 구분해 로그한다 — 클라 프로필 미동기화(#52/#56) 같은 문제의 진단용.
        if (target == null)
        {
            Debug.Log("스캔 실패: 겨냥된 대상 없음");
            return;
        }

        // 콜라이더가 NPC 루트의 자식일 수 있으므로 부모까지 탐색한다 (Handcuffs.FindTarget과 동일 관례).
        CitizenIdentity identity = target.GetComponentInParent<CitizenIdentity>();
        if (identity == null)
        {
            Debug.Log($"스캔 실패: CitizenIdentity 없음 ({target.name})");
            return;
        }

        if (identity.Profile == null)
        {
            Debug.Log($"스캔 실패: 프로필 미배정 — 서버 배정 결과가 이 클라이언트에 동기화되지 않음 ({target.name})");
            return;
        }

        // 프로필만이 아니라 신원 컴포넌트째 넘긴다 — 채널링 도중 거리 이탈 판정에 대상 위치가 필요 (#91)
        ScanAsync(identity).Forget();
    }

    // ---- 스캔 채널링 ----

    // TODO: 네트워크 테스트 시 채널링 타이밍/배터리 소모를 서버 권위로 (클라 시간 조작 방지). 스캔 결과는 ClientRpc/NetworkVariable로 전파
    private async UniTaskVoid ScanAsync(CitizenIdentity identity)
    {
        m_isScanning = true;
        m_cts = new CancellationTokenSource();

        // 프로필은 시작 시점 값으로 고정 — 채널링 도중 재배정될 일은 없다
        CitizenProfile profile = identity.Profile;

        try
        {
            // 단일 Delay가 아닌 프레임 루프 — 도중 거리 이탈을 즉시 실패시킨다 (#91)
            // 뗌 취소는 Yield의 토큰 예외(catch)로, 거리 이탈은 return으로 — 취소 사유가 구분된다
            float elapsed = 0f;
            while (elapsed < m_channelSeconds)
            {
                if (identity == null || !IsInRange(identity.transform))
                {
                    Debug.Log("스캔 실패 — 대상이 범위를 벗어남");
                    return;
                }

                await UniTask.Yield(PlayerLoopTiming.Update, m_cts.Token);
                elapsed += Time.deltaTime;
            }

            m_currentBattery = Mathf.Max(m_currentBattery - 1, 0);
            Debug.Log($"NPC 스캔됨: {GetScanInfo(profile)}");
            OnScanCompleted?.Invoke(profile);
            Debug.Log($"남은 배터리: {m_currentBattery}");
        }
        catch (OperationCanceledException)
        {
            Debug.Log("스캔 취소됨");
        }
        finally
        {
            m_isScanning = false;
            m_cts?.Dispose();
            m_cts = null;
        }
    }

    /// <summary>좌클릭 뗌 — 진행 중인 스캔 채널링을 취소한다 (#91).</summary>
    public override void CancelUse() => CancelScan();

    /// <summary>진행 중인 스캔 채널링을 취소한다. (이동·피격 등 방해 시 호출)</summary>
    public void CancelScan() => m_cts?.Cancel();

    private bool IsInRange(Transform target)
    {
        return (target.position - transform.position).sqrMagnitude
            <= m_scanKeepRange * m_scanKeepRange;
    }

    private static string GetScanInfo(CitizenProfile profile)
    {
        if (profile == null)
        {
            return "대상 정보 없음";
        }

        return $"이름={profile.CitizenName}, 타입={profile.m_typeView}, 세력={profile.m_factionView}";
    }

    // ---- 라이프사이클 ----

    // TODO: 네트워크 테스트 시 배터리 초기화를 서버의 OnNetworkSpawn으로 이동 (NetworkVariable은 서버가 초기화)
    private void Awake()
    {
        m_currentBattery = m_maxBattery;
    }

    // TODO: 네트워크 테스트 시 OnNetworkDespawn에서도 취소 처리 추가
    private void OnDisable()
    {
        CancelScan();
    }

    private void OnDestroy()
    {
        m_cts?.Cancel();
        m_cts?.Dispose();
    }
}
