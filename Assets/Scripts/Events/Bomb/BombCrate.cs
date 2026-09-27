using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 씬에 미리 배치된 폭탄 상자 — 추격 폭탄의 스폰 후보이자 등장 연출을 맡는다.
/// 근처 폭탄이 Emerging 상태이면 각 피어가 스스로 상자를 연다.
/// </summary>
public class BombCrate : MonoBehaviour
{
    public static readonly List<BombCrate> All = new List<BombCrate>();

    [Tooltip("폭탄이 나올 자리 — 비우면 이 오브젝트의 위치를 쓴다. 상자 메시의 피벗이 구석에 있으면 지정할 것")]
    [SerializeField]
    private Transform m_spawnPoint;

    [Header("1단계 — 들썩")]
    [Tooltip("상자가 흔들리는 시간(초) — 아래 밀려남과 합쳐 BombDevice의 등장 시간보다 짧게 둘 것")]
    [SerializeField]
    private float m_shakeSeconds = 0.8f;

    [Tooltip("흔들림 높이(m)")]
    [SerializeField]
    private float m_shakeHeight = 0.06f;

    [Tooltip("흔들림 좌우 기울기(도)")]
    [SerializeField]
    private float m_shakeTilt = 4f;

    [Tooltip("초당 흔들림 횟수")]
    [SerializeField]
    private float m_shakeFrequency = 9f;

    [Header("2단계 — 밀려남")]
    [Tooltip("상자가 밀려나는 시간(초)")]
    [SerializeField]
    private float m_shoveSeconds = 0.45f;

    [Tooltip("뒤로 밀려나는 거리(m) — 폭탄을 완전히 벗어날 만큼. 짧으면 상자가 폭탄을 덮은 채로 남는다")]
    [SerializeField]
    private float m_shoveDistance = 1.4f;

    [Tooltip("밀려나며 살짝 떠오르는 높이(m) — 바닥을 긁는 게 아니라 밀쳐진 것으로 보이게")]
    [SerializeField]
    private float m_shoveHop = 0.22f;

    [Tooltip("밀려나며 기우는 각도(도) — 크게 주면 상자가 세로로 서 버린다(깊이가 높이보다 길다)")]
    [SerializeField]
    private float m_shoveTilt = 24f;

    private Vector3 m_closedPosition;
    private Quaternion m_closedRotation;
    private bool m_opening;
    private float m_elapsed;

    private BombDevice m_openedFor;

    public Vector3 SpawnPosition => m_spawnPoint != null ? m_spawnPoint.position : transform.position;

    public Quaternion SpawnRotation => m_spawnPoint != null ? m_spawnPoint.rotation : transform.rotation;

    private void Awake()
    {
        m_closedPosition = transform.localPosition;
        m_closedRotation = transform.localRotation;
    }

    private void OnEnable() => All.Add(this);

    private void OnDisable() => All.Remove(this);

    private void Update()
    {
        BombDevice bomb = BombDevice.Active;

        if (bomb == null)
        {
            if (m_opening)
                Close();
            return;
        }

        if (!m_opening)
        {
            if (bomb.State != BombState.Emerging)
                return;

            if (!IsNearestCrateTo(bomb.transform.position))
                return;

            m_openedFor = bomb;
            m_opening = true;
            m_elapsed = 0f;
        }
        else if (m_openedFor != bomb)
        {
            Close();
            return;
        }

        m_elapsed += Time.deltaTime;

        if (m_elapsed < m_shakeSeconds)
        {
            TickShake(m_elapsed);
            return;
        }

        TickShove(Mathf.Clamp01((m_elapsed - m_shakeSeconds) / m_shoveSeconds));
    }

    private void TickShake(float t)
    {
        float wave = Mathf.Abs(Mathf.Sin(t * m_shakeFrequency * Mathf.PI));
        transform.localPosition = m_closedPosition + Vector3.up * (wave * m_shakeHeight);
        transform.localRotation = m_closedRotation * Quaternion.Euler(
            0f, 0f, Mathf.Sin(t * m_shakeFrequency * Mathf.PI * 0.5f) * m_shakeTilt);
    }

    /// <summary>상자가 뒤로 밀려나며 기운다 — 폭탄이 안에서 밀어젖힌 그림.</summary>
    private void TickShove(float t)
    {
        float eased = 1f - (1f - t) * (1f - t);

        Vector3 back = m_closedRotation * Vector3.back;
        transform.localPosition = m_closedPosition
            + back * (m_shoveDistance * eased)
            + Vector3.up * (Mathf.Sin(t * Mathf.PI) * m_shoveHop);
        transform.localRotation = m_closedRotation * Quaternion.Euler(
            -m_shoveTilt * eased, m_shoveTilt * 0.5f * eased, 0f);
    }

    /// <summary>등장 중인 폭탄에서 가장 가까운 상자가 연다 — 이 상자가 그 상자인가.</summary>
    private bool IsNearestCrateTo(Vector3 position)
    {
        float mine = (position - SpawnPosition).sqrMagnitude;
        for (int i = 0; i < All.Count; i++)
        {
            BombCrate other = All[i];
            if (other == null || other == this)
                continue;

            if ((position - other.SpawnPosition).sqrMagnitude < mine)
                return false;
        }

        return true;
    }

    private void Close()
    {
        m_opening = false;
        m_openedFor = null;
        m_elapsed = 0f;
        transform.localPosition = m_closedPosition;
        transform.localRotation = m_closedRotation;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(SpawnPosition, 0.4f);
    }
}
