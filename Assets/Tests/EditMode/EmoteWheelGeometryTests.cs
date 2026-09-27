using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 휠 방향 → 슬롯 인덱스 매핑 테스트.
/// </summary>
public class EmoteWheelGeometryTests
{
    [Test]
    public void 위쪽은_0번_슬롯이다()
    {
        Assert.AreEqual(0, EmoteWheelGeometry.SlotFromDirection(Vector2.up));
    }

    [Test]
    public void 시계방향으로_인덱스가_증가한다()
    {
        Assert.AreEqual(2, EmoteWheelGeometry.SlotFromDirection(Vector2.right), "오른쪽");
        Assert.AreEqual(4, EmoteWheelGeometry.SlotFromDirection(Vector2.down), "아래");
        Assert.AreEqual(6, EmoteWheelGeometry.SlotFromDirection(Vector2.left), "왼쪽");
    }

    [Test]
    public void 대각선도_제자리에_떨어진다()
    {
        Assert.AreEqual(1, EmoteWheelGeometry.SlotFromDirection(new Vector2(1f, 1f)), "오른위");
        Assert.AreEqual(7, EmoteWheelGeometry.SlotFromDirection(new Vector2(-1f, 1f)), "왼위");
    }

    [Test]
    public void 경계각은_다음_슬롯으로_넘어간다()
    {
        Assert.AreEqual(0, EmoteWheelGeometry.SlotFromDirection(DirectionAt(22f)), "22도");
        Assert.AreEqual(1, EmoteWheelGeometry.SlotFromDirection(DirectionAt(23f)), "23도");
    }

    [Test]
    public void 한바퀴_돌아도_같은_슬롯이다()
    {
        Assert.AreEqual(
            EmoteWheelGeometry.SlotFromDirection(DirectionAt(30f)),
            EmoteWheelGeometry.SlotFromDirection(DirectionAt(390f)));
    }

    [Test]
    public void 데드존_안에서는_선택이_없다()
    {
        Assert.AreEqual(-1, EmoteWheelGeometry.SlotFromDirection(Vector2.zero), "정중앙");
        Assert.AreEqual(-1, EmoteWheelGeometry.SlotFromDirection(Vector2.up * 0.1f), "데드존 안");
    }

    [Test]
    public void 슬롯_중심각은_45도_간격이다()
    {
        Assert.AreEqual(0f, EmoteWheelGeometry.SlotCenterDegrees(0), 0.001f);
        Assert.AreEqual(45f, EmoteWheelGeometry.SlotCenterDegrees(1), 0.001f);
        Assert.AreEqual(315f, EmoteWheelGeometry.SlotCenterDegrees(7), 0.001f);
    }

    private static Vector2 DirectionAt(float degrees)
    {
        float radians = degrees * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
    }
}
