namespace Cop.Providers.SourceModel;

/// <summary>
/// A C#-specific <see cref="TypeDeclaration"/> carrying language-specific facts that
/// have no place in the language-agnostic common model (e.g. records, partial types).
///
/// Only the C# provider emits these. The runtime maps this CLR type to the cop type
/// <c>CSharpType</c> (declared in the csharp package), so checks that narrow with
/// <c>:asCSharp</c> can read these fields while the common <c>Type</c> stays clean.
/// </summary>
public sealed record CSharpTypeDeclaration : TypeDeclaration
{
    public CSharpTypeDeclaration(
        TypeDeclaration source,
        bool isRecord = false,
        bool isRecordStruct = false,
        bool isReadOnly = false,
        bool isRef = false,
        bool isFileLocal = false,
        bool isPartial = false,
        bool hasPrimaryConstructor = false,
        bool isGeneric = false)
        : base(source)
    {
        IsRecord = isRecord;
        IsRecordStruct = isRecordStruct;
        IsReadOnly = isReadOnly;
        IsRef = isRef;
        IsFileLocal = isFileLocal;
        IsPartial = isPartial;
        HasPrimaryConstructor = hasPrimaryConstructor;
        IsGeneric = isGeneric;
    }

    /// <summary>True for <c>record</c> / <c>record struct</c> declarations.</summary>
    public bool IsRecord { get; init; }

    /// <summary>True for <c>record struct</c> declarations specifically.</summary>
    public bool IsRecordStruct { get; init; }

    /// <summary>True for <c>readonly struct</c> declarations.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>True for <c>ref struct</c> declarations.</summary>
    public bool IsRef { get; init; }

    /// <summary>True for <c>file</c>-scoped (file-local) type declarations.</summary>
    public bool IsFileLocal { get; init; }

    /// <summary>True for <c>partial</c> type declarations.</summary>
    public bool IsPartial { get; init; }

    /// <summary>True when the type declares a primary constructor.</summary>
    public bool HasPrimaryConstructor { get; init; }

    /// <summary>True when the type declares generic type parameters.</summary>
    public bool IsGeneric { get; init; }
}

public static class CSharpTypeDeclarationExtensions
{
    /// <summary>Wraps a common <see cref="TypeDeclaration"/> as a C#-specific one.</summary>
    public static CSharpTypeDeclaration AsCSharp(
        this TypeDeclaration source,
        bool isRecord = false,
        bool isRecordStruct = false,
        bool isReadOnly = false,
        bool isRef = false,
        bool isFileLocal = false,
        bool isPartial = false,
        bool hasPrimaryConstructor = false,
        bool isGeneric = false)
        => new(source, isRecord, isRecordStruct, isReadOnly, isRef, isFileLocal, isPartial, hasPrimaryConstructor, isGeneric);
}
