// Represents a checksum violation found during verification

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cop.Core
{
    /// <summary>
    /// Represents a checksum violation found during verification
    /// </summary>
    public record ChecksumViolation(string FilePath, string ViolationType, string? Expected, string? Actual);
}
