using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class RunDecisionPanel : PanelContainer
{
    private Label _title = null!;
    private Label _recommendation = null!;
    private Label _note = null!;
    private VBoxContainer _rows = null!;
    private Button _execute = null!;
    private CheckButton _automatic = null!;
    private bool _refreshing;

    public static RunDecisionPanel Create(NGame host, Action execute, Action<bool> automatic)
    {
        CanvasLayer layer = new() { Name = "CombatSolverRunDecisions", Layer = 121 };
        host.AddChild(layer);
        RunDecisionPanel panel = new()
        {
            Position = new(18, 120), CustomMinimumSize = new(380, 0),
            Theme = SolverUiTokens.Fonts.Theme,
        };
        layer.AddChild(panel);
        StyleBoxFlat style = new() { BgColor = SolverUiTokens.Palette.Surface,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 12, ContentMarginBottom = 12,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10 };
        panel.AddThemeStyleboxOverride("panel", style);
        VBoxContainer stack = new();
        stack.AddThemeConstantOverride("separation", 8);
        panel.AddChild(stack);
        panel._title = Text(stack, "");
        panel._title.AddThemeFontSizeOverride("font_size", 20);
        panel._recommendation = Text(stack, "");
        panel._automatic = new() { Text = SolverText.Get("局外决策自动执行"), ButtonPressed = false };
        panel._automatic.TooltipText = SolverText.Get("关闭后停止后续选择；已经开始的原生操作不会撤销。");
        panel._automatic.Toggled += enabled => { if (!panel._refreshing) automatic(enabled); };
        stack.AddChild(panel._automatic);
        ScrollContainer scroll = new() { CustomMinimumSize = new(0, 190),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        stack.AddChild(scroll);
        panel._rows = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(panel._rows);
        panel._note = Text(stack, "");
        panel._note.AddThemeFontSizeOverride("font_size", 13);
        panel._execute = new() { Text = SolverText.Get("执行推荐选择") };
        panel._execute.Pressed += execute;
        stack.AddChild(panel._execute);
        return panel;
    }

    private static Label Text(Node parent, string text)
    {
        Label label = new() { Text = text, CustomMinimumSize = new(350, 0),
            AutowrapMode = TextServer.AutowrapMode.WordSmart };
        label.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextPrimary);
        parent.AddChild(label);
        return label;
    }

    public void Present(RunDecisionView view, bool automatic)
    {
        _refreshing = true;
        _title.Text = SolverText.Format($"局外决策 · {view.Kind}");
        _recommendation.Text = SolverText.Format($"推荐：{view.Recommendation}");
        _automatic.Text = SolverText.Get("局外决策自动执行");
        _automatic.TooltipText = SolverText.Get("关闭后停止后续选择；已经开始的原生操作不会撤销。");
        _automatic.SetPressedNoSignal(automatic);
        foreach (Node row in _rows.GetChildren()) { _rows.RemoveChild(row); row.QueueFree(); }
        foreach (var row in view.Rows)
            Text(_rows, $"{row.Title}  {row.Score:+0.00;-0.00;0}\n{row.Detail}");
        _note.Text = view.Note;
        _execute.Text = SolverText.Get("执行推荐选择");
        _execute.Disabled = !view.CanExecute;
        _refreshing = false;
        Show();
    }

    public void SetExecuting()
    {
        _execute.Disabled = true;
        _execute.Text = SolverText.Get("执行中…");
    }

    public void ShowFailure(string message)
    {
        _note.Text = SolverText.Format($"局外执行已停止：{message}");
        _execute.Disabled = true;
    }
}
