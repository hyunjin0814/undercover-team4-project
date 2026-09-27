#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 벽에 박힌 팔 접기(RagdollArmFold) 재현 테스트 — 합성 리그와 Physics.Simulate로 팔이 빠져나오는지 검증한다.
/// </summary>
public class RagdollWallFoldTests
{
    private const float k_minPastSurface = 0.06f;
    private const float k_foldSeconds = 0.25f;
    private const float k_limbMassMax = 8f;
    private const int k_maxAttempts = 3;
    private const float k_windowSeconds = 8f;
    private const int k_probeStepsMoving = 4;
    private const int k_probeStepsSettled = 10;
    private const int k_confirmProbes = 3;

    private const float k_armMass = 4.38f;
    private const float k_torsoMass = 10.94f;

    private const float k_armRadius = 0.085f;

    private const float k_wallFace = 0.25f;

    private const float k_thinWall = 0.038f;

    private const float k_launchSpeed = 14f;
    private const float k_liftRatio = 0.5f;

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

    /// <summary>팔끝이 벽면 너머로 들어간 깊이(m)를 잰다. 막는 벽이 없으면 0.</summary>
    private float MeasurePastSurface()
    {
        Physics.SyncTransforms();

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

    private string DescribeArm(int step)
    {
        float past = MeasurePastSurface();
        float z = ArmTipCollider().bounds.center.z;

        return $"{step,3}스텝 팔끝 z={z:F3} "
            + (past > 0f ? $"벽면 너머 {past * 100f:F1}cm" : "벽 없음");
    }

    private string DescribeFoldLogs() =>
        m_foldLogs.Count == 0 ? "(로그 없음)" : string.Join("\n  ", m_foldLogs);

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

    [Test]
    public void 벽에_박힌_팔에_임펄스를_주면_접어서_빼낸다()
    {
        BuildScene(wallThickness: k_thinWall);
        PushArmThroughWall(k_thinWall);

        RagdollArmFold fold = new RagdollArmFold(m_rig, IsCharacterCollider) { Label = "테스트NPC" };
        RagdollArmFold.Tuning tuning = BuildTuning();

        fold.Begin();
        m_rig.SetKinematic(false);

        m_rig.SetBoneKinematic(IndexOfBone("Hips"), true);
        m_rig.ApplyImpulse(ImpulseIntoWall());

        List<string> trace = new List<string>();

        float shallowest = float.MaxValue;

        bool escaped = false;
        for (int step = 0; step < k_maxFixedSteps && !(escaped && !fold.IsFolding); step++)
        {
            Physics.Simulate(Time.fixedDeltaTime);

            shallowest = Mathf.Min(shallowest, MeasurePastSurface());
            if (trace.Count < 16)
                trace.Add(DescribeArm(step));

            fold.Tick(false, tuning);
            escaped = escaped || SawFoldLog("빠져나옴");
        }

        Assert.IsTrue(
            SawFoldLog("접기 시작"),
            "접기가 시작되지 않았다 — 몸이 벽을 떠났거나 판정이 안 걸렸다.\n"
                + $"  로그: {DescribeFoldLogs()}\n  {string.Join("\n  ", trace)}"
        );

        Assert.IsTrue(
            escaped,
            $"{k_maxFixedSteps}스텝 안에 팔이 빠져나오지 못했다.\n  {DescribeFoldLogs()}"
        );

        Assert.IsFalse(
            fold.IsFolding,
            $"붙드는 스텝이 끝났는데도 접는 중이다 — 뼈가 키네마틱으로 갇힌다.\n  {DescribeFoldLogs()}"
        );

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
        BuildScene(wallThickness: 1f);
        PushArmIntoWall();

        RagdollArmFold fold = new RagdollArmFold(m_rig, IsCharacterCollider) { Label = "테스트NPC" };

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

    /// <summary>콜라이더가 사람인지 판정한다(NpcRagdoll.IsCharacterCollider와 같은 규칙).</summary>
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

    private void BuildScene(float wallThickness)
    {
        BuildWall(wallThickness);
        BuildRig();
        Physics.SyncTransforms();
    }

    private void BuildWall(float thickness)
    {
        GameObject go = NewObject("테스트벽", new Vector3(0f, 1.5f, k_wallFace + thickness * 0.5f));
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.size = new Vector3(6f, 4f, thickness);
        m_wall = box;
    }

    /// <summary>RagdollRig가 인식하는 최소 합성 리그(골반·팔·다리·머리)를 만든다.</summary>
    private void BuildRig()
    {
        GameObject owner = NewObject("테스트리그", Vector3.zero);

        GameObject root = new GameObject(RagdollRig.k_defaultBoneRootName);
        root.transform.SetParent(owner.transform, false);

        Transform hips = NewBone("Hips", root.transform, new Vector3(0f, 1f, 0f), k_torsoMass, null);
        Transform spine = NewBone("Spine", hips, new Vector3(0f, 1.3f, 0f), k_torsoMass, hips);
        NewBone("Head", spine, new Vector3(0f, 1.6f, 0f), 2f, spine);
        NewBone("Thigh_L", hips, new Vector3(0.15f, 0.6f, 0f), k_armMass, hips);

        Transform shoulder = NewBone("Shoulder_L", spine, new Vector3(0.2f, 1.3f, 0.1f), k_armMass, spine);
        NewBone("Elbow_L", shoulder, new Vector3(0.2f, 1.3f, 0.32f), k_armMass, shoulder);

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
        go.layer = m_ragdollLayer;
        go.transform.SetParent(parent, false);
        go.transform.position = position;

        CapsuleCollider capsule = go.AddComponent<CapsuleCollider>();
        capsule.radius = k_armRadius;
        capsule.height = 0.25f;
        capsule.direction = 2;

        Rigidbody body = go.AddComponent<Rigidbody>();
        body.mass = mass;
        body.useGravity = false;
        body.isKinematic = true;

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

    private void PushArmIntoWall()
    {
        Transform elbow = m_rig.Bones.GetTransform(IndexOfBone("Elbow_L"));
        elbow.position = new Vector3(elbow.position.x, elbow.position.y, k_wallFace + 0.2f);
        Physics.SyncTransforms();
    }

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

    private Collider SpawnCharacterBetweenHipsAndArm()
    {
        GameObject person = NewObject("테스트사람", new Vector3(0.1f, 1.15f, 0.15f));
        CharacterController controller = person.AddComponent<CharacterController>();
        controller.enabled = false;

        GameObject body = new GameObject("몸통콜라이더");
        body.transform.SetParent(person.transform, false);
        BoxCollider box = body.AddComponent<BoxCollider>();
        box.size = new Vector3(0.4f, 0.6f, 0.1f);
        Physics.SyncTransforms();

        return box;
    }

    private Vector3 ImpulseIntoWall() =>
        Vector3.forward * k_launchSpeed + Vector3.up * (k_launchSpeed * k_liftRatio);
}
#endif
