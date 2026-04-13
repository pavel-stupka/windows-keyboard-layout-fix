namespace KbFix.Domain;

/// <summary>
/// Snapshot of the keyboard input profiles currently active in the user's
/// Windows session, plus the foreground layout at the moment of capture.
/// </summary>
internal sealed record SessionState
{
    public LayoutSet Layouts { get; }
    public LayoutId ActiveLayout { get; }
    public DateTime ReadAt { get; }

    public SessionState(LayoutSet layouts, LayoutId activeLayout, DateTime readAt)
    {
        if (layouts is null)
        {
            throw new ArgumentNullException(nameof(layouts));
        }

        if (!layouts.Contains(activeLayout))
        {
            throw new ArgumentException(
                "ActiveLayout must be a member of Layouts.", nameof(activeLayout));
        }

        Layouts = layouts;
        ActiveLayout = activeLayout;
        ReadAt = readAt;
    }
}
