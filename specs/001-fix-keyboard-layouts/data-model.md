# Phase 1 Data Model: Fix Windows Session Keyboard Layouts

**Feature**: 001-fix-keyboard-layouts
**Date**: 2026-04-10

The utility is a one-shot CLI with no persistent storage of its own. The
"data model" therefore describes the in-memory entities the program builds
during a single run. All entities are immutable value objects unless noted.

## Entities

### LayoutId

Stable identifier for a single keyboard layout / input profile.

| Field        | Type           | Notes                                                                       |
|--------------|----------------|-----------------------------------------------------------------------------|
| `langId`     | `ushort`       | Windows LANGID (low word of an HKL), e.g. `0x0405` for cs-CZ.               |
| `klid`       | `string`       | Keyboard Layout ID, 8-hex-digit string from registry, e.g. `"00000405"`.    |
| `profileGuid`| `Guid?`        | Text Service profile GUID for TSF profiles; null for pure legacy HKLs.      |

**Equality**: two `LayoutId` values are equal iff `(langId, klid, profileGuid)`
match. Comparing only by `langId` is forbidden — multiple layouts can share a
language (the bug we're fixing relies on this distinction, e.g. cs-CZ QWERTY
vs cs-CZ QWERTZ).

**Validation**:
- `klid` MUST be exactly 8 hex digits.
- `langId` MUST be non-zero.
- If `profileGuid` is present it MUST be a valid GUID.

### LayoutSet

An unordered set of `LayoutId` values.

| Field    | Type                 | Notes                                                  |
|----------|----------------------|--------------------------------------------------------|
| `items`  | `IReadOnlySet<LayoutId>` | Set semantics: no duplicates, order irrelevant.    |

Operations (pure):
- `Difference(other)` → `LayoutSet` of items in `this` but not in `other`.
- `Contains(layoutId)` → `bool`.
- `Count` → `int`.

### PersistedConfig

The desired-state snapshot read from `HKCU\Keyboard Layout\Preload` (with
`Substitutes` applied).

| Field      | Type        | Notes                                                  |
|------------|-------------|--------------------------------------------------------|
| `layouts`  | `LayoutSet` | Authoritative desired state. Read-only.                |
| `readAt`   | `DateTime`  | UTC timestamp of the read; used in the report only.    |

**Validation**:
- `layouts.Count >= 1` — if zero, the program MUST refuse to reconcile and
  exit with code 3 (FR-008, edge case in spec).

### SessionState

The actual-state snapshot read from TSF
(`ITfInputProcessorProfileMgr::EnumProfiles`).

| Field             | Type        | Notes                                                                |
|-------------------|-------------|----------------------------------------------------------------------|
| `layouts`         | `LayoutSet` | All keyboard input profiles currently active in the user session.    |
| `activeLayout`    | `LayoutId`  | The profile currently in the foreground at read time.                |
| `readAt`          | `DateTime`  | UTC timestamp of the read.                                           |

**Validation**:
- `layouts.Contains(activeLayout)` MUST hold.

### ReconciliationPlan

The diff between persisted and session state, plus the ordered actions
required to converge them. Pure function of `(PersistedConfig, SessionState)`.

| Field             | Type                       | Notes                                                                       |
|-------------------|----------------------------|-----------------------------------------------------------------------------|
| `toRemove`        | `IReadOnlyList<LayoutId>`  | Session-only layouts that should be deactivated.                            |
| `mustSwitchFirst` | `LayoutId?`                | Non-null iff `activeLayout ∈ toRemove`. The persisted layout to switch to.  |
| `noOp`            | `bool`                     | True iff `toRemove` is empty.                                               |
| `refuse`          | `bool`                     | True iff persisted set is empty (FR-008 / edge case).                       |
| `refuseReason`    | `string?`                  | Human-readable reason if `refuse` is true.                                  |

**Construction rules** (the unit-tested core):

1. If `persisted.layouts.Count == 0` → `refuse = true`, no other fields set.
2. `toRemove = session.layouts.Difference(persisted.layouts)` — preserving
   sorted order by `(langId, klid)` for deterministic reporting.
3. If `toRemove.Contains(session.activeLayout)`:
   - Pick the first layout in `persisted.layouts` (sorted by `langId, klid`)
     that is also in `session.layouts`. Assign it to `mustSwitchFirst`.
   - If no such layout exists (i.e. the session has *none* of the persisted
     layouts active — pathological), `refuse = true` with reason
     "session has no persisted layout to fall back to".
4. `noOp = (toRemove.Count == 0 && !refuse)`.

### ReconciliationReport

The human-readable artifact emitted to stdout (FR-011). Built from a
`PersistedConfig`, a `SessionState`, a `ReconciliationPlan`, and an
execution outcome.

| Field            | Type                       | Notes                                       |
|------------------|----------------------------|---------------------------------------------|
| `persistedList`  | `IReadOnlyList<LayoutId>`  | Sorted for stable output.                   |
| `sessionList`    | `IReadOnlyList<LayoutId>`  | Sorted.                                     |
| `actionsTaken`   | `IReadOnlyList<Action>`    | See `Action` below.                         |
| `dryRun`         | `bool`                     | When true, actions are prefixed `(dry-run)`.|
| `outcome`        | `Outcome`                  | `Success` / `NoOp` / `Refused` / `Failed`.  |
| `errorDetail`    | `string?`                  | Populated when `outcome == Failed`.         |

### Action

A single mutation the utility performed (or, in dry-run, would perform).

| Field        | Type        | Notes                                              |
|--------------|-------------|----------------------------------------------------|
| `kind`       | `enum`      | `SwitchActive` \| `Deactivate`.                    |
| `layoutId`   | `LayoutId`  | The target layout for this action.                 |
| `succeeded`  | `bool`      | False if the underlying API call failed.          |
| `failure`    | `string?`   | Win32/HRESULT detail when `succeeded == false`.    |

### Outcome (enum)

| Value      | Exit code | Meaning                                                              |
|------------|-----------|----------------------------------------------------------------------|
| `Success`  | 0         | At least one removal succeeded; final state matches persisted.       |
| `NoOp`     | 0         | Nothing to do; states already matched.                               |
| `Refused`  | 3         | Edge case (empty persisted set, or no fallback layout).              |
| `Failed`   | 1 or 2    | 2 if unsupported platform / missing API, 1 for any other failure.    |

## State Transitions

A single run proceeds through fixed phases:

```text
Read Persisted ──► Read Session ──► Build Plan ──► (dry-run? ─► Report ─► Exit)
                                          │
                                          └──► Switch Active (if needed) ──►
                                              Deactivate Removals ──►
                                              Re-read Session (verify) ──►
                                              Report ──► Exit
```

There is no persistence between runs; each invocation is independent.
