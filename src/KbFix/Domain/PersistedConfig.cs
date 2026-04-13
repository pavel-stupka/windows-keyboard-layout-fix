namespace KbFix.Domain;

/// <summary>
/// Snapshot of the user's persisted (HKCU) keyboard layout configuration.
/// Authoritative desired-state — never written by this utility.
/// </summary>
internal sealed record PersistedConfig(LayoutSet Layouts, DateTime ReadAt);
