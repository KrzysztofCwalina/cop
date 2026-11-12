// Centralizes access to process environment variables.

using System.Collections;

namespace Cop.Core;

/// <summary>Provides the application's access to process environment variables.</summary>
public static class HostEnvironment
{
    /// <summary>Gets the value of the specified process environment variable.</summary>
    /// <param name="variable">The environment variable name.</param>
    /// <returns>The variable value, or <see langword="null"/> if it is not defined.</returns>
    public static string? GetEnvironmentVariable(string variable) =>
        Environment.GetEnvironmentVariable(variable);

    /// <summary>Gets the value of the specified environment variable for a target.</summary>
    /// <param name="variable">The environment variable name.</param>
    /// <param name="target">The environment target.</param>
    /// <returns>The variable value, or <see langword="null"/> if it is not defined.</returns>
    public static string? GetEnvironmentVariable(string variable, EnvironmentVariableTarget target) =>
        Environment.GetEnvironmentVariable(variable, target);

    /// <summary>Sets the value of the specified environment variable for a target.</summary>
    /// <param name="variable">The environment variable name.</param>
    /// <param name="value">The value to assign, or <see langword="null"/> to remove the variable.</param>
    /// <param name="target">The environment target.</param>
    public static void SetEnvironmentVariable(
        string variable, string? value, EnvironmentVariableTarget target = EnvironmentVariableTarget.Process) =>
        Environment.SetEnvironmentVariable(variable, value, target);

    /// <summary>Gets all process environment variables.</summary>
    /// <returns>The process environment variables.</returns>
    public static IDictionary GetEnvironmentVariables() => Environment.GetEnvironmentVariables();

    /// <summary>Expands environment variables in the specified string.</summary>
    /// <param name="value">The string to expand.</param>
    /// <returns>The string with environment variables expanded.</returns>
    public static string ExpandEnvironmentVariables(string value) =>
        Environment.ExpandEnvironmentVariables(value);
}
