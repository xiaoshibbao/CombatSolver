using System.Reflection;
using System.Text;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertProcessDiagnosticsAsync()
    {
        WrapperRegistrySnapshot wrappers = await Task.Run(PerformanceRecording.CreateWrapperRegistryProbe());
        if (wrappers.GodotObjects <= 0 || wrappers.OtherWrappers <= 0)
            throw new InvalidOperationException("Live Godot registry counters were not available to the background sampler.");
        _completedChecks.Add("ProcessDiagnostics:BackgroundGodotRegistryCounts");
        using CombatDiagnosticJournal journal = new(Path.Combine(Path.GetTempPath(), "CombatSolver-process-log-tests"));
        journal.BeginCombat("first", "fixture", "seed");
        journal.Write("info", "[CombatSolver/Test] HEAP_RECLAIM fixture=first");
        journal.Write("info", "[CombatSolver/Test] MEMORY_MONITOR_DISPLAY fixture=excluded");
        journal.EndCombat("fixture");
        journal.BeginCombat("second", "fixture", "seed");
        var archive = await journal.CaptureAsync();
        string process = Encoding.UTF8.GetString(archive.Process.JsonLines);
        string current = Encoding.UTF8.GetString(archive.Events.JsonLines);
        if (archive.Process.Error != null || !process.Contains("HEAP_RECLAIM fixture=first")
            || process.Contains("fixture=excluded") || current.Contains("fixture=first")
            || !process.Contains("COMBAT_LOG_BEGIN id=second"))
            throw new InvalidOperationException("Cross-combat performance evidence was lost or included per-frame samples.");
        _completedChecks.Add("ProcessDiagnostics:SavedRunPendingNetService:CrossCombatGcEvidence:NoDisplaySamples");
    }
}
