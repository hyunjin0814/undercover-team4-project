#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// <b>벽에 박힌 팔 접기</b>(#980)의 재현 테스트 — <c>docs/refactoring/ragdoll-split.md</c> §4의
/// Play 회귀 3번을 자동화한 것이다. 그 항목의 판정 기준을 그대로 옮겼다:
/// <b>"<c>[래그돌팔접기]</c> 로그가 뜨고 팔이 빠져나온다."</b>
///
/// <b>NPC 프리팹도 네트워크 세션도 쓰지 않는다.</b> 여기서 검증할 것은 커밋 5(뼈 계층 질의 분리)와
/// 커밋 7(사람 판정 주입)이 <see cref="RagdollArmFold"/>의 동작을 바꾸지 않았는가이고, 그 셋은
/// <see cref="RagdollRig"/>·<see cref="RagdollBoneGraph"/>·<see cref="RagdollWallProbe"/>만 있으면
/// 돈다. 프리팹을 끌어들이면 NavMesh·NGO·FSM이 전부 따라와 <b>실패했을 때 어디가 깨졌는지를
/// 못 가른다</b> — 그쪽 통합 확인은 <see cref="RagdollWallDevHotkeys"/>(<c>,</c>·<c>.</c>)가 맡는다.
///
/// <b>합성 리그의 조건은 임의값이 아니다.</b> <see cref="RagdollRig"/>가 뼈를 알아보는 규칙
/// (레이어 <c>Ragdoll</c> · 관절 없는 뼈가 골반)과 <see cref="RagdollBoneGraph.CollectArmBones"/>가
/// 팔을 가르는 규칙(골반 직속이 아니고 · 머리가 아니고 · 질량 상한 이하)을 만족해야 하므로,
/// 아래 <see cref="BuildRig"/>가 그 규칙을 그대로 따라 짓는다. 질량은 실측값이다(팔 4.38 / 몸통 10.94
/// — <c>NpcRagdoll.WallFix</c>의 인스펙터 주석).
///
/// ⚠ <b>이 파일에는 asmdef가 없다</b> — 래그돌 코드가 <c>Assembly-CSharp</c>에 있는데 asmdef는 기본
/// 어셈블리를 참조하지 못해서다. 그래서 테스트도 같은 어셈블리에 산다. 그 자리에는 <c>nunit</c>만
/// 있고 <c>UnityEngine.TestRunner</c>는 없어 <c>[UnityTest]</c>(코루틴)를 못 쓴다 — 물리를 여러 스텝
/// 돌려야 하는 쪽은 <see cref="Physics.Simulate"/>로 직접 돌린다. 결과적으로 프레임 타이밍에
/// 흔들리지 않아 <b>매번 같은 결과</b>가 나온다.
/// </summary>
public class RagdollWallFoldTests
{
    // NpcRagdoll.BuildFoldTuning과 같은 값 — 프리팹 기본값이다. 여기가 달라지면 테스트가
    // "실제로 쓰이는 설정"을 검증하지 않게 되므로 그쪽을 고치면 여기도 같이 고친다.
    private const float k_minPastSurface = 0.06f;
    private const float k_foldSeconds = 0.25f;
    private const float k_limbMassMax = 8f;
    private const int k_maxAttempts = 3;
    private const float k_windowSeconds = 8f;
    private const int k_probeStepsMoving = 4;
    private const int k_probeStepsSettled = 10;
    private const int k_confirmProbes = 3;

    // 실측 질량 — 팔이 상한 아래, 몸통이 위여야 팔/몸통 구분이 성립한다.
    private const float k_armMass = 4.38f;
    private const float k_torsoMass = 10.94f;

    // 팔 캡슐 반지름(m) — 프리팹 실측. 침투 깊이를 이 값 기준으로 잡아야 의미가 있다.
    private const float k_armRadius = 0.085f;

    // 벽 앞면의 z — 팔을 넣을 깊이를 이 면 기준으로 잡는다.
    private const float k_wallFace = 0.25f;

    // HQ 벽 실측 두께(m). 이 얇기가 '완전 관통'을 만든다 — 팔이 반대편으로 나가 겹침이 0이 된다.
    private const float k_thinWall = 0.038f;

    // 홈런 진압봉 기본값 (HomeRunBaton.m_launchSpeed · RagdollWallDevHotkeys와 같은 조립).
    private const float k_launchSpeed = 14f;
    private const float k_liftRatio = 0.5f;

    // 접기가 끝나기를 기다리는 물리 스텝 상한. 판정 주기 4스텝 × 확인 3회 = 최소 12스텝 뒤에
    // 접기가 시작되고, 접는 데 0.25초(≈13스텝)가 더 걸린다. 150이면 넉넉하다.
    private const int k_maxFixedSteps = 150;

    private readonly List<GameObject> m_spawned = new List<GameObject>();
    private readonly List<string> m_foldLogs = new List<string>();

    private RagdollRig m_rig;
    private Collider m_wall;
    private int m_ragdollLayer;
    private SimulationMode m_previousSimulationMode;

    [SetUp]
    public void 로그를_받아_두고_레이어를_확인한다()
    {
        // 물리를 테스트가 직접 돌린다 — 에디터가 알아서 도는 것에 얹으면 스텝 수가 프레임에 따라
        // 달라져 같은 테스트가 어떤 날은 통과한다. 원래 값은 TearDown이 돌려놓는다.
        m_previousSimulationMode = Physics.simulationMode;
        Physics.simulationMode = SimulationMode.Script;

        m_ragdollLayer = LayerMask.NameToLayer(RagdollRig.k_layerName);
        if (m_ragdollLayer < 0)
        {
            Assert.Ignore(
                $"레이어 '{RagdollRig.k_layerName}'가 프로젝트에 없다 — "
                    + "Tools > Player > Finish Ragdoll Setup을 먼저 실행할 것"
            );
        }

        // 판정 기준이 "로그가 뜨는가"라 로그를 증거로 쓴다. LogAssert는 '기대한 로그가 안 오면
        // 실패'라 순서·개수까지 묶이는데, 여기서 필요한 것은 "이 문구가 나왔는가" 하나뿐이다.
        m_foldLogs.Clear();
        Application.logMessageReceived += CollectFoldLog;
    }

    [TearDown]
    public void 만든_것을_전부_지운다()
    {
        Application.logMessageReceived -= CollectFoldLog;
        Physics.simulationMode = m_previousSimulationMode;

        for (int i = 0; i < m_spawned.Count; i++)
            if (m_spawned[i] != null)
                Object.DestroyImmediate(m_spawned[i]);

        m_spawned.Clear();
        m_rig = null;
        m_wall = null;
    }

    private void CollectFoldLog(string message, string stackTrace, LogType type)
    {
        if (message.Contains("[래그돌팔접기]"))
            m_foldLogs.Add(message);
    }

    private bool SawFoldLog(string fragment)
    {
        for (int i = 0; i < m_foldLogs.Count; i++)
            if (m_foldLogs[i].Contains(fragment))
                return true;

        return false;
    }

    /// <summary>
    /// 지금 팔끝이 벽면 너머로 얼마나 들어가 있는가(m) — 막는 벽이 없으면 0.
    /// <c>RagdollArmFold.IsPinned</c>와 같은 질문을 테스트 쪽에서 재는 자다.
    /// </summary>
    private float MeasurePastSurface()
    {
        Physics.SyncTransforms(); // autoSyncTransforms=0 — 안 하면 방금 돌린 회전 이전 bounds를 읽는다

        return RagdollWallProbe.TryFindPinningWall(
            m_rig.Hips.position,
            ArmTipCollider(),
            0.02f,
            IsCharacterCollider,
            out RagdollWallProbe.Pin pin
        )
            ? pin.PastSurface
            : 0f;
    }

    // 이번 스텝에 팔이 어디 있고 프로브가 뭐라고 답했는가 — 실패 메시지가 이걸 달고 나온다.
    private string DescribeArm(int step)
    {
        float past = MeasurePastSurface();
        float z = ArmTipCollider().bounds.center.z;

        return $"{step,3}스텝 팔끝 z={z:F3} "
            + (past > 0f ? $"벽면 너머 {past * 100f:F1}cm" : "벽 없음");
    }

    private string DescribeFoldLogs() =>
        m_foldLogs.Count == 0 ? "(로그 없음)" : string.Join("\n  ", m_foldLogs);

    // ---- 1. 프로브: 커밋 7(사람 판정 주입)이 만든 이음매 ----

    [Test]
    public void 팔이_벽_너머에_있으면_벽을_찾고_법선은_골반을_향한다()
    {
        BuildScene(wallThickness: 1f);
        PushArmIntoWall();

        Assert.IsTrue(
            RagdollWallProbe.TryFindPinningWall(
                m_rig.Hips.position,
                ArmTipCollider(),
                0.02f,
                IsCharacterCollider,
                out RagdollWallProbe.Pin pin
            ),
            "벽 안에 넣은 팔을 프로브가 못 찾았다"
        );

        Assert.AreEqual(m_wall, pin.Wall, "다른 콜라이더를 벽으로 집었다");

        // 탈출 방향의 정당성이 여기서 나온다 — 골반에서 쐈으므로 첫 면의 법선은 골반을 향한다.
        Vector3 towardHips = (m_rig.Hips.position - pin.Point).normalized;
        Assert.Greater(
            Vector3.Dot(pin.Normal, towardHips),
            0f,
            $"법선이 골반 반대쪽을 향한다 — 이 방향으로 밀면 몸이 벽 속으로 들어간다 (법선 {pin.Normal:F3})"
        );

        Assert.Greater(
            pin.PastSurface,
            k_minPastSurface,
            $"벽면 너머 깊이가 판정 문턱보다 얕다 — 재현 자체가 성립하지 않았다 ({pin.PastSurface * 100f:F1}cm)"
        );
    }

    [Test]
    public void 완전히_관통한_팔도_잡는다()
    {
        // 겹침 0에는 "안 닿음"과 "반대편으로 뚫고 나감" 두 뜻이 있다. ComputePenetration으로
        // 판정했다면 여기서 놓친다 — 레이로 바꾼 이유가 이것이다 (docs/980 §2-7).
        BuildScene(wallThickness: k_thinWall);
        PushArmThroughWall(k_thinWall);

        Assert.AreEqual(
            0f,
            Physics.ComputePenetration(
                ArmTipCollider(),
                ArmTipCollider().transform.position,
                ArmTipCollider().transform.rotation,
                m_wall,
                m_wall.transform.position,
                m_wall.transform.rotation,
                out _,
                out float overlap
            )
                ? overlap
                : 0f,
            0.0001f,
            "이 테스트는 겹침이 0인 상태를 전제로 한다 — 배치가 틀렸다"
        );

        Assert.IsTrue(
            RagdollWallProbe.TryFindPinningWall(
                m_rig.Hips.position,
                ArmTipCollider(),
                0.02f,
                IsCharacterCollider,
                out RagdollWallProbe.Pin pin
            ),
            "관통한 팔을 놓쳤다 — 겹침 깊이로 되돌아간 것은 아닌지 볼 것"
        );

        Assert.AreEqual(m_wall, pin.Wall);
    }

    [Test]
    public void 사람은_벽으로_세지_않는다()
    {
        BuildScene(wallThickness: 1f);
        PushArmIntoWall();

        // 골반과 팔 사이에 사람을 세운다 — 자기 자신의 루트 캡슐이 여기 걸리는 것이 실제 상황이다.
        Collider person = SpawnCharacterBetweenHipsAndArm();

        Assert.IsTrue(
            RagdollWallProbe.TryFindPinningWall(
                m_rig.Hips.position,
                ArmTipCollider(),
                0.02f,
                IsCharacterCollider,
                out RagdollWallProbe.Pin withPredicate
            ),
            "사람을 걸러낸 뒤 뒤쪽 벽을 찾았어야 한다 — 첫 히트만 보고 포기한 것은 아닌지"
        );
        Assert.AreEqual(
            m_wall,
            withPredicate.Wall,
            "사람을 벽으로 집었다 — 술어가 안 먹었다"
        );

        // 술어가 없으면 사람이 그대로 벽으로 잡힌다. 이것이 술어를 주입하는 이유다(커밋 7).
        Assert.IsTrue(
            RagdollWallProbe.TryFindPinningWall(
                m_rig.Hips.position,
                ArmTipCollider(),
                0.02f,
                null,
                out RagdollWallProbe.Pin without
            )
        );
        Assert.AreEqual(
            person,
            without.Wall,
            "술어 없이도 사람이 안 잡혔다 — 이 테스트의 전제(사람이 사이를 막는다)가 깨졌다"
        );
    }

    [Test]
    public void 술어가_null이어도_터지지_않는다()
    {
        // Common은 도메인을 모르므로 술어를 못 받을 수 있다. NRE가 아니라 '거르지 않음'이어야 한다.
        BuildScene(wallThickness: 1f);
        PushArmIntoWall();

        Assert.DoesNotThrow(
            () =>
                RagdollWallProbe.TryFindPinningWall(
                    m_rig.Hips.position,
                    ArmTipCollider(),
                    0.02f,
                    null,
                    out _
                )
        );
    }

    [Test]
    public void 바닥과_천장은_벽이_아니다()
    {
        // 계단·경사에 누운 다리가 상시로 걸리는 것을 막는 가드(|법선.y| > 0.5).
        BuildScene(wallThickness: 1f);

        GameObject hips = NewObject("가짜골반", new Vector3(0f, 2f, 0f));
        GameObject bone = NewObject("가짜뼈", new Vector3(0f, 0f, 0f));
        SphereCollider boneCollider = bone.AddComponent<SphereCollider>();
        boneCollider.radius = 0.05f;

        GameObject floor = NewObject("바닥", new Vector3(0f, 1f, 0f));
        BoxCollider plate = floor.AddComponent<BoxCollider>();
        plate.size = new Vector3(10f, 0.2f, 10f);
        Physics.SyncTransforms();

        Assert.IsFalse(
            RagdollWallProbe.TryFindPinningWall(
                hips.transform.position,
                boneCollider,
                0.02f,
                IsCharacterCollider,
                out _
            ),
            "수평면을 벽으로 집었다"
        );
    }

    // ---- 2. 뼈 계층 질의: 커밋 5가 옮긴 규칙 ----

    [Test]
    public void 팔만_대상이다_다리와_머리와_몸통은_뺀다()
    {
        BuildScene(wallThickness: 1f);

        int[] into = new int[16];
        int count = m_rig.Bones.CollectArmBones(k_limbMassMax, into);

        List<string> names = new List<string>();
        for (int i = 0; i < count; i++)
            names.Add(m_rig.Bones.GetName(into[i]));

        names.Sort();
        CollectionAssert.AreEqual(
            new[] { "Elbow_L", "Shoulder_L" },
            names,
            $"팔 판정이 달라졌다 — 담긴 것: {string.Join(", ", names)}"
        );
    }

    [Test]
    public void 접을_체인은_팔에서_멈추고_몸통을_끌어들이지_않는다()
    {
        BuildScene(wallThickness: 1f);

        int elbow = IndexOfBone("Elbow_L");
        int[] into = new int[8];
        int count = m_rig.Bones.CollectChainUpward(elbow, 3, k_limbMassMax, into);

        List<string> names = new List<string>();
        for (int i = 0; i < count; i++)
            names.Add(m_rig.Bones.GetName(into[i]));

        CollectionAssert.AreEqual(
            new[] { "Elbow_L", "Shoulder_L" },
            names,
            $"체인이 몸통까지 갔다 — 담긴 것: {string.Join(", ", names)}"
        );
    }

    // ---- 3. 본편: 벽에 박힌 팔 + 임펄스 ----

    [Test]
    public void 벽에_박힌_팔에_임펄스를_주면_접어서_빼낸다()
    {
        // ⚠ <b>얇은 벽을 관통시킨다 — 두꺼운 벽에 깊이 겹치게 두면 안 된다.</b> 실측(이 테스트를
        // 그렇게 짰다가 고쳤다): 25.9cm 겹친 팔은 물리에 넘긴 지 <b>5스텝(0.1초)</b> 만에
        // 디페네트레이션이 스스로 밀어내 벽 밖으로 나간다(25.9 → 21.7 → 14.6 → 10.2 → 4.3 → 벽 밖).
        // 확인 3회(12스텝)를 채우기 전에 조건이 사라져 접기가 아예 안 걸린다.
        //
        // <b>실제로 박힌 채 남는 것은 겹침이 0인 관통 쪽이다</b> — 밀어낼 겹침이 없으니 물리가
        // 손대지 않는다. #980이 "박혔다"고 부르는 상태가 이것이고, 겹침 깊이 대신 레이로 판정하게
        // 바꾼 이유(docs/980 §2-7)도 같다.
        BuildScene(wallThickness: k_thinWall);
        PushArmThroughWall(k_thinWall);

        RagdollArmFold fold = new RagdollArmFold(m_rig, IsCharacterCollider) { Label = "테스트NPC" };
        RagdollArmFold.Tuning tuning = BuildTuning();

        // NpcRagdoll.EnterRagdoll과 같은 순서 — 창을 열고, 물리에 넘기고, 임펄스를 준다.
        fold.Begin();
        m_rig.SetKinematic(false);

        // ⚠ <b>골반만 다시 키네마틱으로 묶는다.</b> 임펄스 14m/s는 0.24초(확인 3회에 필요한 시간)에
        // 3.4m를 가므로, 몸을 자유롭게 두면 <b>판정이 끝나기 전에 벽을 떠난다</b> — 그러면 이 테스트는
        // 접기가 아니라 "날아갔다"를 보게 된다. 실제 상황도 몸은 바닥·벽에 눌려 거의 안 움직이고
        // 팔만 박혀 있는 쪽이다. ApplyImpulse는 키네마틱 뼈를 건너뛰므로 힘은 팔·다리에만 실린다.
        m_rig.SetBoneKinematic(IndexOfBone("Hips"), true);
        m_rig.ApplyImpulse(ImpulseIntoWall());

        // ⚠ <b>물리를 손으로 스텝한다</b> — 코루틴(<c>WaitForFixedUpdate</c>)이 아니다.
        // Assembly-CSharp에는 <c>UnityEngine.TestRunner</c>가 안 붙어 있어 <c>[UnityTest]</c>를 못 쓰고,
        // 손으로 도는 편이 프레임 타이밍에 흔들리지 않아 <b>결과가 매번 같다</b>.
        // 실패했을 때 "왜 안 걸렸나"를 눈이 아니라 값으로 말하게 한다 — 팔이 어디 있었는지가 없으면
        // 이 테스트의 실패는 읽을 수가 없다.
        List<string> trace = new List<string>();

        // ⚠ <b>"빠져나옴" 로그에서 멈추면 안 된다.</b> 그 로그 뒤에도 접기는 몇 스텝 더 붙들었다가
        // (<c>m_holdLeft</c>) 뼈를 물리로 돌려준다 — 로그 직후에는 <see cref="RagdollArmFold.IsFolding"/>이
        // 아직 참이다. 돌려주는 데까지 가야 "갇히지 않았다"를 볼 수 있다.
        // 접는 동안 팔이 가장 얕게 들어갔던 지점 — "정말 빼냈는가"를 이 값으로 판정한다.
        float shallowest = float.MaxValue;

        bool escaped = false;
        for (int step = 0; step < k_maxFixedSteps && !(escaped && !fold.IsFolding); step++)
        {
            Physics.Simulate(Time.fixedDeltaTime);

            shallowest = Mathf.Min(shallowest, MeasurePastSurface());
            if (trace.Count < 16)
                trace.Add(DescribeArm(step));

            // NpcRagdoll.TickWallFix와 같은 자리 — FixedUpdate의 마지막이다.
            fold.Tick(false, tuning);
            escaped = escaped || SawFoldLog("빠져나옴");
        }

        // ⚠ <b>"빠져나왔다"만 보면 안 된다.</b> 몸이 임펄스에 통째로 날아가 벽을 떠나도 팔은
        // 안 박힌 상태가 되므로, 접기가 <b>실제로 돌았는지</b>를 먼저 못박는다.
        Assert.IsTrue(
            SawFoldLog("접기 시작"),
            "접기가 시작되지 않았다 — 몸이 벽을 떠났거나 판정이 안 걸렸다.\n"
                + $"  로그: {DescribeFoldLogs()}\n  {string.Join("\n  ", trace)}"
        );

        Assert.IsTrue(
            escaped,
            $"{k_maxFixedSteps}스텝 안에 팔이 빠져나오지 못했다.\n  {DescribeFoldLogs()}"
        );

        // 붙드는 스텝이 끝나면 뼈를 물리로 돌려줘야 한다 — 안 돌려주면 팔이 키네마틱으로 갇힌 채
        // 남아 정착·스트림과 어긋난다.
        Assert.IsFalse(
            fold.IsFolding,
            $"붙드는 스텝이 끝났는데도 접는 중이다 — 뼈가 키네마틱으로 갇힌다.\n  {DescribeFoldLogs()}"
        );

        // ⚠ <b>"놓은 뒤에도 밖에 있는가"는 묻지 않는다.</b> 접기는 빠져나온 자리에서 멈추는데
        // (더 접으면 관절 한계를 넘는다 — <c>TickFold</c>의 주석), 그 자세를 놓는 순간 관절이
        // 되튕겨 팔이 다시 벽 쪽으로 간다. 실측: 25.4cm → 0으로 빼낸 뒤 놓고 나서 9.2cm로 되돌아갔다.
        // 여기 합성 리그는 <c>CharacterJoint</c> 기본 한계라 프리팹보다 세게 튕기므로, 그 값을
        // 못박으면 <b>리팩토링이 아니라 관절 설정을 재는 테스트</b>가 된다.
        //
        // 판정 기준(§4 3번)이 말하는 것은 "팔이 빠져나온다"이고, 그 사건은 접는 동안 일어난다.
        Assert.LessOrEqual(
            shallowest,
            k_minPastSurface,
            "로그는 빠져나왔다는데 팔이 한 번도 벽면 밖으로 안 나왔다 — 로그가 거짓말을 한다.\n"
                + $"  로그: {DescribeFoldLogs()}\n  {string.Join("\n  ", trace)}"
        );
    }

    [Test]
    public void 진입_예방은_물리에_넘기기_전에_팔을_접는다()
    {
        // 대부분의 사례가 여기서 걸린다 — 애초에 박힌 채로 출발하지 않게 하는 쪽 (docs/980 §3-ⓐ).
        BuildScene(wallThickness: 1f);
        PushArmIntoWall();

        RagdollArmFold fold = new RagdollArmFold(m_rig, IsCharacterCollider) { Label = "테스트NPC" };

        // 뼈는 아직 키네마틱이다 — 그래서 벽과 싸우지 않고 자세를 고칠 수 있다.
        Assert.IsTrue(fold.FoldOnEntry(BuildTuning()), "진입 예방이 박힌 팔을 못 찾았다");
        Assert.IsTrue(SawFoldLog("진입 예방"), $"예방 로그가 없다.\n  {DescribeFoldLogs()}");

        Physics.SyncTransforms();
        bool stillPinned =
            RagdollWallProbe.TryFindPinningWall(
                m_rig.Hips.position,
                ArmTipCollider(),
                0.02f,
                IsCharacterCollider,
                out RagdollWallProbe.Pin pin
            ) && pin.PastSurface > k_minPastSurface;

        Assert.IsFalse(stillPinned, "접었다는데 팔이 아직 벽면 너머에 있다");
    }

    [Test]
    public void 창이_닫혀_있으면_아무것도_하지_않는다()
    {
        // Begin 전·End 후에 도는 Tick이 뼈를 건드리면 정착·스트림과 싸운다.
        BuildScene(wallThickness: 1f);
        PushArmIntoWall();

        RagdollArmFold fold = new RagdollArmFold(m_rig, IsCharacterCollider) { Label = "테스트NPC" };

        Assert.IsFalse(fold.IsWindowOpen);
        for (int i = 0; i < 20; i++)
            fold.Tick(false, BuildTuning());

        Assert.IsEmpty(m_foldLogs, $"창이 닫혔는데 접기가 돌았다.\n  {DescribeFoldLogs()}");

        fold.Begin();
        fold.End();
        Assert.IsFalse(fold.IsWindowOpen, "End 뒤에도 창이 열려 있다");
    }

    // ---- 조립 ----

    private RagdollArmFold.Tuning BuildTuning() =>
        new RagdollArmFold.Tuning(
            k_minPastSurface,
            k_foldSeconds,
            k_limbMassMax,
            k_maxAttempts,
            k_windowSeconds,
            k_probeStepsMoving,
            k_probeStepsSettled,
            k_confirmProbes
        );

    /// <summary>
    /// 사람 판정 — <c>NpcRagdoll.IsCharacterCollider</c>와 같은 3항이다.
    ///
    /// ⚠ 그쪽은 <c>private static</c>이라 직접 부를 수 없어 여기 같은 것을 둔다. 저장소에 비슷한
    /// 술어가 <b>여섯 곳에 세 가지 형태</b>로 갈려 있으므로(<c>AimOcclusion</c>은 <c>PlayerHealth</c>를
    /// 안 본다), 그쪽을 고칠 때 여기도 같이 봐야 한다. <c>PlayerHealth</c>·<c>NpcController</c>는
    /// 이 테스트가 세우지 않으므로 실제로 걸리는 것은 <c>CharacterController</c> 가지다.
    /// </summary>
    private static bool IsCharacterCollider(Collider collider) =>
        collider.GetComponentInParent<CharacterController>() != null
        || collider.GetComponentInParent<NpcController>() != null
        || collider.GetComponentInParent<PlayerHealth>() != null;

    private GameObject NewObject(string name, Vector3 position)
    {
        GameObject go = new GameObject(name);
        go.transform.position = position;
        m_spawned.Add(go);
        return go;
    }

    // 벽 하나와 합성 리그 하나. 벽은 두께만 바꿔 가며 쓴다 — 얇은 벽은 '관통', 두꺼운 벽은 '박힘'이다.
    private void BuildScene(float wallThickness)
    {
        BuildWall(wallThickness);
        BuildRig();
        Physics.SyncTransforms();
    }

    private void BuildWall(float thickness)
    {
        // 앞면이 z = k_wallFace에 오게 둔다 — 팔을 넣을 깊이를 이 면 기준으로 잡는다.
        GameObject go = NewObject("테스트벽", new Vector3(0f, 1.5f, k_wallFace + thickness * 0.5f));
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.size = new Vector3(6f, 4f, thickness);
        m_wall = box;
    }

    /// <summary>
    /// <see cref="RagdollRig"/>가 알아보는 최소 리그. 관절이 없는 뼈가 골반이고, 부모는
    /// <c>CharacterJoint.connectedBody</c>가 준다 — <see cref="RagdollBoneGraph"/>가 그걸로 계층을 읽는다.
    ///
    /// 다리(<c>Thigh_L</c>)와 머리(<c>Head</c>)를 일부러 넣는다. 이 둘이 팔 목록에서 빠지는 것이
    /// <see cref="RagdollBoneGraph.CollectArmBones"/>의 규칙이라, 없으면 그 규칙을 검증하지 못한다.
    /// </summary>
    private void BuildRig()
    {
        GameObject owner = NewObject("테스트리그", Vector3.zero);

        // RagdollRig는 리그 최상단을 '직속 자식'에서 이름으로 찾는다.
        GameObject root = new GameObject(RagdollRig.k_defaultBoneRootName);
        root.transform.SetParent(owner.transform, false);

        Transform hips = NewBone("Hips", root.transform, new Vector3(0f, 1f, 0f), k_torsoMass, null);
        Transform spine = NewBone("Spine", hips, new Vector3(0f, 1.3f, 0f), k_torsoMass, hips);
        NewBone("Head", spine, new Vector3(0f, 1.6f, 0f), 2f, spine);
        NewBone("Thigh_L", hips, new Vector3(0.15f, 0.6f, 0f), k_armMass, hips); // 골반 직속 = 다리

        Transform shoulder = NewBone("Shoulder_L", spine, new Vector3(0.2f, 1.3f, 0.1f), k_armMass, spine);
        NewBone("Elbow_L", shoulder, new Vector3(0.2f, 1.3f, 0.32f), k_armMass, shoulder);

        // 계층이 다 선 뒤에 붙인다 — Awake가 곧바로 뼈를 수집한다.
        m_rig = owner.AddComponent<RagdollRig>();

        Assert.IsTrue(
            m_rig.IsValid,
            "합성 리그를 RagdollRig가 못 알아봤다 — 레이어나 관절 배선을 볼 것"
        );
        Assert.AreEqual("Hips", m_rig.Hips.name, "관절 없는 뼈를 골반으로 잡는 규칙이 깨졌다");
    }

    private Transform NewBone(string name, Transform parent, Vector3 position, float mass, Transform connectedTo)
    {
        GameObject go = new GameObject(name);
        go.layer = m_ragdollLayer; // 이 레이어에 있는 Rigidbody만 뼈로 센다
        go.transform.SetParent(parent, false);
        go.transform.position = position;

        CapsuleCollider capsule = go.AddComponent<CapsuleCollider>();
        capsule.radius = k_armRadius;
        capsule.height = 0.25f;
        capsule.direction = 2; // z축 — 팔이 벽 쪽(+z)을 향한다

        Rigidbody body = go.AddComponent<Rigidbody>();
        body.mass = mass;
        body.useGravity = false; // 몸이 낙하로 벽을 떠나면 '박힘'이 재현되지 않는다
        body.isKinematic = true;

        // 관절이 없는 뼈가 골반이다 — 골반에만 안 붙인다.
        if (connectedTo != null)
        {
            CharacterJoint joint = go.AddComponent<CharacterJoint>();
            joint.connectedBody = connectedTo.GetComponent<Rigidbody>();
        }

        return go.transform;
    }

    private int IndexOfBone(string name)
    {
        for (int i = 0; i < 32; i++)
            if (m_rig.Bones.GetName(i) == name)
                return i;

        Assert.Fail($"뼈 '{name}'을 리그에서 못 찾았다");
        return -1;
    }

    private Collider ArmTipCollider() => m_rig.Bones.GetCollider(IndexOfBone("Elbow_L"));

    // 팔끝을 벽면 너머로 밀어 넣는다 — 회전이 아니라 위치로 옮긴다. 여기서는 관절 위반이
    // 문제가 되지 않는다(접기 판정은 콜라이더 위치만 본다).
    private void PushArmIntoWall()
    {
        Transform elbow = m_rig.Bones.GetTransform(IndexOfBone("Elbow_L"));
        elbow.position = new Vector3(elbow.position.x, elbow.position.y, k_wallFace + 0.2f);
        Physics.SyncTransforms();
    }

    // 벽을 완전히 통과시킨다 — 겹침 0이지만 골반에서 보면 벽 뒤다.
    private void PushArmThroughWall(float thickness)
    {
        Transform elbow = m_rig.Bones.GetTransform(IndexOfBone("Elbow_L"));
        elbow.position = new Vector3(
            elbow.position.x,
            elbow.position.y,
            k_wallFace + thickness + k_armRadius + 0.05f
        );
        Physics.SyncTransforms();
    }

    // 골반과 팔 사이에 사람을 세운다. CharacterController가 붙은 부모 아래의 콜라이더 —
    // NpcProneCollider의 캡슐이 래그돌 중에도 켜져 있는 상황과 같은 모양이다.
    private Collider SpawnCharacterBetweenHipsAndArm()
    {
        GameObject person = NewObject("테스트사람", new Vector3(0.1f, 1.15f, 0.15f));
        CharacterController controller = person.AddComponent<CharacterController>();
        controller.enabled = false; // 자체 캡슐이 레이에 끼어들지 않게 — 자식 콜라이더만 쓴다

        GameObject body = new GameObject("몸통콜라이더");
        body.transform.SetParent(person.transform, false);
        BoxCollider box = body.AddComponent<BoxCollider>();
        box.size = new Vector3(0.4f, 0.6f, 0.1f);
        Physics.SyncTransforms();

        return box;
    }

    // 벽 쪽으로 눕히는 임펄스 — RagdollWallDevHotkeys.LaunchIntoWall과 같은 조립이다.
    private Vector3 ImpulseIntoWall() =>
        Vector3.forward * k_launchSpeed + Vector3.up * (k_launchSpeed * k_liftRatio);
}
#endif
