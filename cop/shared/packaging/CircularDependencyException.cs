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
        /// <summary>
        /// The ordered package names forming the detected dependency cycle.
        /// </summary>
        public List<string> Cycle { get; }

        /// <summary>Reports a dependency cycle whose package names are unavailable.</summary>
        public CircularDependencyException() => Cycle = [];

        /// <summary>Reports a dependency cycle with a descriptive message.</summary>
        public CircularDependencyException(string message) : base(message) => Cycle = [];

        /// <summary>Reports a dependency cycle while retaining its underlying failure.</summary>
        public CircularDependencyException(string message, Exception innerException)
            : base(message, innerException) => Cycle = [];

        /// <summary>Reports the ordered package names forming a dependency cycle.</summary>
        public CircularDependencyException(List<string> cycle)
            : base($"Circular dependency detected: {string.Join(" → ", cycle)}")
        {
            Cycle = cycle;
        }
    }
}
