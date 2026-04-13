using KbFix.Domain;
using Xunit;

namespace KbFix.Tests.Domain;

public class LayoutSetTests
{
    private static LayoutId Cs() => LayoutId.Create(0x0405, "00000405");
    private static LayoutId CsQwertz() => LayoutId.Create(0x0405, "00010405");
    private static LayoutId En() => LayoutId.Create(0x0409, "00000409");

    [Fact]
    public void Empty_set_has_count_zero()
    {
        Assert.Equal(0, LayoutSet.Empty.Count);
    }

    [Fact]
    public void Constructor_deduplicates_input()
    {
        var set = new LayoutSet(new[] { Cs(), Cs(), En() });
        Assert.Equal(2, set.Count);
    }

    [Fact]
    public void Difference_returns_items_only_in_left()
    {
        var session = new LayoutSet(new[] { Cs(), CsQwertz(), En() });
        var persisted = new LayoutSet(new[] { Cs(), En() });

        var extra = session.Difference(persisted);

        Assert.Equal(1, extra.Count);
        Assert.True(extra.Contains(CsQwertz()));
    }

    [Fact]
    public void Difference_with_self_is_empty()
    {
        var s = new LayoutSet(new[] { Cs(), En() });
        Assert.Equal(0, s.Difference(s).Count);
    }

    [Fact]
    public void Difference_with_empty_is_self()
    {
        var s = new LayoutSet(new[] { Cs(), En() });
        Assert.Equal(2, s.Difference(LayoutSet.Empty).Count);
    }

    [Fact]
    public void Empty_difference_with_anything_is_empty()
    {
        var s = new LayoutSet(new[] { Cs(), En() });
        Assert.Equal(0, LayoutSet.Empty.Difference(s).Count);
    }

    [Fact]
    public void Contains_is_true_for_member_and_false_otherwise()
    {
        var s = new LayoutSet(new[] { Cs(), En() });
        Assert.True(s.Contains(Cs()));
        Assert.True(s.Contains(En()));
        Assert.False(s.Contains(CsQwertz()));
    }

    [Fact]
    public void Sorted_is_deterministic_by_lang_then_klid()
    {
        var s = new LayoutSet(new[] { En(), CsQwertz(), Cs() });
        var ordered = s.Sorted().ToArray();

        Assert.Equal(Cs(), ordered[0]);
        Assert.Equal(CsQwertz(), ordered[1]);
        Assert.Equal(En(), ordered[2]);
    }

    [Fact]
    public void LayoutId_factory_rejects_zero_lang_id()
    {
        Assert.Throws<ArgumentException>(() => LayoutId.Create(0, "00000405"));
    }

    [Fact]
    public void LayoutId_factory_rejects_non_eight_hex_klid()
    {
        Assert.Throws<ArgumentException>(() => LayoutId.Create(0x0405, "405"));
        Assert.Throws<ArgumentException>(() => LayoutId.Create(0x0405, "ZZZZZZZZ"));
    }
}
