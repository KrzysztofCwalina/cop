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
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream("Cop.Cli.LanguageReference.md");
                if (stream == null)
                {
                    _content = "Error: Language reference resource not found. This is a build issue.";
                }
                else
                {
                    using var reader = new StreamReader(stream);
                    _content = reader.ReadToEnd();
                }
            }
            return _content;
        }
    }
}
