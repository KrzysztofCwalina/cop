// Loads and formats the embedded Cop language reference.

using System.Reflection;

namespace Cop.Cli.Commands;

/// <summary>
/// Provides access to the embedded Cop language reference.
/// </summary>
internal static class LanguageReference
{
    private static string? _content;

    public static string Content
    {
        get
        {
            if (_content == null)
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using Stream? stream = assembly.GetManifestResourceStream("Cop.Cli.LanguageReference.md");
                if (stream == null)
                {
                    _content = "Error: Language reference resource not found. This is a build issue.";
                }
                else
                {
                    using StreamReader reader = new StreamReader(stream);
                    _content = reader.ReadToEnd();
                }
            }
            return _content;
        }
    }
}
