using Cop.Core;
using Cop.Lang;
using Cop.Lang.Interpreter;

namespace Cop.Providers;

internal static class ChangeFunctions
{
    public static void Register(LanguageBridge bridge, AnalysisContext context)
    {
        bridge.RegisterFunction("changeReportsAll", (_, _) =>
            CopBool.Of(context.Options.ReportScope == "all"), 0);
        bridge.RegisterFunction("changeMatches", (args, _) =>
            CopBool.Of(context.Changes.IsChanged(PathArgument(args), LineArgument(args))), 2);
        bridge.RegisterFunction("changeReportIncludes", (args, _) =>
            CopBool.Of(context.Options.ReportScope == "all" ||
                string.IsNullOrEmpty(PathArgument(args)) ||
                context.Changes.IsChanged(PathArgument(args), LineArgument(args))), 2);
        bridge.RegisterFunction("changeFiles", (_, _) =>
        {
            var files = context.Changes.Files.Select(file =>
            {
                var data = new DataObject("ChangedFile");
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
