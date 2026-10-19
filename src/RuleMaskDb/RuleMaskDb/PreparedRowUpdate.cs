using System.Collections.Immutable;

namespace RuleMaskDb;

/// <summary>
/// A row's selected assignments. Values contain the row keys followed by the changed values,
/// in the same order as the rule indices.
/// </summary>
internal sealed record PreparedRowUpdate(ImmutableArray<int> ChangedRuleIndices, ImmutableArray<object?> Values)
{
    public bool HasChanges => !ChangedRuleIndices.IsEmpty;
}
