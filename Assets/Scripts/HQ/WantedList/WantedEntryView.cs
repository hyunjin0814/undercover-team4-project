using TMPro;
using UnityEngine;

/// <summary>
/// 수배 리스트의 한 줄 — 검거 조건 몽타주 포트레이트 / 조건 글 / 현상금 표시. (#669)
///
/// 정답이 개체에서 조건으로 바뀌면서 이름은 뺐다 — 조건에 맞으면 누구를 잡아도 정답인데 행에 이름이
/// 있으면 플레이어가 그 한 개체만 찾게 된다. 조건 글(`BuildMontageText`)은 이름과 달리 개체를
/// 가리키지 않으므로(축:값 나열일 뿐) 남긴다 — 그림과 함께 띄운다.
///
/// 몽타주는 그림이 정본이다 (#607) — 완성물이 아니라 재료(조건 축 + 값)만 항목에 실려 오고 각 피어가
/// 조립한다. 그래서 AppearanceDatabase를 Bind 인자로 받는다(행마다 배선하지 않기 위해).
/// </summary>
public class WantedEntryView : MonoBehaviour
{
    [Header("몽타주")]
    [SerializeField]
    private MontagePortraitView m_portrait;

    [Tooltip(
        "조건 글 표시 — 축:값 나열(예: \"머리색: 빨강 / 안경: 있음\"). 이름이 아니라 조건만 말하므로 그림과 함께 띄운다"
    )]
    [SerializeField]
    private TMP_Text m_conditionText;

    [Tooltip("현상금 표시 (#395). 비워 두면 표시하지 않는다")]
    [SerializeField]
    private TMP_Text m_bountyText;

    // 금액 서식은 프로젝트 공용이고 행마다 같은 문구다 — SerializeField로 두면 행 프리팹이
    // 늘 때마다 같은 키를 다시 배선해야 하고 하나만 빠지면 그 행만 옛 표기로 남는다. (#497)
    // 언어 변경 갱신은 WantedListView가 로케일 변경에 걸고 통째로 다시 그리는 것으로 처리한다.
    private const string k_commonTable = "CommonTable";
    private const string k_moneyKey = "Common.Unit.Money";

    public void Bind(in WantedEntry entry, AppearanceDatabase appearanceDatabase)
    {
        if (m_portrait != null)
            m_portrait.Bind(entry.Appearance, entry.RevealedAxes, appearanceDatabase);

        if (m_conditionText != null)
            m_conditionText.text =
                appearanceDatabase != null
                    ? appearanceDatabase.BuildMontageText(entry.Appearance, entry.RevealedAxes)
                    : string.Empty;

        if (m_bountyText != null)
            m_bountyText.text = LocalizedStrings.Get(k_commonTable, k_moneyKey, entry.Bounty);
    }
}
