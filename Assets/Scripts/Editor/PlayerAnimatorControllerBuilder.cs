using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// 플레이어 AnimatorController(MoveX/MoveZ 2D 블렌드 트리와 각 상태)를 생성·갱신한다.
/// 메뉴: Tools > Player > Create Animator Controller. 수동으로 추가한 상태·전환은 보존한다.
/// </summary>
public static class PlayerAnimatorControllerBuilder
{
    private const string k_outputPath = "Assets/Animation/Player.controller";
    private const string k_stateName = "Locomotion";

    private const string k_idleFbx =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Idles/HumanM@Idle01.fbx";
    private const string k_walkFolder =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Movement/Walk";
    private const string k_runFolder =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Movement/Run";

    private const string k_downParam = "Down";
    private const string k_fallState = "Knockdown_Fall";
    private const string k_groundState = "Knockdown_Ground";
    private const string k_standUpState = "Knockdown_StandUp";
    private const string k_combatFolder =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Combat";

    private const string k_emoteParam = "Emote";
    private const string k_emoteIndexParam = "EmoteIndex";
    private const string k_emoteStatePrefix = "Emote_";
    private const string k_emoteCatalogPath = "Assets/Settings/Emote/EmoteCatalog.asset";

    private const string k_stunParam = "Stunned";
    private const string k_stunState = "Stun";

    private const string k_crouchParam = "Crouch";
    private const string k_crouchState = "Crouch";
    private const string k_crouchFolder =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Movement/Crouch";

    private const string k_airborneParam = "Airborne";
    private const string k_jumpBeginState = "Jump_Begin";
    private const string k_jumpAirState = "Jump_Air";
    private const string k_jumpFolder =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Movement/Jump";

    private const float k_landBlend = 0.1f;

    private const string k_jumpAirCrouchState = "Jump_Air_Crouch";

    private const string k_legacyJumpLandState = "Jump_Land";

    private const string k_revivingParam = "Reviving";
    private const string k_revivingBeginState = "Reviving_Begin";
    private const string k_revivingLoopState = "Reviving_Loop";
    private const string k_revivingFolder =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Misc/Open";

    private const string k_attackParam = "Attack";
    private const string k_attackLayer = "UpperBodyAttack";
    private const string k_attackEmptyState = "None";
    private const string k_attackState = "Attack_Baton";
    private const string k_attackMaskPath = "Assets/Animation/UpperBodyAttack.mask";

    private const string k_attackClip =
        "Assets/Imported/Kevin Iglesias/Human Animations/Animations/Male/Combat/1H/HumanM@Attack1H01_R.fbx";

    private const float k_attackBlend = 0.08f;

    private const float k_attackClipImpactNormalized = 0.3f;

    private static readonly AvatarMaskBodyPart[] s_attackMaskParts =
    {
        AvatarMaskBodyPart.Body,
        AvatarMaskBodyPart.Head,
        AvatarMaskBodyPart.LeftArm,
        AvatarMaskBodyPart.RightArm,
        AvatarMaskBodyPart.LeftFingers,
        AvatarMaskBodyPart.RightFingers,
    };

    private static readonly (string suffix, Vector2 dir)[] s_directions =
    {
        ("Forward", new Vector2(0f, 1f)),
        ("ForwardRight", new Vector2(0.7071f, 0.7071f)),
        ("Right", new Vector2(1f, 0f)),
        ("BackwardRight", new Vector2(0.7071f, -0.7071f)),
        ("Backward", new Vector2(0f, -1f)),
        ("BackwardLeft", new Vector2(-0.7071f, -0.7071f)),
        ("Left", new Vector2(-1f, 0f)),
        ("ForwardLeft", new Vector2(-0.7071f, 0.7071f)),
    };

    [MenuItem("Tools/Player/Create Animator Controller")]
    public static void Create()
    {
        AnimationClip idle = LoadClip(k_idleFbx);
        if (idle == null)
            return;

        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(k_outputPath);
        bool isNew = controller == null;
        if (isNew)
        {
            controller = AnimatorController.CreateAnimatorControllerAtPath(k_outputPath);
        }

        EnsureFloatParameter(controller, "MoveX");
        EnsureFloatParameter(controller, "MoveZ");

        BlendTree blendTree = FindOrCreateBlendTree(controller, k_stateName);
        blendTree.children = new ChildMotion[0];

        blendTree.AddChild(idle, Vector2.zero);

        foreach ((string suffix, Vector2 dir) in s_directions)
        {
            AnimationClip walk = LoadClip($"{k_walkFolder}/HumanM@Walk01_{suffix}.fbx");
            AnimationClip run = LoadClip($"{k_runFolder}/HumanM@Run01_{suffix}.fbx");
            if (walk == null || run == null)
                return;

            blendTree.AddChild(walk, dir * PlayerAnimationDriver.k_walkParam);
            blendTree.AddChild(run, dir * PlayerAnimationDriver.k_runParam);
        }

        BlendTree crouchTree = SetupCrouchState(controller);
        BlendTree airCrouchTree = SetupJumpStates(controller);
        SetupDownStates(controller);
        SetupRevivingState(controller);
        SetupEmoteStates(controller);
        RemoveLegacyStunStates(controller);
        SetupAttackLayer(controller);

        EditorUtility.SetDirty(blendTree);
        if (crouchTree != null)
        {
            EditorUtility.SetDirty(crouchTree);
        }
        if (airCrouchTree != null)
        {
            EditorUtility.SetDirty(airCrouchTree);
        }
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();

        Debug.Log(
            $"[PlayerAnimatorControllerBuilder] {(isNew ? "생성" : "갱신")} 완료: {k_outputPath} "
                + $"(Idle 중앙 / Walk 반경 {PlayerAnimationDriver.k_walkParam} 8방향 / "
                + $"Run 반경 {PlayerAnimationDriver.k_runParam} 8방향, 총 17모션 + 다운 3상태(기절 공용) "
                + $"+ 앉기 9모션 + 점프 3상태(공중 웅크림 9모션) + 타격 상체 레이어 1 + 구조 채널링 2상태)"
        );

        Selection.activeObject = controller;
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

    private static void EnsureBoolParameter(AnimatorController controller, string name)
    {
        foreach (AnimatorControllerParameter parameter in controller.parameters)
        {
            if (parameter.name == name)
                return;
        }

        controller.AddParameter(name, AnimatorControllerParameterType.Bool);
    }

    private static void EnsureIntParameter(AnimatorController controller, string name)
    {
        foreach (AnimatorControllerParameter parameter in controller.parameters)
        {
            if (parameter.name == name)
                return;
        }

        controller.AddParameter(name, AnimatorControllerParameterType.Int);
    }

    private static void EnsureTriggerParameter(AnimatorController controller, string name)
    {
        foreach (AnimatorControllerParameter parameter in controller.parameters)
        {
            if (parameter.name == name)
                return;
        }

        controller.AddParameter(name, AnimatorControllerParameterType.Trigger);
    }

    /// <summary>상체 마스크를 쓰는 타격 레이어(레이어 1)를 추가·갱신한다.</summary>
    private static void SetupAttackLayer(AnimatorController controller)
    {
        AnimationClip swing = LoadClip(k_attackClip);
        if (swing == null)
            return;

        EnsureTriggerParameter(controller, k_attackParam);

        AvatarMask mask = CreateOrUpdateAttackMask();
        AnimatorStateMachine stateMachine = FindOrCreateAttackLayer(controller, mask);

        foreach (ChildAnimatorState child in stateMachine.states)
        {
            stateMachine.RemoveState(child.state);
        }

        AnimatorState empty = stateMachine.AddState(k_attackEmptyState);
        empty.motion = null;
        stateMachine.defaultState = empty;

        AnimatorState attack = stateMachine.AddState(k_attackState);
        attack.motion = swing;

        float naturalImpact = k_attackClipImpactNormalized * swing.length;
        attack.speed = naturalImpact / PlayerAnimationDriver.k_swingImpactSeconds;

        float exitTime = PlayerAnimationDriver.k_swingSeconds * attack.speed / swing.length;
        if (exitTime > 1f)
        {
            Debug.LogWarning(
                $"[PlayerAnimatorControllerBuilder] 스윙 클립이 목표 길이보다 짧다 "
                    + $"(필요 exitTime={exitTime:F2}). 1.0으로 자른다 — 1인칭 스윙이 3인칭보다 늦게 끝난다."
            );
            exitTime = 1f;
        }

        AnimatorStateTransition toAttack = empty.AddTransition(attack);
        toAttack.hasExitTime = false;
        toAttack.hasFixedDuration = true;
        toAttack.duration = k_attackBlend;
        toAttack.AddCondition(AnimatorConditionMode.If, 0f, k_attackParam);

        AnimatorStateTransition toEmpty = attack.AddTransition(empty);
        toEmpty.hasExitTime = true;
        toEmpty.exitTime = exitTime;
        toEmpty.hasFixedDuration = true;
        toEmpty.duration = k_attackBlend;

        EditorUtility.SetDirty(stateMachine);
    }

    /// <summary>타격 레이어용 상체 아바타 마스크를 만들거나 부위 구성을 다시 맞춘다.</summary>
    private static AvatarMask CreateOrUpdateAttackMask()
    {
        AvatarMask mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(k_attackMaskPath);
        bool isNew = mask == null;
        if (isNew)
        {
            mask = new AvatarMask();
        }

        foreach (AvatarMaskBodyPart part in System.Enum.GetValues(typeof(AvatarMaskBodyPart)))
        {
            if (part == AvatarMaskBodyPart.LastBodyPart)
                continue;

            mask.SetHumanoidBodyPartActive(
                part,
                System.Array.IndexOf(s_attackMaskParts, part) >= 0
            );
        }

        if (isNew)
        {
            AssetDatabase.CreateAsset(mask, k_attackMaskPath);
        }
        else
        {
            EditorUtility.SetDirty(mask);
        }

        return mask;
    }

    /// <summary>타격 레이어를 찾거나 만들고 마스크·블렌딩·가중치를 맞춰 돌려준다.</summary>
    private static AnimatorStateMachine FindOrCreateAttackLayer(
        AnimatorController controller,
        AvatarMask mask
    )
    {
        bool exists = false;
        foreach (AnimatorControllerLayer existing in controller.layers)
        {
            if (existing.name == k_attackLayer)
            {
                exists = true;
                break;
            }
        }

        if (!exists)
        {
            controller.AddLayer(k_attackLayer);
        }

        AnimatorControllerLayer[] layers = controller.layers;
        AnimatorStateMachine stateMachine = null;
        foreach (AnimatorControllerLayer layer in layers)
        {
            if (layer.name != k_attackLayer)
                continue;

            layer.avatarMask = mask;
            layer.blendingMode = AnimatorLayerBlendingMode.Override;
            layer.defaultWeight = 1f;
            stateMachine = layer.stateMachine;
        }

        controller.layers = layers;
        return stateMachine;
    }

    /// <summary>앉기 블렌드 트리 상태와 Locomotion↔Crouch 전환을 구성한다.</summary>
    private static BlendTree SetupCrouchState(AnimatorController controller)
    {
        EnsureBoolParameter(controller, k_crouchParam);

        BlendTree crouchTree = FindOrCreateBlendTree(controller, k_crouchState);
        if (!FillCrouchBlendTree(crouchTree))
            return null;

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        AnimatorState locomotion = FindState(stateMachine, k_stateName);
        AnimatorState crouch = FindState(stateMachine, k_crouchState);
        if (locomotion == null || crouch == null)
            return crouchTree;

        RemoveTransitionsBetween(locomotion, crouch);

        AnimatorStateTransition toCrouch = locomotion.AddTransition(crouch);
        toCrouch.hasExitTime = false;
        toCrouch.duration = PlayerCrouch.k_blendDuration;
        toCrouch.AddCondition(AnimatorConditionMode.If, 0f, k_crouchParam);

        AnimatorStateTransition toStand = crouch.AddTransition(locomotion);
        toStand.hasExitTime = false;
        toStand.duration = PlayerCrouch.k_blendDuration;
        toStand.AddCondition(AnimatorConditionMode.IfNot, 0f, k_crouchParam);

        return crouchTree;
    }

    /// <summary>블렌드 트리에 앉기 클립(중앙 Idle + 8방향 CrouchWalk)을 채운다. 클립이 없으면 false.</summary>
    private static bool FillCrouchBlendTree(BlendTree tree)
    {
        AnimationClip crouchIdle = LoadClip($"{k_crouchFolder}/HumanM@Crouch01_Idle.fbx");
        if (crouchIdle == null)
            return false;

        tree.children = new ChildMotion[0];
        tree.AddChild(crouchIdle, Vector2.zero);

        foreach ((string suffix, Vector2 dir) in s_directions)
        {
            AnimationClip crouchWalk = LoadClip(
                $"{k_crouchFolder}/CrouchWalk/HumanM@Crouch01_Walk_{suffix}.fbx"
            );
            if (crouchWalk == null)
                return false;

            tree.AddChild(crouchWalk, dir * PlayerAnimationDriver.k_walkParam);
        }

        return true;
    }

    private static void RemoveTransitionsBetween(AnimatorState a, AnimatorState b)
    {
        RemoveTransitionsTo(a, b);
        RemoveTransitionsTo(b, a);
    }

    private static void RemoveTransitionsTo(AnimatorState from, AnimatorState destination)
    {
        var toRemove = new System.Collections.Generic.List<AnimatorStateTransition>();
        foreach (AnimatorStateTransition transition in from.transitions)
        {
            if (transition.destinationState == destination)
            {
                toRemove.Add(transition);
            }
        }
        foreach (AnimatorStateTransition transition in toRemove)
        {
            from.RemoveTransition(transition);
        }
    }

    /// <summary>점프 상태 머신(이륙 → 체공 루프 → 착지 블렌드)을 구성하고 공중 웅크림 트리를 돌려준다.</summary>
    private static BlendTree SetupJumpStates(AnimatorController controller)
    {
        AnimationClip begin = LoadClip($"{k_jumpFolder}/HumanM@Jump01 - Begin.fbx");
        AnimationClip air = LoadClip($"{k_jumpFolder}/HumanM@Fall01.fbx");
        if (begin == null || air == null)
            return null;

        EnsureBoolParameter(controller, k_airborneParam);

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        RemoveJumpStates(stateMachine);

        AnimatorState locomotion = FindState(stateMachine, k_stateName);
        AnimatorState crouch = FindState(stateMachine, k_crouchState);

        AnimatorState beginState = stateMachine.AddState(k_jumpBeginState);
        beginState.motion = begin;
        AnimatorState airState = stateMachine.AddState(k_jumpAirState);
        airState.motion = air;

        BlendTree airCrouchTree = FindOrCreateBlendTree(controller, k_jumpAirCrouchState);
        if (!FillCrouchBlendTree(airCrouchTree))
            return null;

        AnimatorState airCrouchState = FindState(stateMachine, k_jumpAirCrouchState);

        if (crouch != null)
        {
            AnimatorStateTransition crouchToTuck = crouch.AddTransition(airCrouchState);
            crouchToTuck.hasExitTime = false;
            crouchToTuck.duration = 0.05f;
            crouchToTuck.AddCondition(AnimatorConditionMode.If, 0f, k_airborneParam);
            crouchToTuck.AddCondition(AnimatorConditionMode.If, 0f, k_crouchParam);
        }

        foreach (AnimatorState source in new[] { locomotion, crouch })
        {
            if (source == null)
                continue;

            AnimatorStateTransition toBegin = source.AddTransition(beginState);
            toBegin.hasExitTime = false;
            toBegin.duration = 0.05f;
            toBegin.AddCondition(AnimatorConditionMode.If, 0f, k_airborneParam);
        }

        foreach (AnimatorState source in new[] { beginState, airState, airCrouchState })
        {
            if (crouch != null)
            {
                AnimatorStateTransition toCrouch = source.AddTransition(crouch);
                toCrouch.hasExitTime = false;
                toCrouch.duration = k_landBlend;
                toCrouch.AddCondition(AnimatorConditionMode.IfNot, 0f, k_airborneParam);
                toCrouch.AddCondition(AnimatorConditionMode.If, 0f, k_crouchParam);
            }

            if (locomotion != null)
            {
                AnimatorStateTransition toLocomotion = source.AddTransition(locomotion);
                toLocomotion.hasExitTime = false;
                toLocomotion.duration = k_landBlend;
                toLocomotion.AddCondition(AnimatorConditionMode.IfNot, 0f, k_airborneParam);
                toLocomotion.AddCondition(AnimatorConditionMode.IfNot, 0f, k_crouchParam);
            }
        }

        foreach (AnimatorState source in new[] { beginState, airState })
        {
            AnimatorStateTransition toTuck = source.AddTransition(airCrouchState);
            toTuck.hasExitTime = false;
            toTuck.duration = 0.1f;
            toTuck.AddCondition(AnimatorConditionMode.If, 0f, k_crouchParam);
        }

        AnimatorStateTransition tuckToAir = airCrouchState.AddTransition(airState);
        tuckToAir.hasExitTime = false;
        tuckToAir.duration = 0.1f;
        tuckToAir.AddCondition(AnimatorConditionMode.IfNot, 0f, k_crouchParam);

        AnimatorStateTransition toAir = beginState.AddTransition(airState);
        toAir.hasExitTime = true;
        toAir.exitTime = 0.8f;
        toAir.duration = 0.1f;

        return airCrouchTree;
    }

    private static void RemoveJumpStates(AnimatorStateMachine stateMachine)
    {
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            var toRemove = new System.Collections.Generic.List<AnimatorStateTransition>();
            foreach (AnimatorStateTransition transition in child.state.transitions)
            {
                if (
                    transition.destinationState != null
                    && IsJumpState(transition.destinationState.name)
                )
                {
                    toRemove.Add(transition);
                }
            }
            foreach (AnimatorStateTransition transition in toRemove)
            {
                child.state.RemoveTransition(transition);
            }
        }

        AnimatorState airCrouch = FindState(stateMachine, k_jumpAirCrouchState);
        if (airCrouch != null)
        {
            foreach (AnimatorStateTransition transition in airCrouch.transitions)
            {
                airCrouch.RemoveTransition(transition);
            }
        }

        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (IsRemovableJumpState(child.state.name))
            {
                stateMachine.RemoveState(child.state);
            }
        }
    }

    private static bool IsJumpState(string name)
    {
        return name == k_jumpBeginState
            || name == k_jumpAirState
            || name == k_jumpAirCrouchState
            || name == k_legacyJumpLandState;
    }

    private static bool IsRemovableJumpState(string name)
    {
        return name != k_jumpAirCrouchState && IsJumpState(name);
    }

    /// <summary>다운 상태 머신(쓰러짐 → 바닥 대기 → 기상)을 구성한다.</summary>
    private static void SetupDownStates(AnimatorController controller)
    {
        AnimationClip fall = LoadClip($"{k_combatFolder}/HumanM@Knockdown01 - Fall.fbx");
        AnimationClip ground = LoadClip($"{k_combatFolder}/HumanM@Knockdown01 - Ground.fbx");
        AnimationClip standUp = LoadClip($"{k_combatFolder}/HumanM@Knockdown01 - StandUp.fbx");
        if (fall == null || ground == null || standUp == null)
            return;

        EnsureBoolParameter(controller, k_downParam);

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        RemoveDownStates(stateMachine);

        AnimatorState locomotion = FindState(stateMachine, k_stateName);
        AnimatorState fallState = stateMachine.AddState(k_fallState);
        fallState.motion = fall;
        AnimatorState groundState = stateMachine.AddState(k_groundState);
        groundState.motion = ground;
        AnimatorState standUpState = stateMachine.AddState(k_standUpState);
        standUpState.motion = standUp;

        AnimatorState[] sources =
        {
            locomotion,
            FindState(stateMachine, k_crouchState),
            FindState(stateMachine, k_jumpBeginState),
            FindState(stateMachine, k_jumpAirState),
            FindState(stateMachine, k_jumpAirCrouchState),
        };
        foreach (AnimatorState source in sources)
        {
            if (source == null)
                continue;

            AnimatorStateTransition toFall = source.AddTransition(fallState);
            toFall.hasExitTime = false;
            toFall.duration = 0.1f;
            toFall.AddCondition(AnimatorConditionMode.If, 0f, k_downParam);
        }

        AnimatorStateTransition toGround = fallState.AddTransition(groundState);
        toGround.hasExitTime = true;
        toGround.exitTime = 0.9f;
        toGround.duration = 0.1f;

        AnimatorStateTransition toStandUp = groundState.AddTransition(standUpState);
        toStandUp.hasExitTime = false;
        toStandUp.duration = 0.1f;
        toStandUp.AddCondition(AnimatorConditionMode.IfNot, 0f, k_downParam);

        if (locomotion != null)
        {
            AnimatorStateTransition toLocomotion = standUpState.AddTransition(locomotion);
            toLocomotion.hasExitTime = true;
            toLocomotion.exitTime = 0.9f;
            toLocomotion.duration = 0.1f;
        }
    }

    /// <summary>구조 채널링 모션 상태(Begin → Loop)와 복귀 전환을 구성한다.</summary>
    private static void SetupRevivingState(AnimatorController controller)
    {
        AnimationClip begin = LoadClip($"{k_revivingFolder}/HumanM@Opening01 - Begin.fbx");
        AnimationClip loop = LoadClip($"{k_revivingFolder}/HumanM@Opening01 - Loop.fbx");
        if (begin == null || loop == null)
            return;

        EnsureBoolParameter(controller, k_revivingParam);

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        RemoveRevivingState(stateMachine);

        AnimatorState locomotion = FindState(stateMachine, k_stateName);
        AnimatorState beginState = stateMachine.AddState(k_revivingBeginState);
        beginState.motion = begin;
        AnimatorState loopState = stateMachine.AddState(k_revivingLoopState);
        loopState.motion = loop;

        if (locomotion == null)
            return;

        AnimatorStateTransition toBegin = locomotion.AddTransition(beginState);
        toBegin.hasExitTime = false;
        toBegin.duration = 0.15f;
        toBegin.AddCondition(AnimatorConditionMode.If, 0f, k_revivingParam);

        AnimatorStateTransition toLoop = beginState.AddTransition(loopState);
        toLoop.hasExitTime = true;
        toLoop.exitTime = 0.9f;
        toLoop.duration = 0.1f;

        foreach (AnimatorState source in new[] { beginState, loopState })
        {
            AnimatorStateTransition toLocomotion = source.AddTransition(locomotion);
            toLocomotion.hasExitTime = false;
            toLocomotion.duration = 0.15f;
            toLocomotion.AddCondition(AnimatorConditionMode.IfNot, 0f, k_revivingParam);
        }
    }

    private static void RemoveRevivingState(AnimatorStateMachine stateMachine)
    {
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            var toRemove = new System.Collections.Generic.List<AnimatorStateTransition>();
            foreach (AnimatorStateTransition transition in child.state.transitions)
            {
                if (
                    transition.destinationState != null
                    && IsRevivingState(transition.destinationState.name)
                )
                {
                    toRemove.Add(transition);
                }
            }
            foreach (AnimatorStateTransition transition in toRemove)
            {
                child.state.RemoveTransition(transition);
            }
        }

        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (IsRevivingState(child.state.name))
            {
                stateMachine.RemoveState(child.state);
            }
        }
    }

    private static bool IsRevivingState(string name) =>
        name == k_revivingBeginState || name == k_revivingLoopState;

    /// <summary>감정표현 상태를 카탈로그 순서대로 만든다. 다운 상태 머신 구성 이후에 호출해야 한다.</summary>
    private static void SetupEmoteStates(AnimatorController controller)
    {
        var catalog = AssetDatabase.LoadAssetAtPath<EmoteCatalog>(k_emoteCatalogPath);
        if (catalog == null)
        {
            Debug.LogWarning(
                $"[PlayerAnimatorControllerBuilder] 감정표현 카탈로그가 없어 건너뜁니다: {k_emoteCatalogPath}"
            );
            return;
        }

        EnsureBoolParameter(controller, k_emoteParam);
        EnsureIntParameter(controller, k_emoteIndexParam);

        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;
        RemoveEmoteStates(stateMachine);

        AnimatorState locomotion = FindState(stateMachine, k_stateName);
        AnimatorState fallState = FindState(stateMachine, k_fallState);
        AnimatorState jumpBegin = FindState(stateMachine, k_jumpBeginState);

        int created = 0;
        for (int index = 0; index < catalog.Count; index++)
        {
            EmoteDefinition definition = catalog.Get(index);
            if (definition == null || definition.Clip == null)
                continue;

            AnimatorState state = stateMachine.AddState($"{k_emoteStatePrefix}{index:00}");
            state.motion = definition.Clip;
            created++;

            if (locomotion != null)
            {
                AnimatorStateTransition enter = locomotion.AddTransition(state);
                enter.hasExitTime = false;
                enter.duration = 0.15f;
                enter.AddCondition(AnimatorConditionMode.If, 0f, k_emoteParam);
                enter.AddCondition(AnimatorConditionMode.Equals, index, k_emoteIndexParam);

                AnimatorStateTransition exit = state.AddTransition(locomotion);
                exit.hasExitTime = false;
                exit.duration = 0.15f;
                exit.AddCondition(AnimatorConditionMode.IfNot, 0f, k_emoteParam);
            }

            if (fallState != null)
            {
                AnimatorStateTransition toFall = state.AddTransition(fallState);
                toFall.hasExitTime = false;
                toFall.duration = 0.1f;
                toFall.AddCondition(AnimatorConditionMode.If, 0f, k_downParam);
            }

            if (jumpBegin != null)
            {
                AnimatorStateTransition toJump = state.AddTransition(jumpBegin);
                toJump.hasExitTime = false;
                toJump.duration = 0.1f;
                toJump.AddCondition(AnimatorConditionMode.If, 0f, k_airborneParam);
            }
        }

        Debug.Log($"[PlayerAnimatorControllerBuilder] 감정표현 상태 {created}개 생성");
    }

    private static void RemoveEmoteStates(AnimatorStateMachine stateMachine)
    {
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            var toRemove = new System.Collections.Generic.List<AnimatorStateTransition>();
            foreach (AnimatorStateTransition transition in child.state.transitions)
            {
                if (
                    transition.destinationState != null
                    && transition.destinationState.name.StartsWith(k_emoteStatePrefix)
                )
                {
                    toRemove.Add(transition);
                }
            }
            foreach (AnimatorStateTransition transition in toRemove)
            {
                child.state.RemoveTransition(transition);
            }
        }

        var statesToRemove = new System.Collections.Generic.List<AnimatorState>();
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (child.state != null && child.state.name.StartsWith(k_emoteStatePrefix))
                statesToRemove.Add(child.state);
        }
        foreach (AnimatorState state in statesToRemove)
        {
            stateMachine.RemoveState(state);
        }
    }

    private static void RemoveLegacyStunStates(AnimatorController controller)
    {
        RemoveStunStates(controller.layers[0].stateMachine);

        foreach (AnimatorControllerParameter parameter in controller.parameters)
        {
            if (parameter.name == k_stunParam)
            {
                controller.RemoveParameter(parameter);
                return;
            }
        }
    }

    private static void RemoveStunStates(AnimatorStateMachine stateMachine)
    {
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            var toRemove = new System.Collections.Generic.List<AnimatorStateTransition>();
            foreach (AnimatorStateTransition transition in child.state.transitions)
            {
                if (
                    transition.destinationState != null
                    && transition.destinationState.name == k_stunState
                )
                {
                    toRemove.Add(transition);
                }
            }
            foreach (AnimatorStateTransition transition in toRemove)
            {
                child.state.RemoveTransition(transition);
            }
        }

        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (child.state.name == k_stunState)
            {
                stateMachine.RemoveState(child.state);
                return;
            }
        }
    }

    private static void RemoveDownStates(AnimatorStateMachine stateMachine)
    {
        foreach (ChildAnimatorState child in stateMachine.states)
        {
            var toRemove = new System.Collections.Generic.List<AnimatorStateTransition>();
            foreach (AnimatorStateTransition transition in child.state.transitions)
            {
                if (
                    transition.destinationState != null
                    && IsDownState(transition.destinationState.name)
                )
                {
                    toRemove.Add(transition);
                }
            }
            foreach (AnimatorStateTransition transition in toRemove)
            {
                child.state.RemoveTransition(transition);
            }
        }

        foreach (ChildAnimatorState child in stateMachine.states)
        {
            if (IsDownState(child.state.name))
            {
                stateMachine.RemoveState(child.state);
            }
        }
    }

    private static bool IsDownState(string name)
    {
        return name == k_fallState || name == k_groundState || name == k_standUpState;
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

    /// <summary>지정 상태의 MoveX/MoveZ 블렌드 트리를 찾고, 없으면 상태와 함께 만든다.</summary>
    private static BlendTree FindOrCreateBlendTree(AnimatorController controller, string stateName)
    {
        AnimatorStateMachine stateMachine = controller.layers[0].stateMachine;

        AnimatorState state = FindState(stateMachine, stateName);

        if (state == null)
        {
            state = stateMachine.AddState(stateName);
        }

        if (state.motion is BlendTree existing)
        {
            existing.blendType = BlendTreeType.FreeformDirectional2D;
            existing.blendParameter = "MoveX";
            existing.blendParameterY = "MoveZ";
            return existing;
        }

        var blendTree = new BlendTree
        {
            name = stateName,
            blendType = BlendTreeType.FreeformDirectional2D,
            blendParameter = "MoveX",
            blendParameterY = "MoveZ",
        };

        AssetDatabase.AddObjectToAsset(blendTree, controller);
        state.motion = blendTree;
        return blendTree;
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

        Debug.LogError($"[PlayerAnimatorControllerBuilder] 클립을 찾을 수 없음: {fbxPath}");
        return null;
    }
}
