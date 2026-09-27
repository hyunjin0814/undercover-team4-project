using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Localization;
using Random = UnityEngine.Random;

/// <summary>
/// 외형 특징 축별 옵션(표시 이름·색·머티리얼·프롭) 정의 SO.
/// NPC 외형 적용과 몽타주 텍스트·포트레이트 조립의 원본이다.
/// </summary>
[CreateAssetMenu(fileName = "AppearanceDatabase", menuName = "Scriptable Objects/AppearanceDatabase")]
public class AppearanceDatabase : ScriptableObject
{
    [Serializable]
    public class AppearanceOption
    {
        [Tooltip("몽타주·무전으로 전달하는 표시 이름 — NpcTable의 Npc.Appearance.* (예: 빨강, 없음)")]
        public LocalizedString DisplayName;

        [Tooltip("프롭 렌더러에 틴트되는 색 (프롭 없는 옵션에서는 무시)")]
        public Color Color = Color.white;

        [Tooltip("지정하면 대상 머티리얼 슬롯을 통째로 교체한다 (프롭이 없을 때만)")]
        public Material MaterialOverride;

        [Tooltip("머리 앵커에 붙일 프롭(Color로 틴트). 여럿이면 NPC마다 하나를 쓰며, 몽타주에서 구분되지 않는 것만 넣을 것")]
        public GameObject[] PropPrefabs;

        public GameObject MontageProp => FirstPropFrom(0);

        /// <summary>seed로 결정론적으로 NPC 메시 하나를 고른다 — 모든 피어에서 같은 결과가 나온다.</summary>
        public GameObject PickProp(ulong seed)
        {
            if (PropPrefabs == null || PropPrefabs.Length == 0)
                return null;

            ulong mixed = seed * 2654435761UL + 1013904223UL;
            return FirstPropFrom((int)(mixed % (ulong)PropPrefabs.Length));
        }

        /// <summary>start부터 순회하며 처음 만나는 비어 있지 않은 프롭을 돌려준다.</summary>
        private GameObject FirstPropFrom(int start)
        {
            if (PropPrefabs == null || PropPrefabs.Length == 0)
                return null;

            for (int i = 0; i < PropPrefabs.Length; i++)
            {
                GameObject prop = PropPrefabs[(start + i) % PropPrefabs.Length];
                if (prop != null)
                    return prop;
            }
            return null;
        }

        [Tooltip("SciFi 카탈로그 전용 값 — Generic 랜덤 배정에서 제외한다(예: 가림, 후드/헬멧, 특수 피부색)")]
        public bool SciFiOnly;

        [Tooltip("몽타주에서 이 값을 그리는 레이어 그림 — 프롭 값은 자동 생성. 색 축과 '없음'은 비운다")]
        public Sprite MontageLayer;

        [Tooltip("몽타주에서 이 값을 뺀다(그림·공개 축 후보 제외, 화면 착용은 유지). 그림·글로 옳게 말할 수 없는 값에만 켤 것")]
        public bool ExcludeFromMontage;
    }

    [Serializable]
    public class AxisDefinition
    {
        public AppearanceOption[] Options;

        [Tooltip("이 축이 '없음'(0번)으로 뽑힐 확률. 0이면 다른 값과 같이 1/n. 색 축에는 쓰지 않는다")]
        [Range(0f, 1f)]
        public float NoneChance;
    }

    private const string k_table = "NpcTable";

    [Header("특징 축 (AppearanceAxis 순서와 일치)")]
    [SerializeField] private AxisDefinition m_hairStyle;
    [SerializeField] private AxisDefinition m_hairColor;
    [SerializeField] private AxisDefinition m_skinColor;
    [SerializeField] private AxisDefinition m_facialHair;
    [SerializeField] private AxisDefinition m_headwear;
    [SerializeField] private AxisDefinition m_eyewear;

    [Header("몽타주 포트레이트 (#607) — 축에 속하지 않는 공용 레이어")]
    [Tooltip("맨 아래에 깔리는 두상 실루엣. 피부색이 공개 축이면 이 그림이 그 색으로 칠해진다 — 그래서 명암·질감 없는 순백이어야 색이 제대로 나온다")]
    [SerializeField] private Sprite m_montageBase;

    [Tooltip("머리 스타일은 미공개인데 머리색만 공개일 때 칠할 '형태 미상' 머리. 스타일을 말하지 않으면서 색을 얹을 자리를 만든다")]
    [SerializeField] private Sprite m_montageUnknownHair;

    [Tooltip("라운드가 진행될수록 몽타주를 흐리게 하는 표 (#724). 비우면 항상 원본 화질 그대로다")]
    [SerializeField] private MontageClarityTable m_clarityTable;

    public Sprite MontageBase => m_montageBase;

    public Sprite MontageUnknownHair => m_montageUnknownHair;

    public MontageClarityTable ClarityTable => m_clarityTable;

    public AxisDefinition GetAxis(AppearanceAxis axis) => axis switch
    {
        AppearanceAxis.HairStyle => m_hairStyle,
        AppearanceAxis.HairColor => m_hairColor,
        AppearanceAxis.SkinColor => m_skinColor,
        AppearanceAxis.FacialHair => m_facialHair,
        AppearanceAxis.Headwear => m_headwear,
        AppearanceAxis.Eyewear => m_eyewear,
        _ => null
    };

    public int GetOptionCount(AppearanceAxis axis)
    {
        AxisDefinition definition = GetAxis(axis);
        return definition?.Options?.Length ?? 0;
    }

    /// <summary>축 이름을 지금 언어로 읽는다 — 규약 키 <c>Npc.Axis.&lt;AppearanceAxis&gt;</c>.</summary>
    public static string GetAxisName(AppearanceAxis axis) =>
        LocalizedStrings.Get(k_table, "Npc.Axis." + axis);

    public static string UnknownValueName => LocalizedStrings.Get(k_table, "Npc.Appearance.Unknown");

    /// <summary>옵션의 표시 이름을 지금 언어로 읽는다. 배선이 빠진 옵션은 물음표로 둔다.</summary>
    public static string GetOptionName(AppearanceOption option) =>
        option == null || option.DisplayName == null || option.DisplayName.IsEmpty
            ? "?"
            : option.DisplayName.GetLocalizedString();

    public AppearanceOption GetOption(AppearanceAxis axis, int index)
    {
        AxisDefinition definition = GetAxis(axis);
        if (definition?.Options == null || index < 0 || index >= definition.Options.Length)
            return null;
        return definition.Options[index];
    }

    /// <summary>Generic 경로로 표현 가능한(SciFiOnly가 아닌) 옵션 인덱스 목록. 전부 SciFiOnly거나 옵션이 없으면 전체 인덱스로 폴백(제한 없음).</summary>
    public List<int> GetGenericSelectableIndices(AppearanceAxis axis)
    {
        var result = new List<int>();
        AxisDefinition definition = GetAxis(axis);
        if (definition?.Options == null)
            return result;

        for (int i = 0; i < definition.Options.Length; i++)
        {
            if (definition.Options[i] != null && !definition.Options[i].SciFiOnly)
                result.Add(i);
        }

        if (result.Count == 0)
        {
            for (int i = 0; i < definition.Options.Length; i++)
                result.Add(i);
        }
        return result;
    }

    /// <summary>이 조합에서 머리카락이 화면에 보이는지(머리 스타일에 프롭이 있는지) 판정한다.</summary>
    public bool HasVisibleHair(in AppearanceProfile profile) =>
        GetOption(AppearanceAxis.HairStyle, profile.HairStyleIndex)?.MontageProp != null;

    /// <summary>이 값을 몽타주 포트레이트로 그릴 수 있는지 판정한다(공개 축 후보 필터).</summary>
    public bool CanDepict(AppearanceAxis axis, int index)
    {
        AppearanceOption option = GetOption(axis, index);
        if (option == null)
            return false;

        if (option.ExcludeFromMontage)
            return false;

        if (axis == AppearanceAxis.HairColor || axis == AppearanceAxis.SkinColor)
            return true;

        if (option.MontageLayer != null)
            return true;

        if (axis == AppearanceAxis.HairStyle)
            return option.MontageProp == null;

        return option.MontageProp == null && !option.SciFiOnly;
    }

    /// <summary>Generic 경로용 랜덤 옵션 인덱스를 뽑는다(SciFi 전용 값 제외, 없음 확률 적용).</summary>
    public int GetRandomGenericIndex(AppearanceAxis axis)
    {
        List<int> indices = GetGenericSelectableIndices(axis);
        if (indices.Count == 0)
            return 0;

        float noneChance = HasNoneValue(axis) ? GetAxis(axis)?.NoneChance ?? 0f : 0f;
        if (noneChance > 0f && indices.Count > 1 && indices[0] == 0)
            return Random.value < noneChance ? 0 : indices[Random.Range(1, indices.Count)];

        return indices[Random.Range(0, indices.Count)];
    }

    /// <summary>0번 값이 '없음'(대머리·수염 없음 등)인 축인가 — 색 축은 0번도 실제 색이라 아니다.</summary>
    public static bool HasNoneValue(AppearanceAxis axis) =>
        axis != AppearanceAxis.HairColor && axis != AppearanceAxis.SkinColor;

    /// <summary>Generic 경로용 랜덤 프로필 — 축마다 SciFiOnly가 아닌 옵션에서만 뽑는다. 옵션이 없는 축은 0.</summary>
    public AppearanceProfile CreateRandomProfile()
    {
        AppearanceProfile profile = default;
        for (int i = 0; i < AppearanceProfile.k_axisCount; i++)
        {
            var axis = (AppearanceAxis)i;
            profile.SetIndex(axis, GetRandomGenericIndex(axis));
        }
        return profile;
    }

    /// <summary>공개 축의 특징을 현재 언어로 글 몽타주 텍스트로 조립한다.</summary>
    public string BuildMontageText(in AppearanceProfile profile, RevealedAxisSet revealedAxes)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < AppearanceProfile.k_axisCount; i++)
        {
            var axis = (AppearanceAxis)i;
            if (!IsMontageAxis(axis))
                continue;

            if (builder.Length > 0)
                builder.Append(" / ");

            AppearanceOption option = revealedAxes.Contains(axis) ? GetOption(axis, profile.GetIndex(axis)) : null;
            builder.Append(GetAxisName(axis)).Append(": ").Append(option != null ? GetOptionName(option) : UnknownValueName);
        }
        return builder.ToString();
    }

    /// <summary>몽타주가 다룰 수 있는 축인지(그릴 수 있는 값이 하나라도 있는지) 판정한다.</summary>
    public bool IsMontageAxis(AppearanceAxis axis)
    {
        AppearanceOption[] options = GetAxis(axis)?.Options;
        if (options == null)
            return false;

        for (int i = 0; i < options.Length; i++)
        {
            if (CanDepict(axis, i))
                return true;
        }
        return false;
    }
}
