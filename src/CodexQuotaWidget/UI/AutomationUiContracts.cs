using System.Collections.Generic;
using System.Threading.Tasks;

namespace CodexQuotaWidget;

/// <summary>UI boundary for saved, typed local plans. TestAsync is always non-destructive.</summary>
public interface IAutomationUiController
{
    AutomationUiState GetState();
    AutomationUiSaveResult Save(bool enabled, string text);
    Task<string> TestAsync(string text);
    string ReadLastHistory();
}

public sealed record AutomationUiState(bool Enabled, string Text, string Status, IReadOnlyList<string>? Steps = null);
public sealed record AutomationUiSaveResult(bool Success, string Message, IReadOnlyList<string> Steps);
