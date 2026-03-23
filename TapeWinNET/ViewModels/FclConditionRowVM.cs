using System.Windows.Input;

using FclNET;
using FclNET.Ast;

namespace TapeWinNET.ViewModels;

/// <summary>
/// ViewModel for a single FCL condition row in the visual filter editor.
/// Manages field selection, operator filtering, adaptive value input,
/// optional NOT negation, and bidirectional conversion to/from
/// <see cref="FclExpression"/> AST nodes.
/// </summary>
public class FclConditionRowVM : ViewModelBase
{
    // ─────────────────────────────────────────────────────
    //  Static operator lists per field category
    // ─────────────────────────────────────────────────────

    private static readonly FclOperator[] StringOperators =
    [
        FclOperator.Equals, FclOperator.NotEquals,
        FclOperator.Contains, FclOperator.NotContains,
        FclOperator.Matches, FclOperator.NotMatches,
        FclOperator.Regex
    ];

    private static readonly FclOperator[] DateOperators =
    [
        FclOperator.Equals, FclOperator.NotEquals,
        FclOperator.Before, FclOperator.BeforeOrOn,
        FclOperator.After, FclOperator.AfterOrOn
    ];

    private static readonly FclOperator[] SizeOperators =
    [
        FclOperator.Equals, FclOperator.NotEquals,
        FclOperator.GreaterThan, FclOperator.GreaterOrEqual,
        FclOperator.LessThan, FclOperator.LessOrEqual
    ];

    private static readonly FclOperator[] AttributeOperators =
    [
        FclOperator.Have, FclOperator.NotHave
    ];

    // ─────────────────────────────────────────────────────
    //  Backing fields
    // ─────────────────────────────────────────────────────

    private bool _isNegated;
    private FclField? _selectedField;
    private FclOperator? _selectedOperator;
    private string _valueText = string.Empty;
    private DateTime? _dateValue;
    private bool _isRelativeDate;
    private double _sizeValue;
    private FclSizeUnit _selectedSizeUnit = FclSizeUnit.MB;
    private FclAttribute? _selectedAttribute;

    // ─────────────────────────────────────────────────────
    //  Construction
    // ─────────────────────────────────────────────────────

    public FclConditionRowVM()
    {
        RemoveCommand = new RelayCommand(
            _ => RemoveRequested?.Invoke(this),
            _ => CanRemove);
    }

    // ─────────────────────────────────────────────────────
    //  Properties — condition definition
    // ─────────────────────────────────────────────────────

    /// <summary>Whether this condition is negated (<c>NOT</c>).</summary>
    public bool IsNegated
    {
        get => _isNegated;
        set { if (SetProperty(ref _isNegated, value)) Modified?.Invoke(); }
    }

    /// <summary>The selected field (left-hand side of the condition).</summary>
    public FclField? SelectedField
    {
        get => _selectedField;
        set
        {
            if (!SetProperty(ref _selectedField, value))
                return;

            // Recompute derived state when the field changes.
            OnPropertyChanged(nameof(ActiveCategory));
            OnPropertyChanged(nameof(AvailableOperators));

            // Reset operator to the first available (or null) if the
            //  current operator is not valid for the new field category.
            if (SelectedOperator is { } op && !AvailableOperators.Contains(op))
                SelectedOperator = AvailableOperators.Length > 0 ? AvailableOperators[0] : null;

            // Clear value inputs on category change to avoid stale state.
            ClearValueInputs();

            Modified?.Invoke();
        }
    }

    /// <summary>The selected comparison operator.</summary>
    public FclOperator? SelectedOperator
    {
        get => _selectedOperator;
        set { if (SetProperty(ref _selectedOperator, value)) Modified?.Invoke(); }
    }

    // ─────────────────────────────────────────────────────
    //  Properties — value inputs (one active per category)
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// String value — used for String fields (patterns, substrings, regex, etc.).
    /// </summary>
    public string ValueText
    {
        get => _valueText;
        set { if (SetProperty(ref _valueText, value)) Modified?.Invoke(); }
    }

    /// <summary>
    /// Absolute date value — used for Date fields when <see cref="IsRelativeDate"/> is false.
    /// </summary>
    public DateTime? DateValue
    {
        get => _dateValue;
        set
        {
            if (!SetProperty(ref _dateValue, value))
                return;
            OnPropertyChanged(nameof(RelativeDateHint));
            Modified?.Invoke();
        }
    }

    /// <summary>
    /// When checked, the date is stored as a relative offset from today
    /// (e.g. <c>today-7d</c>) instead of an absolute date.
    /// </summary>
    public bool IsRelativeDate
    {
        get => _isRelativeDate;
        set
        {
            if (!SetProperty(ref _isRelativeDate, value))
                return;
            OnPropertyChanged(nameof(RelativeDateHint));
            Modified?.Invoke();
        }
    }

    /// <summary>
    /// Human-readable hint showing the computed relative expression,
    /// e.g. <c>"= today − 7d"</c>. Visible only when <see cref="IsRelativeDate"/> is true
    /// and <see cref="DateValue"/> is set.
    /// </summary>
    public string? RelativeDateHint
    {
        get
        {
            if (!IsRelativeDate || DateValue is not { } date)
                return null;

            int days = (date.Date - DateTime.Today).Days;
            return days switch
            {
                0 => "= today",
                > 0 => $"= today+{days}d",
                _ => $"= today\u2212{Math.Abs(days)}d" // minus sign: −
            };
        }
    }

    /// <summary>Numeric part of the size value.</summary>
    public double SizeValue
    {
        get => _sizeValue;
        set { if (SetProperty(ref _sizeValue, value)) Modified?.Invoke(); }
    }

    /// <summary>Unit for the size value (KB, MB, GB, etc.).</summary>
    public FclSizeUnit SelectedSizeUnit
    {
        get => _selectedSizeUnit;
        set { if (SetProperty(ref _selectedSizeUnit, value)) Modified?.Invoke(); }
    }

    /// <summary>Selected attribute value — used for Attributes field.</summary>
    public FclAttribute? SelectedAttribute
    {
        get => _selectedAttribute;
        set { if (SetProperty(ref _selectedAttribute, value)) Modified?.Invoke(); }
    }

    // ─────────────────────────────────────────────────────
    //  Properties — derived / UI support
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// The field category of <see cref="SelectedField"/>, driving which
    /// value input controls are visible. <c>null</c> when no field is selected.
    /// </summary>
    public FclFieldCategory? ActiveCategory =>
        SelectedField is { } f ? FclFieldTranslator.GetCategory(f) : null;

    /// <summary>
    /// Operators available for the currently selected field.
    /// Empty when no field is selected.
    /// </summary>
    public FclOperator[] AvailableOperators => ActiveCategory switch
    {
        FclFieldCategory.String => StringOperators,
        FclFieldCategory.Date => DateOperators,
        FclFieldCategory.Size => SizeOperators,
        FclFieldCategory.Attribute => AttributeOperators,
        _ => []
    };

    /// <summary>All FCL fields, for the Field combobox.</summary>
    public static FclField[] AllFields { get; } =
        Enum.GetValues<FclField>();

    /// <summary>All size units, for the Size unit combobox.</summary>
    public static FclSizeUnit[] AllSizeUnits { get; } =
        Enum.GetValues<FclSizeUnit>();

    /// <summary>All attributes, for the Attribute combobox.</summary>
    public static FclAttribute[] AllAttributes { get; } =
        Enum.GetValues<FclAttribute>();

    /// <summary>
    /// Whether this condition row can be removed.
    /// Set to <c>false</c> for the last remaining row in a group.
    /// </summary>
    public bool CanRemove { get; set; } = true;

    // ─────────────────────────────────────────────────────
    //  Commands & events
    // ─────────────────────────────────────────────────────

    /// <summary>Command to request removal of this row from its group.</summary>
    public ICommand RemoveCommand { get; }

    /// <summary>
    /// Raised when the user clicks the remove button. The owning
    /// <see cref="FclConditionGroupVM"/> subscribes to perform the removal.
    /// </summary>
    public event Action<FclConditionRowVM>? RemoveRequested;

    /// <summary>
    /// Raised when any data property (field, operator, value, negation) changes,
    /// signalling that the visual program has been modified.
    /// </summary>
    public event Action? Modified;

    // ─────────────────────────────────────────────────────
    //  AST conversion: ViewModel → Expression
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when the row has enough data to produce
    /// a valid <see cref="FclExpression"/> (field, operator, and value set).
    /// </summary>
    public bool IsComplete => SelectedField is not null
        && SelectedOperator is not null
        && IsValueComplete;

    /// <summary>
    /// Builds an <see cref="FclExpression"/> from the current row state.
    /// Returns <c>null</c> if the row is incomplete.
    /// Wraps in <see cref="FclNotExpression"/> when <see cref="IsNegated"/> is true.
    /// </summary>
    public FclExpression? ToExpression()
    {
        if (!IsComplete)
            return null;

        var field = SelectedField!.Value;
        var op = SelectedOperator!.Value;
        var value = BuildValue(field, op);
        if (value is null)
            return null;

        FclExpression condition = new FclCondition(
            field, SourceSpan.None,
            op, SourceSpan.None,
            value, SourceSpan.None);

        return IsNegated
            ? new FclNotExpression(condition, SourceSpan.None)
            : condition;
    }

    // ─────────────────────────────────────────────────────
    //  AST conversion: Expression → ViewModel
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Populates the row's properties from an existing AST expression.
    /// Handles <see cref="FclCondition"/> and <c>NOT(FclCondition)</c>.
    /// </summary>
    public void FromExpression(FclExpression expression)
    {
        // Unwrap NOT
        if (expression is FclNotExpression not)
        {
            IsNegated = true;
            expression = not.Operand;
        }
        else
        {
            IsNegated = false;
        }

        if (expression is not FclCondition cond)
            return;

        // Set field first (triggers operator list update)
        SelectedField = cond.Field;
        SelectedOperator = cond.Operator;

        // Populate the appropriate value input
        switch (cond.Value)
        {
            case FclStringValue sv:
                ValueText = sv.Value;
                break;

            case FclAbsoluteDateValue adv:
                DateValue = adv.Value;
                IsRelativeDate = false;
                break;

            case FclRelativeDateValue rdv:
                // Resolve relative date to absolute for the DatePicker,
                //  and check the "relative" box so ToExpression re-computes the offset.
                DateValue = rdv.Resolve();
                IsRelativeDate = true;
                break;

            case FclSizeValue szv:
                SizeValue = szv.NumericValue;
                SelectedSizeUnit = szv.Unit;
                break;

            case FclAttributeValue av:
                SelectedAttribute = av.Attribute;
                break;
        }
    }

    // ─────────────────────────────────────────────────────
    //  Display helpers
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns a user-friendly display name for an <see cref="FclOperator"/>,
    /// suitable for ComboBox display (e.g. "greater than" instead of "greaterThan").
    /// </summary>
    public static string GetOperatorDisplayName(FclOperator op) => op switch
    {
        FclOperator.Equals => "equals",
        FclOperator.NotEquals => "not equals",
        FclOperator.Contains => "contains",
        FclOperator.NotContains => "not contains",
        FclOperator.Matches => "matches",
        FclOperator.NotMatches => "not matches",
        FclOperator.Regex => "regex",
        FclOperator.Before => "before",
        FclOperator.BeforeOrOn => "before or on",
        FclOperator.After => "after",
        FclOperator.AfterOrOn => "after or on",
        FclOperator.GreaterThan => "greater than",
        FclOperator.GreaterOrEqual => "greater or equal",
        FclOperator.LessThan => "less than",
        FclOperator.LessOrEqual => "less or equal",
        FclOperator.Have => "have",
        FclOperator.NotHave => "not have",
        _ => op.ToString()
    };

    /// <summary>
    /// Returns a user-friendly display name for an <see cref="FclField"/>
    /// (e.g. "Full Name" instead of "FullName").
    /// </summary>
    public static string GetFieldDisplayName(FclField field) => field switch
    {
        FclField.FullName => "Full Name",
        FclField.Name => "Name",
        FclField.Extension => "Extension",
        FclField.Path => "Path",
        FclField.Size => "Size",
        FclField.Created => "Created",
        FclField.Modified => "Modified",
        FclField.Attributes => "Attributes",
        _ => field.ToString()
    };

    // ─────────────────────────────────────────────────────
    //  Private helpers
    // ─────────────────────────────────────────────────────

    /// <summary>Whether the current value input has a usable value.</summary>
    private bool IsValueComplete => ActiveCategory switch
    {
        FclFieldCategory.String => !string.IsNullOrWhiteSpace(ValueText),
        FclFieldCategory.Date => DateValue is not null,
        FclFieldCategory.Size => true, // 0 is a valid size
        FclFieldCategory.Attribute => SelectedAttribute is not null,
        _ => false
    };

    /// <summary>
    /// Builds the appropriate <see cref="FclValue"/> AST node from the
    /// current value inputs.
    /// </summary>
    private FclValue? BuildValue(FclField field, FclOperator op)
    {
        var category = FclFieldTranslator.GetCategory(field);

        switch (category)
        {
            case FclFieldCategory.String:
            {
                var text = ValueText.Trim();
                // wasQuoted=false — the formatter will add quotes when needed
                return new FclStringValue(text, wasQuoted: false, SourceSpan.None);
            }

            case FclFieldCategory.Date:
            {
                if (DateValue is not { } date)
                    return null;

                if (IsRelativeDate)
                {
                    // Compute offset in days from today
                    int days = (date.Date - DateTime.Today).Days;
                    return new FclRelativeDateValue(
                        FclDateAnchor.Today, days, FclDateUnit.Days, SourceSpan.None);
                }
                else
                {
                    return new FclAbsoluteDateValue(date, hasTime: false, SourceSpan.None);
                }
            }

            case FclFieldCategory.Size:
            {
                long bytes = (long)(SizeValue * GetSizeMultiplier(SelectedSizeUnit));
                return new FclSizeValue(SizeValue, SelectedSizeUnit, bytes, SourceSpan.None);
            }

            case FclFieldCategory.Attribute:
            {
                if (SelectedAttribute is not { } attr)
                    return null;
                return new FclAttributeValue(attr, SourceSpan.None);
            }

            default:
                return null;
        }
    }

    /// <summary>Clears all value inputs when the field category changes.</summary>
    private void ClearValueInputs()
    {
        ValueText = string.Empty;
        DateValue = null;
        IsRelativeDate = false;
        SizeValue = 0;
        SelectedSizeUnit = FclSizeUnit.MB;
        SelectedAttribute = null;
    }

    /// <summary>Returns the byte multiplier for the given size unit.</summary>
    private static double GetSizeMultiplier(FclSizeUnit unit) => unit switch
    {
        FclSizeUnit.Bytes => 1,
        FclSizeUnit.KB => 1024,
        FclSizeUnit.MB => 1024 * 1024,
        FclSizeUnit.GB => 1024 * 1024 * 1024,
        FclSizeUnit.TB => 1024L * 1024 * 1024 * 1024,
        _ => 1
    };
}
