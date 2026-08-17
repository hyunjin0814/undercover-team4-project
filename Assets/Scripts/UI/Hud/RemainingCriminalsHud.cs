using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 현장/클라이언트 HUD에 "남은 범죄자 수 / 전체 범죄자 수"를 표시한다(#331).
/// 남은 수는 본부 수배 리스트(WantedListManager.Wanted) 항목 수, 전체 수는 TotalWanted를 쓴다 —
/// 둘 다 서버 권위로 채워져 전 클라이언트에 동기화되고, 검거 시 남은 수가 줄고 탈옥 재등재 시 다시 는다.
/// 표시 전용.
/// </summary>
public class RemainingCriminalsHud : MonoBehaviour
{
    private WantedListManager WantedList => App.Game.WantedList;

    [Header("표시")]
    [Tooltip("남은/전체 범죄자 수를 표시할 TextMeshProUGUI")]
    [SerializeField]
    private TextMeshProUGUI m_countText;

    // 코드가 대입하는 자리라 라벨에 LocalizeStringEvent를 붙일 수 없다 — 서로 덮어쓴다. (#497)
    [Tooltip(
        "표시 형식 — Hud.Criminals.Count ({0}=잡은 수, {1}=수배된 진범 수(TotalWanted)). 라운드 목표는 금액이므로(#395) 이 표시는 목표 진행도가 아니라 검거 현황이다 — 목표 진행도는 본부 게시판 RoundFundBoard가 담당"
    )]
    [SerializeField]
    private LocalizedString m_countFormat;

    // 마지막으로 표시한 값 — 바뀔 때만 문자열을 다시 만들어 불필요한 GC 할당을 피한다
    private int m_lastCaught = int.MinValue;
    private int m_lastTotal = int.MinValue;

    private bool m_bound;

    private void OnEnable()
    {
        if (m_countText == null)
        {
            Debug.LogWarning(
                "RemainingCriminalsHud: 카운트 텍스트가 연결되지 않아 표시할 수 없다",
                this
            );
            return;
        }

        if (WantedList == null)
        {
            Debug.LogWarning(
                "RemainingCriminalsHud: WantedListManager를 찾지 못해 표시할 수 없다",
                this
            );
            SetVisible(false);
            return;
        }

        WantedList.Wanted.OnListChanged += HandleListChanged;
        WantedList.OnTotalWantedChanged += Refresh;
        // late-join 초기 1회 렌더 — NetworkList는 초기 내용을 OnListChanged로 알리지 않는다
        WantedList.OnListReady += Refresh;

        if (WantedList.IsSpawned)
            Refresh();
        else
            SetVisible(false);
    }

    private void OnDisable()
    {
        if (WantedList != null)
        {
            WantedList.Wanted.OnListChanged -= HandleListChanged;
            WantedList.OnTotalWantedChanged -= Refresh;
            WantedList.OnListReady -= Refresh;
        }

        // 꺼진 HUD가 언어 변경에 반응하지 않게 — 다시 켜지면 Refresh가 다시 건다
        Unbind();
        m_lastCaught = int.MinValue;
        m_lastTotal = int.MinValue;
    }

    private void HandleListChanged(NetworkListEvent<WantedEntry> _) => Refresh();

    private void Refresh()
    {
        if (m_countText == null || WantedList == null)
            return;

        int total = WantedList.TotalWanted;
        // 이번 라운드 진범이 아직 등록되기 전(전체 0)에는 "0/0"을 띄우지 않고 숨긴다
        if (total <= 0)
        {
            SetVisible(false);
            m_lastCaught = int.MinValue;
            m_lastTotal = int.MinValue;
            return;
        }

        SetVisible(true);

        // 전체 − 남은 = 잡은 수 (탈옥 재등재 시 다시 감소). OpenCount는 세션 밖에서도 맞는 값이라
        // total(TotalWanted)과 같은 출처를 본다 — Wanted.Count를 쓰면 오프라인에서 0이 나와 '전부 검거'가 된다 (#669)
        int caught = total - WantedList.OpenCount;
        if (caught == m_lastCaught && total == m_lastTotal)
            return;

        m_lastCaught = caught;
        m_lastTotal = total;
        Bind(caught, total);
    }

    // 값이 바뀔 때마다 인자를 갈아끼우고 다시 구독한다 — 인자를 먼저 넣어야 구독 시점의
    // 첫 발화부터 숫자가 들어간 문장이 나온다 (SessionCodePanel과 같은 관례).
    private void Bind(int caught, int total)
    {
        if (m_countFormat == null || m_countFormat.IsEmpty)
        {
            Debug.LogWarning("RemainingCriminalsHud: 검거 현황 문구가 연결되지 않았다", this);
            return;
        }

        Unbind();

        m_countFormat.Arguments = new object[] { caught, total };
        m_countFormat.StringChanged += HandleStringChanged;
        m_bound = true;
    }

    private void HandleStringChanged(string localized)
    {
        if (m_countText != null)
            m_countText.text = localized;
    }

    private void Unbind()
    {
        if (!m_bound)
            return;

        m_countFormat.StringChanged -= HandleStringChanged;
        m_bound = false;
    }

    // TMP 컴포넌트만 켜고 끈다 (RoundTimerUI와 동일한 이유 — 자기 콜백을 죽이지 않도록)
    private void SetVisible(bool visible)
    {
        if (m_countText != null && m_countText.enabled != visible)
            m_countText.enabled = visible;
    }
}
