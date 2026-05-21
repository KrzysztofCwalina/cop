// Sample: assembly attributes for AZC0011 testing
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MyLib.Tests")]
[assembly: InternalsVisibleTo("MyLib.Benchmarks")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
[assembly: InternalsVisibleTo("MyLib.SomeProductAssembly")]

public class Placeholder { }
