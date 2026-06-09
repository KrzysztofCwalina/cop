namespace Cop.Providers.SourceModel;

/// <summary>
/// A key-value property from a project file (e.g., OutputType=Exe, IsTestProject=true).
/// </summary>
public record ProjectProperty(string Name, string Value);
