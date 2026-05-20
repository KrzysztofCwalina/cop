namespace Cop.Lang;

/// <summary>
/// Centralized string constants for all intrinsic function names.
/// These names must match exactly what .cop files declare with '= intrinsic'.
/// </summary>
public static class Intrinsics
{
    public const string Print = "print";
    public const string Save = "save";
    public const string Debug = "debug";
    public const string Assert = "assert";
    public const string Fail = "fail";
    public const string Text = "text";
    public const string Read = "read";
    public const string Error = "error";
    public const string PathMatches = "pathMatches";
    public const string Program = "program";
    public const string Provider = "provider";
}
