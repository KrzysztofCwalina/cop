// Describes a provider-side string predicate over a named property.

using System.Text.Json.Serialization;

namespace Cop.Core;

/// <summary>
/// String predicate on a property. All string comparisons are case-insensitive
/// except <see cref="StringOp.Matches"/> (regex).
/// <list type="table">
///   <listheader><term>Op</term><description>Cop syntax → meaning</description></listheader>
///   <item><term>eq</term><description><c>Name:eq('Foo')</c> — case-insensitive equality</description></item>
///   <item><term>sw</term><description><c>Name:sw('I')</c> — case-insensitive starts-with</description></item>
///   <item><term>ew</term><description><c>Name:ew('Client')</c> — case-insensitive ends-with</description></item>
///   <item><term>ct</term><description><c>Name:ct('task')</c> — case-insensitive substring (contains)</description></item>
///   <item><term>rx</term><description><c>Name:rx('^Foo$')</c> — case-sensitive regex match</description></item>
/// </list>
/// The <c>==</c> and <c>!=</c> operators also compile to case-insensitive equality.
/// </summary>
public record StringOpFilter(string Property, StringOp Op, string Value) : FilterExpression;
