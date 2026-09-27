using System.Text;
using UnityEngine;

/// <summary>
/// 라운드 시작 배정 결과(진범 포함)를 콘솔에 출력하는 진단 로그. 데모 빌드 전 제거 대상.
/// </summary>
public sealed class AssignmentLog
{
    private readonly StringBuilder m_builder = new StringBuilder();

    public AssignmentLog(int npcCount, int suspectCount, int revealCount)
    {
        m_builder.AppendLine(
            $"시민 프로필 배정 완료 ({npcCount}명, 예비 용의자 {suspectCount}명 중 {revealCount}명 공개):"
        );
    }

    /// <summary>시민 한 명분을 기록한다. 배정이 <see cref="CitizenIdentity"/>에 반영된 뒤 호출할 것.</summary>
    public void Add(CitizenIdentity identity, bool isSuspect)
    {
        CitizenProfile profile = identity.Profile;
        if (profile == null)
        {
            return;
        }

        string roleTag = identity.IsCriminal ? $"  ← 수배 공개 ({identity.Reaction})"
            : isSuspect ? $"  ← 예비 용의자 · 미공개 ({identity.Reaction})"
            : identity.Reaction != ReactionType.Compliant ? $"  (미끼: {identity.Reaction})"
            : "";

        string bountyTag = identity.Bounty > 0 ? $"  [현상금 {identity.Bounty}원]" : "";
        string conditionTag = isSuspect ? $"  [{identity.WantedCondition}]" : "";

        m_builder.AppendLine(
            $"  {profile.CitizenName} | {profile.m_typeView} | {profile.m_factionView}{roleTag}{bountyTag}{conditionTag}"
        );
    }

    /// <summary>요약 줄을 붙이고 콘솔에 한 번에 출력한다.</summary>
    public void Flush(int totalAssignedBounty)
    {
        m_builder.AppendLine(
            $"  → 배정 현상금 총합 {totalAssignedBounty}원 (돌발 이벤트 수익 별도)"
        );
        Debug.Log(m_builder.ToString());
    }
}
