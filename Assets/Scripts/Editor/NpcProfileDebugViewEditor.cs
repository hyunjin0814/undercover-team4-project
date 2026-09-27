using UnityEditor;
using UnityEngine;

/// <summary>
/// NpcProfileDebugView의 커스텀 인스펙터 — 선택한 NPC의 외형·몽타주 부합 상태를 실시간으로 보여 준다.
/// </summary>
[CustomEditor(typeof(NpcProfileDebugView))]
public class NpcProfileDebugViewEditor : Editor
{
    public override bool RequiresConstantRepaint() => Application.isPlaying;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var view = (NpcProfileDebugView)target;

        EditorGUILayout.Space();
        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox("플레이 모드에서 배정된 값을 표시합니다. 정답 외형(Appearance)·몽타주는 호스트에서만 채워집니다.", MessageType.Info);
            return;
        }

        NpcProfileDebugView.Report report = view.BuildReport();

        EditorGUILayout.LabelField("검증 결과", EditorStyles.boldLabel);

        string role = report.IsCriminal ? "범인" : "일반/디코이";
        EditorGUILayout.LabelField("역할(서버 전용)", role);

        EditorGUILayout.LabelField("화면 외형", report.HasVisual ? report.VisualLine : "(미배정)", WrapLabel());
        if (report.TruthKnown)
            EditorGUILayout.LabelField("정답 외형", report.TruthLine, WrapLabel());
        else
            EditorGUILayout.LabelField("정답 외형", "(호스트에서만 표시)");

        if (!string.IsNullOrEmpty(report.Warning))
            EditorGUILayout.HelpBox("⚠ " + report.Warning, MessageType.Error);
        else if (report.TruthKnown && report.HasVisual)
            EditorGUILayout.HelpBox("화면 외형 = 정답 외형 ✓", MessageType.Info);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("공개 축", string.IsNullOrEmpty(report.RevealedAxesLine) ? "(미배정)" : report.RevealedAxesLine, WrapLabel());
        EditorGUILayout.LabelField("이 외형으로 만든 몽타주", string.IsNullOrEmpty(report.VisualMontage) ? "-" : report.VisualMontage, WrapLabel());

        if (report.Montages != null && report.Montages.Count > 0)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("범인 몽타주 부합", EditorStyles.boldLabel);
            foreach (NpcProfileDebugView.MontageMatch m in report.Montages)
            {
                string mark = m.Matches ? "● 부합" : "○ 비부합";
                EditorGUILayout.LabelField($"#{m.Index + 1} {mark}", $"\"{m.Text}\"", WrapLabel());
            }
        }
    }

    private static GUIStyle s_wrap;
    private static GUIStyle WrapLabel()
    {
        if (s_wrap == null)
            s_wrap = new GUIStyle(EditorStyles.label) { wordWrap = true };
        return s_wrap;
    }
}
