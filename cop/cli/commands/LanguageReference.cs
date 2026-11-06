// Loads and formats the embedded Cop language reference.

using System.Reflection;

namespace Cop.Cli.Commands;

/// <summary>
/// Provides access to the embedded Cop language reference.
/// </summary>
internal static class LanguageReference
{
    private static string? s_content;

    /// <summary>Full embedded language reference, loaded once on first access.</summary>
    public static string Content
    {
        get
        {
            if (s_content == null)
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using Stream? stream = assembly.GetManifestResourceStream("Cop.Cli.LanguageReference.md");
                if (stream == null)
                {
                    s_content = "Error: Language reference resource not found. This is a build issue.";
                }
                else
                {
                    using StreamReader reader = new StreamReader(stream);
                    s_content = reader.ReadToEnd();
                }
            }
            return s_content;
        }
    }
}
