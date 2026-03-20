using System.Collections.ObjectModel;
using System.Windows.Input;

using FclNET;
using FclNET.Ast;

namespace TapeWinNET.ViewModels;

/// <summary>
/// ViewModel for a group of AND-connected FCL conditions in the visual
/// filter editor. Each group corresponds to one conjunctive clause in
/// the overall DNF expression.
/// </summary>
public class FclConditionGroupVM : ViewModelBase
{
    private bool _isFirst;

    public FclConditionGroupVM()
    {
        AddConditionCommand = new RelayCommand(
            _ => AddCondition());
        RemoveGroupCommand = new RelayCommand(
            _ => RemoveRequested?.Invoke(this),
            _ => CanRemoveGroup);
    }

    // ─────────────────────────────────────────────────────
    //  Properties
    // ─────────────────────────────────────────────────────

    /// <summary>The AND-connected conditions in this group.</summary>
    public ObservableCollection<FclConditionRowVM> Conditions { get; } = [];

    /// <summary>
    /// Whether this is the first group (hides the OR separator above it).
    /// </summary>
    public bool IsFirst
    {
        get => _isFirst;
        set => SetProperty(ref _isFirst, value);
    }

    // ─────────────────────────────────────────────────────
    //  Commands
    // ─────────────────────────────────────────────────────

    /// <summary>Adds a new empty condition row to the group.</summary>
    public ICommand AddConditionCommand { get; }

    /// <summary>Removes this entire group from the visual editor.</summary>
    public ICommand RemoveGroupCommand { get; }

    /// <summary>
    /// Whether this group can be removed. Non-first groups are always
    /// removable; the first group can be removed only when other groups exist.
    /// Set by the owning <see cref="FclFilterWindowVM"/>.
    /// </summary>
    public bool CanRemoveGroup { get; set; }

    /// <summary>
    /// Raised when the user clicks the remove-group button.
    /// The owning dialog VM subscribes to perform the removal.
    /// </summary>
    public event Action<FclConditionGroupVM>? RemoveRequested;

    /// <summary>
    /// Raised when the last condition is removed, meaning this group
    /// is now empty and should be removed by the owning dialog VM.
    /// </summary>
    public event Action<FclConditionGroupVM>? GroupEmptied;

    // ─────────────────────────────────────────────────────
    //  Public methods
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Adds a new empty condition row and subscribes to its removal event.
    /// </summary>
    public FclConditionRowVM AddCondition()
    {
        var row = new FclConditionRowVM();
        row.RemoveRequested += OnConditionRemoveRequested;
        Conditions.Add(row);
        UpdateCanRemove();
        return row;
    }

    /// <summary>
    /// Builds an <see cref="FclExpression"/> from all complete conditions
    /// in this group, AND-chaining them. Returns <c>null</c> if no
    /// conditions are complete.
    /// </summary>
    public FclExpression? ToExpression()
    {
        var expressions = new List<FclExpression>();
        foreach (var row in Conditions)
        {
            var expr = row.ToExpression();
            if (expr is not null)
                expressions.Add(expr);
        }

        return expressions.Count switch
        {
            0 => null,
            1 => expressions[0],
            _ => new FclAndExpression([.. expressions], SourceSpan.None)
        };
    }

    /// <summary>
    /// Populates this group from an AND-expression or a single literal.
    /// Clears existing conditions first.
    /// </summary>
    public void FromExpression(FclExpression expression)
    {
        Clear();

        // Unwrap AND chain into individual literals
        var literals = ExtractAndOperands(expression);
        foreach (var literal in literals)
        {
            var row = AddCondition();
            row.FromExpression(literal);
        }
    }

    /// <summary>Removes all conditions from this group.</summary>
    public void Clear()
    {
        foreach (var row in Conditions)
            row.RemoveRequested -= OnConditionRemoveRequested;
        Conditions.Clear();
    }

    // ─────────────────────────────────────────────────────
    //  Private helpers
    // ─────────────────────────────────────────────────────

    private void OnConditionRemoveRequested(FclConditionRowVM row)
    {
        row.RemoveRequested -= OnConditionRemoveRequested;
        Conditions.Remove(row);

        if (Conditions.Count == 0)
        {
            // Signal to the parent that this group is empty
            GroupEmptied?.Invoke(this);
        }
        else
        {
            UpdateCanRemove();
        }
    }

    /// <summary>
    /// Updates <see cref="FclConditionRowVM.CanRemove"/> on all rows.
    /// The last remaining condition in a group cannot be removed
    /// (the user should remove the whole group instead).
    /// </summary>
    private void UpdateCanRemove()
    {
        foreach (var row in Conditions)
            row.CanRemove = Conditions.Count > 1;
    }

    /// <summary>
    /// Extracts the operands from an AND-expression, or returns a
    /// single-element list for a literal/NOT expression.
    /// </summary>
    private static List<FclExpression> ExtractAndOperands(FclExpression expression)
    {
        if (expression is FclAndExpression and)
            return [.. and.Operands];

        // Single literal or NOT(literal)
        return [expression];
    }
}
