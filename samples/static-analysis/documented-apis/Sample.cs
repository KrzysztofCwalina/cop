namespace App;

/// <summary>A well-documented service.</summary>
public class DocumentedService
{
    /// <summary>Does the work.</summary>
    public void Run() { }
}

public class UndocumentedService   // no doc comment -> flagged
{
    public void Run() { }
}
