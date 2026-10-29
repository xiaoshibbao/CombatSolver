using Godot;

namespace CombatSolver;

internal readonly record struct SolverActionBarState(bool Collapsed, bool Searching, bool ShowAdopt, bool ShowFreeze);

// Owns layout only. The overlay retains command bindings and capability checks.
// The turn command spans the rail; secondary commands share a two-column grid below it.
internal sealed partial class SolverActionBar : VBoxContainer
{
    private readonly GridContainer _actions;
    private readonly Control _autoStart;
    private readonly HBoxContainer _memoryRow;
    private readonly Button _execute;
    private readonly Button _recalculate;
    private readonly Button _stop;
    private readonly Button _adopt;
    private readonly Button _freeze;
    private readonly Button _fullAuto;
    private readonly Control _memory;

    public SolverActionBar(Button execute, Button recalculate, Button stop, Button adopt, Button freeze,
        Button fullAuto, Control autoStart, Control memory, Button releaseMemory)
    {
        Name = "Footer";
        MouseFilter = MouseFilterEnum.Pass;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _execute = execute;
        _recalculate = recalculate;
        _stop = stop;
        _adopt = adopt;
        _freeze = freeze;
        _fullAuto = fullAuto;
        _memory = memory;
        _autoStart = autoStart;

        HBoxContainer primaryRow = new() { Name = "PrimaryRow", MouseFilter = MouseFilterEnum.Pass };
        primaryRow.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        foreach (Button command in new[] { execute, stop })
        {
            FillCell(command);
            command.CustomMinimumSize = new Vector2(0, SolverUiTokens.Size.PrimaryButtonHeight);
            primaryRow.AddChild(command);
        }
        AddChild(primaryRow);

        _actions = new GridContainer
        {
            Name = "CombatActions",
            Columns = 2,
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _actions.AddThemeConstantOverride("h_separation", SolverUiTokens.Spacing.Sm);
        _actions.AddThemeConstantOverride("v_separation", SolverUiTokens.Spacing.Sm);
        foreach (Button command in new[] { fullAuto, adopt, freeze, recalculate })
        {
            FillCell(command);
            _actions.AddChild(command);
        }
        AddChild(_actions);

        AddChild(autoStart);
        _memoryRow = new HBoxContainer { Name = "MemoryRow", MouseFilter = MouseFilterEnum.Pass };
        _memoryRow.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _memoryRow.AddChild(memory);
        _memoryRow.AddChild(releaseMemory);
        AddChild(_memoryRow);
    }

    public void Refresh(SolverActionBarState state)
    {
        _recalculate.Visible = !state.Searching;
        _stop.Visible = state.Searching;
        _adopt.Visible = state.ShowAdopt;
        _freeze.Visible = !state.Searching && state.ShowFreeze;
        _execute.Visible = !state.Searching;
        _autoStart.Visible = !state.Collapsed;
        _memoryRow.Visible = !state.Collapsed;
        _memory.Visible = !state.Collapsed;
    }

    internal void AssertLayoutForTesting()
    {
        bool originalAdoptDisabled = _adopt.Disabled;
        bool originalExecuteDisabled = _execute.Disabled;
        try
        {
            foreach (bool collapsed in new[] { false, true })
            foreach (bool searching in new[] { false, true })
            foreach (bool adopt in new[] { false, true })
            foreach (bool freeze in new[] { false, true })
            foreach (bool adoptDisabled in new[] { false, true })
            foreach (bool executeDisabled in new[] { false, true })
            {
                _adopt.Disabled = adoptDisabled;
                _execute.Disabled = executeDisabled;
                Refresh(new SolverActionBarState(collapsed, searching, adopt, freeze));
                if (_stop.Visible != searching || _recalculate.Visible == searching
                    || _adopt.Visible != adopt
                    || _adopt.Disabled != adoptDisabled
                    || _execute.Disabled != executeDisabled
                    || _freeze.Visible != (!searching && freeze)
                    || _execute.Visible == searching
                    || _memory.Visible == collapsed || _autoStart.Visible == collapsed
                    || _memoryRow.Visible == collapsed
                    || _execute.GetParent() != _stop.GetParent()
                    || _fullAuto.GetParent() != _actions || _fullAuto.GetIndex() != 0
                    || _adopt.GetIndex() != 1 || _freeze.GetIndex() != 2)
                    throw new InvalidOperationException("Action bar layout state did not match its display snapshot.");
            }
        }
        finally
        {
            _adopt.Disabled = originalAdoptDisabled;
            _execute.Disabled = originalExecuteDisabled;
        }
    }

    // Commands size to their cell; fixed widths from the old single-row bar would overflow the rail.
    private static void FillCell(Button command)
    {
        command.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        command.CustomMinimumSize = new Vector2(0, command.CustomMinimumSize.Y);
    }
}
