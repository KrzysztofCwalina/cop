using Cop.Core;
using Cop.Lang;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class LockFileTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cop-lock-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // --- YAML serialization ---

    [Test]
    public void Serialize_RoundTrip_PreservesEntries()
    {
        var lockFile = new LockFile();
        lockFile.Files["config.json"] = new LockEntry
        {
            Checksum = "sha256:abc123",
            Signature = "hmac-sha256:xyz789"
        };
        lockFile.Files["readme.md"] = new LockEntry
        {
            Checksum = "sha256:def456",
            Signature = "hmac-sha256:uvw321"
        };

        var yaml = lockFile.Serialize();
        var deserialized = LockFile.Deserialize(yaml);

        Assert.That(deserialized.Version, Is.EqualTo(1));
        Assert.That(deserialized.Files, Has.Count.EqualTo(2));
        Assert.That(deserialized.Files["config.json"].Checksum, Is.EqualTo("sha256:abc123"));
        Assert.That(deserialized.Files["config.json"].Signature, Is.EqualTo("hmac-sha256:xyz789"));
        Assert.That(deserialized.Files["readme.md"].Checksum, Is.EqualTo("sha256:def456"));
    }

    [Test]
    public void SaveAndLoad_PersistsToDisk()
    {
        var lockFile = new LockFile();
        lockFile.Files["test.txt"] = new LockEntry
        {
            Checksum = "sha256:aaa",
            Signature = "hmac-sha256:bbb"
        };

        lockFile.Save(_tempDir);
        var loaded = LockFile.Load(_tempDir);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded!.Files, Has.Count.EqualTo(1));
        Assert.That(loaded.Files["test.txt"].Checksum, Is.EqualTo("sha256:aaa"));
    }

    [Test]
    public void Load_NoFile_ReturnsNull()
    {
        var result = LockFile.Load(_tempDir);
        Assert.That(result, Is.Null);
    }

    // --- HMAC signing ---

    [Test]
    public void Sign_ProducesConsistentSignature()
    {
        var sig1 = LockFile.Sign("sha256:abc123", "my-secret-key");
        var sig2 = LockFile.Sign("sha256:abc123", "my-secret-key");

        Assert.That(sig1, Is.EqualTo(sig2));
        Assert.That(sig1, Does.StartWith("hmac-sha256:"));
    }

    [Test]
    public void Sign_DifferentKeys_DifferentSignatures()
    {
        var sig1 = LockFile.Sign("sha256:abc123", "key-one");
        var sig2 = LockFile.Sign("sha256:abc123", "key-two");

        Assert.That(sig1, Is.Not.EqualTo(sig2));
    }

    [Test]
    public void Verify_ValidSignature_ReturnsTrue()
    {
        var data = "sha256:abc123";
        var key = "my-secret-key";
        var sig = LockFile.Sign(data, key);

        Assert.That(LockFile.Verify(data, sig, key), Is.True);
    }

    [Test]
    public void Verify_WrongKey_ReturnsFalse()
    {
        var data = "sha256:abc123";
        var sig = LockFile.Sign(data, "correct-key");

        Assert.That(LockFile.Verify(data, sig, "wrong-key"), Is.False);
    }

    [Test]
    public void Verify_TamperedSignature_ReturnsFalse()
    {
        var data = "sha256:abc123";
        var key = "my-key";
        var sig = LockFile.Sign(data, key);
        var tampered = sig + "x";

        Assert.That(LockFile.Verify(data, tampered, key), Is.False);
    }

    // --- File checksum ---

    [Test]
    public void ComputeFileChecksum_ProducesConsistentHash()
    {
        var filePath = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(filePath, "hello world");

        var hash1 = LockFile.ComputeFileChecksum(filePath);
        var hash2 = LockFile.ComputeFileChecksum(filePath);

        Assert.That(hash1, Is.EqualTo(hash2));
        Assert.That(hash1, Does.StartWith("sha256:"));
    }

    [Test]
    public void ComputeFileChecksum_DifferentContent_DifferentHash()
    {
        var file1 = Path.Combine(_tempDir, "a.txt");
        var file2 = Path.Combine(_tempDir, "b.txt");
        File.WriteAllText(file1, "content-a");
        File.WriteAllText(file2, "content-b");

        Assert.That(LockFile.ComputeFileChecksum(file1),
            Is.Not.EqualTo(LockFile.ComputeFileChecksum(file2)));
    }

    // --- Lock/Unlock ---

    [Test]
    public void Lock_AddsEntry()
    {
        File.WriteAllText(Path.Combine(_tempDir, "config.json"), "{\"key\": 1}");
        var lockFile = new LockFile();

        lockFile.Lock("config.json", _tempDir, "my-key");

        Assert.That(lockFile.Files, Has.Count.EqualTo(1));
        Assert.That(lockFile.Files["config.json"].Checksum, Does.StartWith("sha256:"));
        Assert.That(lockFile.Files["config.json"].Signature, Does.StartWith("hmac-sha256:"));
        Assert.That(LockFile.Verify(
            lockFile.Files["config.json"].Checksum,
            lockFile.Files["config.json"].Signature,
            "my-key"), Is.True);
    }

    [Test]
    public void Lock_RejectsLockfileItself()
    {
        File.WriteAllText(Path.Combine(_tempDir, LockFile.FileName), "dummy");

        Assert.Throws<InvalidOperationException>(() =>
            new LockFile().Lock(LockFile.FileName, _tempDir, "key"));
    }

    [Test]
    public void Lock_RejectsMissingFile()
    {
        Assert.Throws<FileNotFoundException>(() =>
            new LockFile().Lock("nonexistent.txt", _tempDir, "key"));
    }

    [Test]
    public void Unlock_RemovesEntry()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.txt"), "content");
        var lockFile = new LockFile();
        lockFile.Lock("a.txt", _tempDir, "key");

        var result = lockFile.Unlock("a.txt");

        Assert.That(result, Is.True);
        Assert.That(lockFile.Files, Has.Count.EqualTo(0));
    }

    [Test]
    public void Unlock_NonexistentEntry_ReturnsFalse()
    {
        Assert.That(new LockFile().Unlock("nothing.txt"), Is.False);
    }

    // --- Checksum verification ---

    [Test]
    public void VerifyChecksums_CleanFile_ReturnsClean()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.txt"), "content");
        var lockFile = new LockFile();
        lockFile.Lock("file.txt", _tempDir, "key");

        var results = lockFile.VerifyChecksums(_tempDir);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Status, Is.EqualTo("clean"));
    }

    [Test]
    public void VerifyChecksums_ModifiedFile_ReturnsModified()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.txt"), "original");
        var lockFile = new LockFile();
        lockFile.Lock("file.txt", _tempDir, "key");

        // Modify the file
        File.WriteAllText(Path.Combine(_tempDir, "file.txt"), "tampered!");

        var results = lockFile.VerifyChecksums(_tempDir);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Status, Is.EqualTo("modified"));
    }

    [Test]
    public void VerifyChecksums_DeletedFile_ReturnsDeleted()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.txt"), "content");
        var lockFile = new LockFile();
        lockFile.Lock("file.txt", _tempDir, "key");

        // Delete the file
        File.Delete(Path.Combine(_tempDir, "file.txt"));

        var results = lockFile.VerifyChecksums(_tempDir);

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Status, Is.EqualTo("deleted"));
    }

    // --- Signature verification ---

    [Test]
    public void VerifySignatures_ValidKey_NoViolations()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.txt"), "content");
        var lockFile = new LockFile();
        lockFile.Lock("file.txt", _tempDir, "my-key");

        var results = lockFile.VerifySignatures("my-key");

        Assert.That(results, Has.Count.EqualTo(0));
    }

    [Test]
    public void VerifySignatures_WrongKey_DetectsInvalid()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.txt"), "content");
        var lockFile = new LockFile();
        lockFile.Lock("file.txt", _tempDir, "correct-key");

        var results = lockFile.VerifySignatures("wrong-key");

        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Status, Is.EqualTo("signature-invalid"));
    }

    // --- Resign ---

    [Test]
    public void Resign_UpdatesSignatures()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.txt"), "content");
        var lockFile = new LockFile();
        lockFile.Lock("file.txt", _tempDir, "old-key");

        var oldSig = lockFile.Files["file.txt"].Signature;

        lockFile.Resign("new-key", _tempDir);

        Assert.That(lockFile.Files["file.txt"].Signature, Is.Not.EqualTo(oldSig));
        Assert.That(LockFile.Verify(
            lockFile.Files["file.txt"].Checksum,
            lockFile.Files["file.txt"].Signature,
            "new-key"), Is.True);
    }

    // --- Path normalization ---

    [Test]
    public void NormalizePath_ForwardSlashes()
    {
        var subDir = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "file.txt"), "x");

        var result = LockFile.NormalizePath(_tempDir, Path.Combine(subDir, "file.txt"));

        Assert.That(result, Is.EqualTo("sub/file.txt"));
        Assert.That(result, Does.Not.Contain("\\"));
    }

    [Test]
    public void NormalizePath_RejectsOutsideRoot()
    {
        var otherDir = Path.Combine(Path.GetTempPath(), "cop-other-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(otherDir);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                LockFile.NormalizePath(_tempDir, Path.Combine(otherDir, "file.txt")));
        }
        finally
        {
            Directory.Delete(otherDir, recursive: true);
        }
    }

    // --- FilesystemTypeRegistrar lock integration ---

    [Test]
    public void Scan_WithLock_UnlockedFiles_HaveDefaultStatus()
    {
        File.WriteAllText(Path.Combine(_tempDir, "normal.txt"), "hello");

        var registry = new TypeRegistry();
        FilesystemTypeRegistrar.Register(registry);
        FilesystemTypeRegistrar.Scan(registry, _tempDir);

        var files = registry.GetGlobalCollectionItems("DiskFiles")!;
        var acc = registry.GetAccessors("DiskFile")!;
        var file = files.Single(f => (string)acc["Name"](f)! == "normal.txt");

        Assert.That(acc["Locked"](file), Is.False);
        Assert.That(acc["LockStatus"](file), Is.EqualTo("unlocked"));
        Assert.That(acc["Checksum"](file), Is.EqualTo(""));
    }

    [Test]
    public void Scan_WithLock_CleanFile_IsMarkedClean()
    {
        File.WriteAllText(Path.Combine(_tempDir, "locked.txt"), "original");
        var lockFile = new LockFile();
        lockFile.Lock("locked.txt", _tempDir, "key");
        lockFile.Save(_tempDir);

        var registry = new TypeRegistry();
        FilesystemTypeRegistrar.Register(registry);
        FilesystemTypeRegistrar.Scan(registry, _tempDir);

        var files = registry.GetGlobalCollectionItems("DiskFiles")!;
        var acc = registry.GetAccessors("DiskFile")!;
        var file = files.Single(f => (string)acc["Name"](f)! == "locked.txt");

        Assert.That(acc["Locked"](file), Is.True);
        Assert.That(acc["LockStatus"](file), Is.EqualTo("clean"));
        Assert.That((string)acc["Checksum"](file)!, Does.StartWith("sha256:"));
    }

    [Test]
    public void Scan_WithLock_ModifiedFile_IsMarkedModified()
    {
        File.WriteAllText(Path.Combine(_tempDir, "locked.txt"), "original");
        var lockFile = new LockFile();
        lockFile.Lock("locked.txt", _tempDir, "key");
        lockFile.Save(_tempDir);

        // Modify after locking
        File.WriteAllText(Path.Combine(_tempDir, "locked.txt"), "changed!");

        var registry = new TypeRegistry();
        FilesystemTypeRegistrar.Register(registry);
        FilesystemTypeRegistrar.Scan(registry, _tempDir);

        var files = registry.GetGlobalCollectionItems("DiskFiles")!;
        var acc = registry.GetAccessors("DiskFile")!;
        var file = files.Single(f => (string)acc["Name"](f)! == "locked.txt");

        Assert.That(acc["Locked"](file), Is.True);
        Assert.That(acc["LockStatus"](file), Is.EqualTo("modified"));
    }

    [Test]
    public void Scan_WithLock_DeletedFile_CreatesPhantonEntry()
    {
        File.WriteAllText(Path.Combine(_tempDir, "locked.txt"), "content");
        var lockFile = new LockFile();
        lockFile.Lock("locked.txt", _tempDir, "key");
        lockFile.Save(_tempDir);

        // Delete the locked file
        File.Delete(Path.Combine(_tempDir, "locked.txt"));

        var registry = new TypeRegistry();
        FilesystemTypeRegistrar.Register(registry);
        FilesystemTypeRegistrar.Scan(registry, _tempDir);

        var files = registry.GetGlobalCollectionItems("DiskFiles")!;
        var acc = registry.GetAccessors("DiskFile")!;
        var phantom = files.SingleOrDefault(f => (string)acc["Name"](f)! == "locked.txt");

        Assert.That(phantom, Is.Not.Null);
        Assert.That(acc["Locked"](phantom!), Is.True);
        Assert.That(acc["LockStatus"](phantom!), Is.EqualTo("deleted"));
        Assert.That(acc["Size"](phantom!), Is.EqualTo(0));
    }

    // --- E2E: cop language can query lock status ---

    [Test]
    public void Integration_LockViolation_DetectedByAlanScript()
    {
        // Arrange: create files
        var codebaseDir = Path.Combine(_tempDir, "codebase");
        var scriptsDir = Path.Combine(_tempDir, "scripts");
        Directory.CreateDirectory(codebaseDir);
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(Path.Combine(codebaseDir, "config.json"), "{\"v\": 1}");
        File.WriteAllText(Path.Combine(codebaseDir, "readme.md"), "# Hello");

        // Lock config.json
        var lockFile = new LockFile();
        lockFile.Lock("config.json", codebaseDir, "secret");
        lockFile.Save(codebaseDir);

        // Modify config.json after locking
        File.WriteAllText(Path.Combine(codebaseDir, "config.json"), "{\"v\": 2}");

        // Write cop script that detects lock violations
        var copSource = """
            predicate isLockViolation(DiskFile) =>
                DiskFile.LockStatus == 'modified' ||
                DiskFile.LockStatus == 'deleted'

            foreach DiskFiles:isLockViolation => PRINT('VIOLATION: {item.Path} ({item.LockStatus})')
            """;
        File.WriteAllText(Path.Combine(scriptsDir, "check-locks.cop"), copSource);

        // Act
        var result = Engine.Run(scriptsDir, codebaseDir);

        // Assert
        Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
        Assert.That(result.Outputs, Has.Count.EqualTo(1));
        Assert.That(result.Outputs[0].Message, Does.Contain("config.json"));
        Assert.That(result.Outputs[0].Message, Does.Contain("modified"));
    }
}
