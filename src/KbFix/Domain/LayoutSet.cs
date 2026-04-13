namespace KbFix.Domain;

/// <summary>
/// Immutable set of <see cref="LayoutId"/> values with the operations the
/// reconciliation algorithm needs. Defensive-copies on construction so callers
/// cannot mutate the contained set after the fact.
/// </summary>
internal sealed class LayoutSet
{
    private readonly HashSet<LayoutId> _items;

    public LayoutSet(IEnumerable<LayoutId> items)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        _items = new HashSet<LayoutId>(items);
    }

    public static LayoutSet Empty { get; } = new(Array.Empty<LayoutId>());

    public int Count => _items.Count;

    public bool Contains(LayoutId id) => _items.Contains(id);

    /// <summary>Items in <c>this</c> that are not in <paramref name="other"/>.</summary>
    public LayoutSet Difference(LayoutSet other)
    {
        if (other is null)
        {
            throw new ArgumentNullException(nameof(other));
        }

        return new LayoutSet(_items.Where(x => !other._items.Contains(x)));
    }

    /// <summary>Deterministic enumeration ordered by <see cref="LayoutId.Compare"/>.</summary>
    public IEnumerable<LayoutId> Sorted()
    {
        return _items
            .OrderBy(x => x, Comparer<LayoutId>.Create((a, b) => a.Compare(b)))
            .ToArray();
    }
}
