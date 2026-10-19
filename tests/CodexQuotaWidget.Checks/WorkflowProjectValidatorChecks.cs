using CodexQuotaWidget.Core.ControlCenter;

/// <summary>Isolated project-binding checks. Invalid executable paths ensure no host command can run.</summary>
public static class WorkflowProjectValidatorChecks
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("Workflow project validation: " + name); count++; }
        var root = Path.Combine(@"D:\CodexQuotaWidget-GitHub\artifacts\project-validator-binding-checks", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "authorized-project"); var other = Path.Combine(root, "other-project");
        Directory.CreateDirectory(project); Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(project, "Project.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(project, "Program.cs"), "// isolated source only\n");
        var validator = new WorkflowProjectValidator(root, Path.Combine(root, "missing", "dotnet.exe"), "Project.csproj");
        var unbound = await validator.ValidateAsync(project, CancellationToken.None);
        Check(!unbound.Success && unbound.Message.Contains("构建定义未绑定", StringComparison.Ordinal), "unfrozen project cannot execute any command");
        validator.FreezeTrustedProject(project);
        var wrong = await validator.ValidateAsync(other, CancellationToken.None);
        Check(!wrong.Success && wrong.Message.Contains("构建定义未绑定", StringComparison.Ordinal), "different root cannot reuse another project's trusted build definitions");
        var missingExecutable = await validator.ValidateAsync(project.ToUpperInvariant(), CancellationToken.None);
        Check(!missingExecutable.Success && missingExecutable.Message.Contains("验证程序路径未核实", StringComparison.Ordinal), "equivalent project casing preserves binding while an unverified executable remains blocked");
        File.AppendAllText(Path.Combine(project, "Project.csproj"), "\n<!-- changed after freeze -->");
        var changed = await validator.ValidateAsync(project, CancellationToken.None);
        Check(!changed.Success && changed.Message.Contains("构建定义未绑定", StringComparison.Ordinal), "changed build definitions are rejected before host executable validation");
        return count;
    }
}
