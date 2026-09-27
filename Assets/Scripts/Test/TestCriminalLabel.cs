using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// [테스트용] 예비 용의자 머리 위에 공개/대기 여부 라벨을 띄운다. 데모 빌드 전 제거할 것.
/// </summary>
public class TestCriminalLabel : MonoBehaviour
{
    private CriminalAssigner Assigner => App.Game.CriminalAssigner;

    [SerializeField] private string m_text = "수배";
    [SerializeField] private Color m_color = Color.red;

    [Header("미공개 예비 용의자 (#102)")]
    [Tooltip("제보 전화로 아직 공개되지 않은 대기 중 용의자에게 붙는 라벨")]
    [SerializeField] private string m_pendingText = "예비(미공개)";
    [SerializeField] private Color m_pendingColor = Color.gray;

    private void OnEnable()
    {
        if (Assigner != null)
            Assigner.OnCriminalAssigned += HandleCriminalAssigned;
    }

    private void Start()
    {
        if (Assigner != null && Assigner.CriminalNpcs.Count > 0)
            HandleCriminalAssigned(Assigner.CriminalNpcs);
    }

    private void OnDisable()
    {
        if (Assigner != null)
            Assigner.OnCriminalAssigned -= HandleCriminalAssigned;
    }

    private void HandleCriminalAssigned(IReadOnlyList<NpcController> criminals)
    {
        foreach (NpcController criminal in criminals)
        {
            if (criminal == null)
                continue;

            if (criminal.GetComponent<TestWorldLabel>() != null)
                continue;

            TestWorldLabel label = criminal.gameObject.AddComponent<TestWorldLabel>();
            Apply(label, criminal);
        }
    }

    private void Update()
    {
        if (Assigner == null)
            return;

        IReadOnlyList<NpcController> criminals = Assigner.CriminalNpcs;
        for (int i = 0; i < criminals.Count; i++)
        {
            if (criminals[i] == null)
                continue;

            TestWorldLabel label = criminals[i].GetComponent<TestWorldLabel>();
            if (label != null)
                Apply(label, criminals[i]);
        }
    }

    private void Apply(TestWorldLabel label, NpcController npc)
    {
        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        bool revealed = identity != null && identity.IsCriminal;
        label.Configure(revealed ? m_text : m_pendingText, revealed ? m_color : m_pendingColor);
    }
}
