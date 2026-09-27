using System.Collections.Generic;
using System.Text;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// [디버그 전용] NPC의 화면 외형과 정답 외형, 몽타주 부합 여부를 에디터 씬 라벨로 표시한다.
/// </summary>
public class NpcProfileDebugView : MonoBehaviour
{
    [Tooltip("Scene 뷰에서 머리 위에 요약 라벨을 그린다")]
    [SerializeField] private bool m_drawSceneLabel = true;

    [Tooltip("라벨을 그릴 높이 오프셋(m)")]
    [SerializeField] private float m_labelHeight = 2.2f;

    private CitizenIdentity m_identity;
    private IAppearanceProfileSource m_appearanceSource;

    private CitizenIdentity Identity => m_identity != null ? m_identity : (m_identity = GetComponent<CitizenIdentity>());
    private IAppearanceProfileSource AppearanceSource =>
        m_appearanceSource ?? (m_appearanceSource = GetComponent<IAppearanceProfileSource>());

    private static AppearanceAssigner Assigner => Application.isPlaying ? App.Game.Appearance : null;
    private static AppearanceDatabase Database
    {
        get
        {
            AppearanceAssigner assigner = Assigner;
            return assigner != null ? assigner.Database : null;
        }
    }

    public struct Report
    {
        public bool HasVisual;
        public string VisualLine;
        public bool TruthKnown;
        public string TruthLine;
        public bool Consistent;
        public string RevealedAxesLine;
        public string VisualMontage;
        public bool IsCriminal;
        public List<MontageMatch> Montages;
        public string Warning;
    }

    public struct MontageMatch
    {
        public int Index;
        public string Text;
        public bool Matches;
    }

    /// <summary>현재 상태를 한 번에 수집한다. 인스펙터·Scene 라벨이 이 결과를 표시한다.</summary>
    public Report BuildReport()
    {
        var report = new Report { Montages = new List<MontageMatch>() };

        AppearanceProfile visual = AppearanceProfile.Unassigned;
        if (AppearanceSource != null)
            visual = AppearanceSource.Profile;
        report.HasVisual = visual.IsAssigned;
        report.VisualLine = FormatProfile(visual);

        CitizenIdentity identity = Identity;
        if (identity != null)
        {
            report.IsCriminal = identity.IsCriminal;

            AppearanceProfile truth = identity.Appearance;
            report.TruthKnown = truth.IsAssigned;
            if (report.TruthKnown)
            {
                report.TruthLine = FormatProfile(truth);
                report.Consistent = report.HasVisual && visual.Equals(truth);
                if (report.HasVisual && !report.Consistent)
                    report.Warning = "화면 외형과 정답 외형이 다릅니다 — 시각 조립 오류 가능";
            }
        }

        AppearanceAssigner assigner = Assigner;
        AppearanceDatabase db = Database;
        if (assigner != null && db != null)
        {
            IReadOnlyList<AppearanceProfile> criminals = assigner.CriminalProfiles;
            IReadOnlyList<RevealedAxisSet> criminalAxes = assigner.CriminalRevealedAxes;

            RevealedAxisSet union = default;
            foreach (RevealedAxisSet axes in criminalAxes)
            {
                foreach (AppearanceAxis axis in axes)
                    union.Add(axis);
            }
            report.RevealedAxesLine = union.IsEmpty ? "(없음)" : $"{FormatRevealedAxes(union)} (범인별 합집합)";

            if (report.HasVisual && !union.IsEmpty)
                report.VisualMontage = db.BuildMontageText(visual, union);

            for (int i = 0; i < criminals.Count; i++)
            {
                RevealedAxisSet axes = i < criminalAxes.Count ? criminalAxes[i] : default;
                bool matches = report.HasVisual && !axes.IsEmpty && visual.MatchesOn(criminals[i], axes);
                report.Montages.Add(new MontageMatch
                {
                    Index = i,
                    Text = db.BuildMontageText(criminals[i], axes),
                    Matches = matches,
                });
            }
        }

        return report;
    }

    /// <summary>6축을 "축이름: 값(인덱스)" 형태로 이어 붙인다. DB가 없으면 인덱스만.</summary>
    public string FormatProfile(in AppearanceProfile profile)
    {
        if (!profile.IsAssigned)
            return "(미배정)";

        AppearanceDatabase db = Database;
        var builder = new StringBuilder();
        for (int i = 0; i < AppearanceProfile.k_axisCount; i++)
        {
            var axis = (AppearanceAxis)i;
            int index = profile.GetIndex(axis);
            string axisName = AppearanceDatabase.GetAxisName(axis);
            string valueName = db != null
                ? AppearanceDatabase.GetOptionName(db.GetOption(axis, index))
                : index.ToString();

            if (builder.Length > 0)
                builder.Append(" / ");
            builder.Append(axisName).Append(": ").Append(valueName);
        }
        return builder.ToString();
    }

    private string FormatRevealedAxes(RevealedAxisSet axes)
    {
        if (axes.IsEmpty)
            return "(없음)";

        var builder = new StringBuilder();
        foreach (AppearanceAxis axis in axes)
        {
            if (builder.Length > 0)
                builder.Append(", ");
            builder.Append(AppearanceDatabase.GetAxisName(axis));
        }
        return builder.ToString();
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!m_drawSceneLabel || !Application.isPlaying)
            return;

        Report report = BuildReport();
        if (!report.HasVisual)
            return;

        var label = new StringBuilder();
        label.Append(name);
        if (report.IsCriminal) label.Append(" [범인]");
        label.Append('\n').Append(report.VisualLine);
        if (!string.IsNullOrEmpty(report.Warning))
            label.Append("\n⚠ ").Append(report.Warning);
        else if (report.TruthKnown && report.Consistent)
            label.Append("\n외형=정답 ✓");

        Handles.Label(transform.position + Vector3.up * m_labelHeight, label.ToString());
    }
#endif
}
