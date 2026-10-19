using System.Text.Json.Serialization;

namespace CodexQuotaWidget.Core.Automation;

[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticsOutcome>))]
public enum DiagnosticsOutcome { RealPassed, SimulatedPassed, Failed, Unknown, NotVerified }

public sealed record AutomationDiagnosticItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public DiagnosticsOutcome Outcome { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<string> Evidence { get; init; } = [];
}

/// <summary>Read-only diagnostics, never execution authorization. Safety flags cannot be enabled by JSON input.</summary>
public sealed record AutomationDiagnosticsReport
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public bool DryRun => true;
    public bool NoProcessesClosed => true;
    public bool NoPowerAction => true;
    public IReadOnlyList<AutomationDiagnosticItem> Items { get; init; } = [];
    public string ReportPath { get; init; } = "";
    public int RealPassed => Items.Count(item => item.Outcome == DiagnosticsOutcome.RealPassed);
    public int SimulatedPassed => Items.Count(item => item.Outcome == DiagnosticsOutcome.SimulatedPassed);
    public int Failed => Items.Count(item => item.Outcome == DiagnosticsOutcome.Failed);
    public int Unknown => Items.Count(item => item.Outcome == DiagnosticsOutcome.Unknown);
    public int NotVerified => Items.Count(item => item.Outcome == DiagnosticsOutcome.NotVerified);
}
