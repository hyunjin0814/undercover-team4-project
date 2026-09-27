using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// NPC.controller의 Attack 상태 모션을 스윙 클립 여러 개 중 하나를 고르는 1D 블렌드 트리로 갱신한다.
/// 메뉴: Tools > NPC > Rebuild Attack Swing Variants. 다른 상태·전이는 보존한다.
/// </summary>
public static class NpcAnimatorControllerBuilder
{
    private const string k_controllerPath = "Assets/Animation/NPC.controller";
    private const string k_attackStateName = "Attack";

    private const string k_swingVariantParam = NpcAnimStates.k_swingVariantParam;

    private const string k_boxerAttackFolder =
        "Assets/Imported/Unleashed_boxer_AnimSet/Animation/Humanoid/Inplace";

    private const string k_openFolder =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Misc/Open";
    private const string k_unlockBeginState = "Unlocking_Begin";
    private const string k_unlockLoopState = "Unlocking_Loop";

    private const float k_standUpSpeed = 1f;
    private const string k_standUpClip =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Combat/HumanM@Knockdown01 - StandUp.fbx";
    private const string k_standUpState = "Stunned_StandUp";

    private const string k_subdueGroggyState = "Subdued_Groggy";
    private const string k_subdueRollState = "Subdued_Roll";

    private const string k_sitFolder =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Misc/Sit";
    private const string k_sitBeginState = "Jailed_Sit_Begin";
    private const string k_sitLoopState = "Jailed_Sit_Loop";

    private static readonly string[] s_swingClipFiles =
    {
        "attack02_inplace.fbx",
        "attack03_inplace.fbx",
        "attack04_inplace.fbx",
        "attack05_inplace.fbx",
    };

    private const string k_weaponLayerName = "WeaponUpperBody";
    private const string k_weaponPoseState = "WeaponPose_Carry";
    private const string k_weaponPoseClip =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Masked Poses/HumanM@ObjectGripShoulder01_R.fbx";
    private const string k_upperBodyMaskPath =
        "Assets/Imported/Kevin Iglesias/Human Animations/Models/Avatar Masks/Arms/Human Arm Right Mask.mask";

    /// <summary>무기 상체 레이어(상체 마스크 + 1H 자세, 가중치 0)를 다시 만든다.</summary>
    [MenuItem("Tools/NPC/Rebuild Weapon Upper-Body Layer")]
    public static void RebuildWeaponLayer()
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(k_controllerPath);
        if (controller == null)
        {
            Debug.LogError($"[NpcAnimatorControllerBuilder] 컨트롤러를 찾을 수 없음: {k_controllerPath}");
            return;
        }

        AnimationClip pose = LoadClip(k_weaponPoseClip);
        if (pose == null)
        {
            Debug.LogError($"[NpcAnimatorControllerBuilder] 무기 자세 클립을 찾을 수 없음: {k_weaponPoseClip}");
            return;
        }

        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(k_upperBodyMaskPath);
        if (mask == null)
        {
            Debug.LogError($"[NpcAnimatorControllerBuilder] 팔 마스크를 찾을 수 없음: {k_upperBodyMaskPath}");
            return;
        }

        List<AnimatorControllerLayer> layers = new List<AnimatorControllerLayer>(controller.layers);
        for (int i = layers.Count - 1; i > 0; i--)
        {
            if (layers[i].name != k_weaponLayerName)
                continue;

            if (layers[i].stateMachine != null)
                Object.DestroyImmediate(layers[i].stateMachine, true);
            layers.RemoveAt(i);
        }

        var machine = new AnimatorStateMachine
        {
            name = k_weaponLayerName,
            hideFlags = HideFlags.HideInHierarchy,
        };
        AssetDatabase.AddObjectToAsset(machine, controller);

        AnimatorState state = machine.AddState(k_weaponPoseState);
        state.motion = pose;
        state.writeDefaultValues = false;
        machine.defaultState = state;

        layers.Add(new AnimatorControllerLayer
        {
            name = k_weaponLayerName,
            stateMachine = machine,
            avatarMask = mask,
            blendingMode = AnimatorLayerBlendingMode.Override,
            defaultWeight = 0f,
            iKPass = false,
        });
        controller.layers = layers.ToArray();

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        Debug.Log(
            $"[NpcAnimatorControllerBuilder] 무기 자세 레이어 갱신 완료 — {k_weaponLayerName}"
                + $" (포즈 {k_weaponPoseState}, 마스크 {System.IO.Path.GetFileName(k_upperBodyMaskPath)}, 가중치 0)"
        );
    }

    [MenuItem("Tools/NPC/Rebuild Attack Swing Variants")]
    public static void Rebuild()
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(k_controllerPath);
        if (controller == null)
        {
            Debug.LogError(
                $"[NpcAnimatorControllerBuilder] 컨트롤러를 찾을 수 없음: {k_controllerPath}"
            );
            return;
        }

        List<AnimationClip> clips = LoadSwingClips();
        if (clips.Count == 0)
        {
            Debug.LogError("[NpcAnimatorControllerBuilder] 스윙 클립을 하나도 불러오지 못해 중단");
            return;
        }

        AnimatorState attack = FindState(controller.layers[0].stateMachine, k_attackStateName);
        if (attack == null)
        {
            Debug.LogError(
                $"[NpcAnimatorControllerBuilder] '{k_attackStateName}' 상태가 없음 — 컨트롤러 구조 확인 필요"
            );
            return;
        }

        EnsureFloatParameter(controller, k_swingVariantParam);

        if (attack.motion is BlendTree oldTree && AssetDatabase.IsSubAsset(oldTree))
        {
            Object.DestroyImmediate(oldTree, true);
        }

        var tree = new BlendTree
        {
            name = "AttackSwingVariants",
            blendType = BlendTreeType.Simple1D,
            blendParameter = k_swingVariantParam,
            useAutomaticThresholds = false,
        };
        AssetDatabase.AddObjectToAsset(tree, controller);

        for (int i = 0; i < clips.Count; i++)
        {
            tree.AddChild(clips[i], i);
        }

        attack.motion = tree;

        SetupUnlockStates(controller);
        SetupStandUpState(controller);
        RemoveSubdueStates(controller.layers[0].stateMachine);
        SetupSitStates(controller);

        EditorUtility.SetDirty(tree);
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        Debug.Log(
            $"[NpcAnimatorControllerBuilder] 갱신 완료: {k_controllerPath} "
                + $"— Attack 상태에 단발 스윙 {clips.Count}종 블렌드 트리 연결 "
                + $"(파라미터 {k_swingVariantParam}, 임계값 0~{clips.Count - 1})"
        );

        Selection.activeObject = controller;
    }

    /// <summary>자물쇠 해제 상태(Begin 1회 → Loop 반복)와 Any State 전이를 다시 구성한다.</summary>
    private static void SetupUnlockStates(AnimatorController controller)
    {
        AnimationClip begin = LoadClip($"{k_openFolder}/HumanM@Opening01 - Begin.fbx");
        AnimationClip loop = LoadClip($"{k_openFolder}/HumanM@Opening01 - Loop.fbx");
        if (begin == null || loop == null)
        {
            Debug.LogError(
                "[NpcAnimatorControllerBuilder] 자물쇠 해제 클립을 불러오지 못해 해제 상태 구성을 건너뜀"
            );
            return;
        }

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        RemoveUnlockStates(stateMachine);

        AnimatorState beginState = stateMachine.AddState(k_unlockBeginState);
        beginState.motion = begin;
        AnimatorState loopState = stateMachine.AddState(k_unlockLoopState);
        loopState.motion = loop;

        AnimatorStateTransition toBegin = stateMachine.AddAnyStateTransition(beginState);
        toBegin.hasExitTime = false;
        toBegin.duration = 0.1f;
        toBegin.canTransitionToSelf = false;
        toBegin.AddCondition(
            AnimatorConditionMode.Equals,
            NpcAnimStates.k_unlockingBegin,
            "State"
        );

        AnimatorStateTransition toLoop = stateMachine.AddAnyStateTransition(loopState);
        toLoop.hasExitTime = false;
        toLoop.duration = 0.1f;
        toLoop.canTransitionToSelf = false;
        toLoop.AddCondition(
            AnimatorConditionMode.Equals,
            NpcAnimStates.k_unlockingLoop,
            "State"
        );
    }

    /// <summary>기절 해제 후 일어나는 StandUp 상태와 Any State 전이를 다시 구성한다.</summary>
    private static void SetupStandUpState(AnimatorController controller)
    {
        AnimationClip standUp = LoadClip(k_standUpClip);
        if (standUp == null)
        {
            Debug.LogError(
                "[NpcAnimatorControllerBuilder] 일어나기 클립을 불러오지 못해 StandUp 상태 구성을 건너뜀"
            );
            return;
        }

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        RemoveStandUpState(stateMachine);

        AnimatorState state = stateMachine.AddState(k_standUpState);
        state.motion = standUp;
        state.speed = k_standUpSpeed;

        AnimatorStateTransition toStandUp = stateMachine.AddAnyStateTransition(state);
        toStandUp.hasExitTime = false;
        toStandUp.duration = 0.1f;
        toStandUp.canTransitionToSelf = false;
        toStandUp.AddCondition(
            AnimatorConditionMode.Equals,
            NpcAnimStates.k_standUp,
            "State"
        );
    }

    /// <summary>유치장 착석 상태(Begin 1회 → Loop 반복)와 Any State 전이를 다시 구성한다.</summary>
    private static void SetupSitStates(AnimatorController controller)
    {
        AnimationClip begin = LoadClip($"{k_sitFolder}/HumanM@SitMedium01 - Begin.fbx");
        AnimationClip loop = LoadClip($"{k_sitFolder}/HumanM@SitMedium01 - Loop.fbx");
        if (begin == null || loop == null)
        {
            Debug.LogError(
                "[NpcAnimatorControllerBuilder] 착석 클립을 불러오지 못해 착석 상태 구성을 건너뜀"
            );
            return;
        }

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        RemoveSitStates(stateMachine);

        AnimatorState beginState = stateMachine.AddState(k_sitBeginState);
        beginState.motion = begin;
        AnimatorState loopState = stateMachine.AddState(k_sitLoopState);
        loopState.motion = loop;

        AnimatorStateTransition toBegin = stateMachine.AddAnyStateTransition(beginState);
        toBegin.hasExitTime = false;
        toBegin.duration = 0.2f;
        toBegin.canTransitionToSelf = false;
        toBegin.AddCondition(
            AnimatorConditionMode.Equals,
            NpcAnimStates.k_sitBegin,
            "State"
        );

        AnimatorStateTransition toLoop = stateMachine.AddAnyStateTransition(loopState);
        toLoop.hasExitTime = false;
        toLoop.duration = 0.1f;
        toLoop.canTransitionToSelf = false;
        toLoop.AddCondition(
            AnimatorConditionMode.Equals,
            NpcAnimStates.k_sitLoop,
            "State"
        );
    }

    /// <summary>이전 실행이 만든 착석 상태와 그 상태를 가리키는 Any State 전이를 제거한다.</summary>
    private static void RemoveSitStates(AnimatorStateMachine stateMachine)
    {
        var staleTransitions = new List<AnimatorStateTransition>();
        foreach (AnimatorStateTransition transition in stateMachine.anyStateTransitions)
        {
            if (transition.destinationState != null && IsSitState(transition.destinationState.name))
                staleTransitions.Add(transition);
        }
        foreach (AnimatorStateTransition transition in staleTransitions)
        {
            stateMachine.RemoveAnyStateTransition(transition);
        }

        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (IsSitState(child.state.name))
                stateMachine.RemoveState(child.state);
        }
    }

    private static bool IsSitState(string name) =>
        name == k_sitBeginState || name == k_sitLoopState;

    /// <summary>이전 실행이 만든 제압 전환 상태와 그 상태를 가리키는 Any State 전이를 제거한다.</summary>
    private static void RemoveSubdueStates(AnimatorStateMachine stateMachine)
    {
        var staleTransitions = new List<AnimatorStateTransition>();
        foreach (AnimatorStateTransition transition in stateMachine.anyStateTransitions)
        {
            if (
                transition.destinationState != null
                && IsSubdueState(transition.destinationState.name)
            )
                staleTransitions.Add(transition);
        }
        foreach (AnimatorStateTransition transition in staleTransitions)
        {
            stateMachine.RemoveAnyStateTransition(transition);
        }

        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (IsSubdueState(child.state.name))
                stateMachine.RemoveState(child.state);
        }
    }

    private static bool IsSubdueState(string name) =>
        name == k_subdueGroggyState || name == k_subdueRollState;

    /// <summary>이전 실행이 만든 일어나기 상태와 그 상태를 가리키는 Any State 전이를 제거한다.</summary>
    private static void RemoveStandUpState(AnimatorStateMachine stateMachine)
    {
        var staleTransitions = new List<AnimatorStateTransition>();
        foreach (AnimatorStateTransition transition in stateMachine.anyStateTransitions)
        {
            if (
                transition.destinationState != null
                && transition.destinationState.name == k_standUpState
            )
                staleTransitions.Add(transition);
        }
        foreach (AnimatorStateTransition transition in staleTransitions)
        {
            stateMachine.RemoveAnyStateTransition(transition);
        }

        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (child.state.name == k_standUpState)
                stateMachine.RemoveState(child.state);
        }
    }

    /// <summary>이전 실행이 만든 해제 상태와 그 상태를 가리키는 Any State 전이를 제거한다.</summary>
    private static void RemoveUnlockStates(AnimatorStateMachine stateMachine)
    {
        var staleTransitions = new List<AnimatorStateTransition>();
        foreach (AnimatorStateTransition transition in stateMachine.anyStateTransitions)
        {
            if (
                transition.destinationState != null
                && IsUnlockState(transition.destinationState.name)
            )
            {
                staleTransitions.Add(transition);
            }
        }
        foreach (AnimatorStateTransition transition in staleTransitions)
        {
            stateMachine.RemoveAnyStateTransition(transition);
        }

        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (IsUnlockState(child.state.name))
            {
                stateMachine.RemoveState(child.state);
            }
        }
    }

    private static bool IsUnlockState(string name) =>
        name == k_unlockBeginState || name == k_unlockLoopState;

    /// <summary>스윙 클립을 순서대로 불러온다. 없는 파일은 경고만 남기고 건너뛴다(일부 누락돼도 나머지로 동작).</summary>
    private static List<AnimationClip> LoadSwingClips()
    {
        var clips = new List<AnimationClip>(s_swingClipFiles.Length);
        foreach (string file in s_swingClipFiles)
        {
            AnimationClip clip = LoadClip($"{k_boxerAttackFolder}/{file}");
            if (clip != null)
            {
                clips.Add(clip);
            }
        }
        return clips;
    }

    private static void EnsureFloatParameter(AnimatorController controller, string name)
    {
        foreach (AnimatorControllerParameter parameter in controller.parameters)
        {
            if (parameter.name == name)
                return;
        }

        controller.AddParameter(name, AnimatorControllerParameterType.Float);
    }

    private static AnimatorState FindState(AnimatorStateMachine stateMachine, string name)
    {
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (child.state.name == name)
                return child.state;
        }
        return null;
    }

    private static AnimationClip LoadClip(string fbxPath)
    {
        foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
        {
            if (asset is AnimationClip clip && !clip.name.StartsWith("__preview__"))
            {
                return clip;
            }
        }

        Debug.LogWarning($"[NpcAnimatorControllerBuilder] 클립을 찾을 수 없음(건너뜀): {fbxPath}");
        return null;
    }
}
