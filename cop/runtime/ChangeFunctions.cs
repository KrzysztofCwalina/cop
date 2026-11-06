// Registers runtime functions that expose source changes to Cop programs.

using Cop.Core;
using Cop.Lang;
using Cop.Lang.Interpreter;

namespace Cop.Providers;

internal static class ChangeFunctions
{
    /// <summary>Registers change-detection functions for the analysis context.</summary>
    public static void Register(LanguageBridge bridge, AnalysisContext context)
    {
        bridge.RegisterFunction("changeReportsAll", (_, _, _) =>
            ValueTask.FromResult<CopValue>(
                CopBool.Of(context.Options.ReportScope == "all")), 0);
        bridge.RegisterFunction("changeMatches", async (args, _, cancellationToken) =>
            CopBool.Of((await context.GetChangesAsync(cancellationToken))
                .IsChanged(PathArgument(args), LineArgument(args))), 2);
        bridge.RegisterFunction("changeReportIncludes", async (args, _, cancellationToken) =>
            CopBool.Of(context.Options.ReportScope == "all" ||
                string.IsNullOrEmpty(PathArgument(args)) ||
                (await context.GetChangesAsync(cancellationToken))
                    .IsChanged(PathArgument(args), LineArgument(args))), 2);
        bridge.RegisterFunction("changeFiles", async (_, _, cancellationToken) =>
        {
            ChangeSnapshot snapshot = await context.GetChangesAsync(cancellationToken);
            IEnumerable<CopValue> files = snapshot.Files.Select(file =>
            {
                DataObject data = new DataObject("ChangedFile");
                data.Set("Path", file.Path);
                data.Set("OldPath", file.OldPath ?? "");
                data.Set("Status", file.Status);
                data.Set("Patch", file.Patch);
                data.Set("IsBinary", file.IsBinary);
                return (CopValue)new CopDynamicObject(data, DataObjectAdapter.Instance);
            });
            return new CopList(files.ToList());
        }, 0);
    }

    private static string PathArgument(IReadOnlyList<CopValue> args) =>
        args.Count >= 1 && args[0] is CopString path ? path.Value :
            throw new ArgumentException("Change queries require a source path string.");

    private static int LineArgument(IReadOnlyList<CopValue> args) =>
        args.Count >= 2 && args[1] is CopInt line ? line.Value :
            throw new ArgumentException("Change queries require an integer source line.");
}
