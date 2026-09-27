using NUnit.Framework;
using UnityEngine;

/// <summary>
/// 감정표현 휠 8칸 구성의 저장·복원 왕복 테스트.
/// </summary>
public class EmoteLoadoutTests
{
    private const string k_prefsKeyPrefix = "Emote.Loadout.";

    private const string k_ownerA = "test-owner-a";
    private const string k_ownerB = "test-owner-b";

    [TearDown]
    public void 테스트가_쓴_저장값을_지운다()
    {
        PlayerPrefs.DeleteKey(k_prefsKeyPrefix + k_ownerA);
        PlayerPrefs.DeleteKey(k_prefsKeyPrefix + k_ownerB);
        PlayerPrefs.Save();
    }

    [Test]
    public void 새_구성은_전부_비어있다()
    {
        var loadout = new EmoteLoadout();
        for (int slot = 0; slot < EmoteLoadout.k_slotCount; slot++)
            Assert.IsNull(loadout.GetSlot(slot), $"{slot}번 칸");
    }

    [Test]
    public void 넣은_값을_그대로_돌려준다()
    {
        var loadout = new EmoteLoadout();
        loadout.SetSlot(3, "dance01");
        Assert.AreEqual("dance01", loadout.GetSlot(3));
    }

    [Test]
    public void 빈_문자열은_빈_칸으로_취급한다()
    {
        var loadout = new EmoteLoadout();
        loadout.SetSlot(2, "cheer01");
        loadout.SetSlot(2, "");
        Assert.IsNull(loadout.GetSlot(2));
    }

    [Test]
    public void 범위_밖_인덱스는_무시한다()
    {
        var loadout = new EmoteLoadout();
        Assert.DoesNotThrow(() => loadout.SetSlot(-1, "dance01"));
        Assert.DoesNotThrow(() => loadout.SetSlot(EmoteLoadout.k_slotCount, "dance01"));
        Assert.IsNull(loadout.GetSlot(-1));
        Assert.IsNull(loadout.GetSlot(EmoteLoadout.k_slotCount));
    }

    [Test]
    public void 직렬화_왕복이_구성을_보존한다()
    {
        var source = new EmoteLoadout();
        source.SetSlot(0, "dance01");
        source.SetSlot(4, "cheer01");
        source.SetSlot(7, "clap01");

        var restored = new EmoteLoadout();
        restored.Deserialize(source.Serialize());

        Assert.AreEqual("dance01", restored.GetSlot(0));
        Assert.IsNull(restored.GetSlot(1));
        Assert.AreEqual("cheer01", restored.GetSlot(4));
        Assert.AreEqual("clap01", restored.GetSlot(7));
    }

    [Test]
    public void 짧은_문자열을_읽어도_칸_수는_그대로다()
    {
        var loadout = new EmoteLoadout();
        loadout.Deserialize("dance01|cheer01");

        Assert.AreEqual("dance01", loadout.GetSlot(0));
        Assert.AreEqual("cheer01", loadout.GetSlot(1));
        Assert.IsNull(loadout.GetSlot(7));
    }

    [Test]
    public void 긴_문자열은_넘치는_부분을_버린다()
    {
        var loadout = new EmoteLoadout();
        loadout.Deserialize("a|b|c|d|e|f|g|h|i|j");

        Assert.AreEqual("h", loadout.GetSlot(7), "마지막 칸");
        Assert.IsNull(loadout.GetSlot(EmoteLoadout.k_slotCount), "칸 수를 넘은 자리");
        Assert.AreEqual("a|b|c|d|e|f|g|h", loadout.Serialize());
    }

    [Test]
    public void 빈_문자열을_읽으면_전부_빈_칸이다()
    {
        var loadout = new EmoteLoadout();
        loadout.SetSlot(0, "dance01");
        loadout.Deserialize("");

        Assert.IsNull(loadout.GetSlot(0));
    }

    [Test]
    public void null을_읽어도_예외가_없다()
    {
        var loadout = new EmoteLoadout();
        Assert.DoesNotThrow(() => loadout.Deserialize(null));
    }

    [Test]
    public void 계정이_다르면_저장이_섞이지_않는다()
    {
        var first = new EmoteLoadout(k_ownerA);
        first.SetSlot(0, "dance01");
        first.Save();

        var second = new EmoteLoadout(k_ownerB);
        second.SetSlot(0, "cheer01");
        second.Save();

        var reloaded = new EmoteLoadout(k_ownerA);
        reloaded.Load();

        Assert.AreEqual("dance01", reloaded.GetSlot(0), "다른 계정의 저장이 덮어썼다");
        Assert.IsTrue(
            PlayerPrefs.HasKey(k_prefsKeyPrefix + k_ownerA),
            "계정별 키 형식이 바뀌었다 — 기존 저장값을 못 읽게 된다"
        );
    }

    [Test]
    public void flush를_미뤄도_저장한_값은_읽힌다()
    {
        var loadout = new EmoteLoadout(k_ownerA);
        loadout.SetSlot(2, "clap01");
        loadout.Save(flush: false);

        var reloaded = new EmoteLoadout(k_ownerA);
        reloaded.Load();

        Assert.AreEqual("clap01", reloaded.GetSlot(2));
    }
}
