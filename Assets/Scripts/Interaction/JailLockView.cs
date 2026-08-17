using UnityEngine;

/// <summary>
/// 자물쇠 겉모습 — 감옥 문에 걸린 <b>사슬</b>을 잠김 상태에 맞춰 보였다 감췄다 한다. (#537)
///
/// <see cref="JailLock"/>은 상태(잠김/열림)만 갖고 생김새를 모른다. 이 컴포넌트가 그 사이를 잇는다 —
/// 자물쇠 쪽에 렌더러 참조가 붙으면 "누가 왜 여는지는 모른다"는 그쪽의 경계가 무너진다.
///
/// <b>전 피어에서 각자 갱신한다.</b> <see cref="JailLock.OnLockChanged"/>는 NetworkVariable 콜백이라
/// 모든 피어에서 발생한다(오프라인에서는 자물쇠가 직접 발행한다) — 새로 동기화할 것이 없다.
///
/// 사슬이 걸려 있으면 잠긴 것, 사라졌으면 털린 것이다. 문이 열려 있는 것(<see cref="JailDoor"/>)과
/// 같은 사실을 다르게 말하지만, 문 뒤에 보이는 공간이 없어진 뒤로는(#537) 이쪽이 더 눈에 띈다.
/// </summary>
public class JailLockView : MonoBehaviour
{
    [Header("자물쇠 (비우면 부모·씬에서 자동 탐색)")]
    [SerializeField] private JailLock m_jailLock;

    [Header("잠김일 때만 보일 것들 (비우면 자신·자식의 렌더러 전부)")]
    [Tooltip("문에 걸린 사슬 등 — 자물쇠가 풀리면 감춘다")]
    [SerializeField] private Renderer[] m_lockedVisuals;

    private void Awake()
    {
        if (m_lockedVisuals == null || m_lockedVisuals.Length == 0)
            m_lockedVisuals = GetComponentsInChildren<Renderer>(true);

        if (m_jailLock == null)
            m_jailLock = GetComponentInParent<JailLock>();
        if (m_jailLock == null)
            m_jailLock = App.Game.JailLock;
    }

    // 구독은 Start에서 — 자물쇠의 Awake(NetworkVariable 초기화)가 끝난 뒤가 보장된다 (아키텍처 규칙 R6)
    private void Start()
    {
        if (m_jailLock == null)
        {
            Debug.LogWarning("JailLockView: 자물쇠(JailLock)를 찾지 못했다 — 사슬이 갱신되지 않는다", this);
            return;
        }

        m_jailLock.OnLockChanged += Apply;
        Apply(m_jailLock.IsLocked); // 구독 전에 이미 정해져 있던 상태를 한 번 반영하고 시작한다
    }

    private void OnDestroy()
    {
        if (m_jailLock != null)
            m_jailLock.OnLockChanged -= Apply;
    }

    private void Apply(bool locked)
    {
        for (int i = 0; i < m_lockedVisuals.Length; i++)
            if (m_lockedVisuals[i] != null)
                m_lockedVisuals[i].enabled = locked;
    }
}
