namespace Cop.Providers.SourceModel;

[Flags]
public enum Modifier
{
    None = 0,
    Public = 1,
    Private = 2,
    Protected = 4,
    Internal = 8,
    Static = 16,
    Sealed = 32,
    Abstract = 64,
    Virtual = 128,
    Async = 256,
    Override = 512,
    Readonly = 1024,
    Const = 2048
}
