// Exception thrown when a circular dependency is detected during resolution.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cop.Core
{
    /// <summary>
    /// Exception thrown when a circular dependency is detected during resolution.
    /// </summary>
    public class CircularDependencyException : Exception
    {
        public List<string> Cycle { get; }

        public CircularDependencyException(List<string> cycle)
            : base($"Circular dependency detected: {string.Join(" → ", cycle)}")
        {
            Cycle = cycle;
        }
    }
}
