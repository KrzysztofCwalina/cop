using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

// End-to-end coverage for every AI-slop quality check, per language: each fixture contains
// positives and near-miss negatives, and the full suite must report exactly these findings.
[TestFixture]
public class QualityChecksPerLanguageTests
{
    private string _root = "";
    private string _repo = "";

    [SetUp]
    public void SetUp()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "cop.sln")))
            directory = directory.Parent;
        _repo = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        _root = Path.Combine(_repo, ".test-output", "quality-lang-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "target"));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);
    [Test]
    public async Task CSharpSuiteReportsExactFindings()
    {
        Write("IMPLEMENTATION_SUMMARY.md", """"
            # done
            """");
        Write("src/Svc.cs", """"
            using System;
            using System.Diagnostics;
            using System.Threading;
            namespace App;
            public class Svc {
                [Obsolete("use New")] public void Old() { }
                public void Caller() { Old(); }
                public void Swallow() { try { Work(); } catch (Exception e) { var x = 1; } }
                public void Logged() { try { Work(); } catch (Exception e) { Log(e); } }
                public void Sql(string id) { var c = new SqlCommand("select * from t where id=" + id); var d = new SqlCommand($"select {id}"); var ok = new SqlCommand("select 1"); }
                public void Crypto() { var a = DES.Create(); var b = new SHA1Managed(); var ok = SHA256.Create(); }
                public void Dbg() { Debugger.Break(); }
                public void Wait() { Thread.Sleep(500); }
                public void SaveFixed() { }
                public void IsFixed() { }
                public string Greeting() { return "done ✅"; }
                public void Work() { }
                public void Log(Exception e) { }
            }
            """");
        Write("tests/SvcTests.cs", """"
            public class SvcTests {
                [Test] public void NoAssert() { var s = new Svc(); s.Work(); }
                [Test] public void Mocked() { var m = new Mock<Svc>(); Assert.That(m, Is.Not.Null); }
                [Test] public void OnlyNotNull() { var s = new Svc(); Assert.NotNull(s); }
                [Test, Ignore("x")] public void Skipped() { Assert.True(true); }
                [Test] public void Sleeps() { Thread.Sleep(10); Assert.AreEqual(1, 1); }
            }
            """");
        await AssertFindings("csharp", [
            "constantAssertions tests/SvcTests.cs:5",
            "disabledTests tests/SvcTests.cs:5",
            "assertionFreeTests tests/SvcTests.cs:2",
            "existenceOnlyAssertions tests/SvcTests.cs:4",
            "mockedTestSubjects tests/SvcTests.cs:3",
            "weakCryptoCalls src/Svc.cs:11",
            "weakCryptoCalls src/Svc.cs:11",
            "injectionProneStrings src/Svc.cs:10",
            "injectionProneStrings src/Svc.cs:10",
            "deprecatedCalls src/Svc.cs:7",
            "blockingTestSleeps tests/SvcTests.cs:6",
            "debuggerLeftovers src/Svc.cs:12",
            "emojiInCode src/Svc.cs:16",
            "clutterNames src/Svc.cs:14",
            "agentReportFiles IMPLEMENTATION_SUMMARY.md:0"]);
    }

    [Test]
    public async Task PythonSuiteReportsExactFindings()
    {
        Write("app/dup.py", """"
            def first(a, b):
                x = a + 1
                y = b + 2
                if x > y:
                    log(x)
                z = compute(x, y)
                save(z)
                return z

            def second(c, d):
                p = c + 1
                q = d + 2
                if p > q:
                    log(p)
                r = compute(p, q)
                save(r)
                return r

            def different(c):
                return c

            def chatty(a):
                # Initialize the counter variable
                count = 0
                # Loop over each item in the list
                for item in a:
                    # Increment the counter by one
                    count += 1
                # Check if count is greater than zero
                if count > 0:
                    # Return the count value
                    return count
                return 0
            """");
        Write("app/helper_copy.py", """"
            def h():
                return 1
            """");
        Write("app/svc.py", """"
            import hashlib
            import os
            import subprocess
            import pdb
            from typing import cast, Any
            from warnings import deprecated

            @deprecated("x")
            def old():
                pass

            def caller():
                old()

            def swallow():
                try:
                    work()
                except Exception:
                    x = 1

            def run(cmd, uid, cur):
                os.system("ls " + cmd)
                subprocess.run(f"ls {cmd}", shell=True)
                cur.execute("select * from t where id=%s" % uid)
                cur.execute("select * from t where id=%s", (uid,))
                hashlib.md5(b"x")
                hashlib.new("sha1")
                pdb.set_trace()
                v = cast(Any, uid)
                y = foo()  # type: ignore
                z = foo()  # type: ignore[attr-defined]
                return "ok 🚀"

            def process_improved():
                pass

            def work():
                pass
            """");
        Write("tests/test_svc.py", """"
            import time
            from unittest.mock import Mock
            import pytest

            class Svc:
                pass

            def test_no_assert():
                work()

            def test_mocked():
                m = Mock(spec=Svc)
                assert m is not None

            @pytest.mark.skip
            def test_skipped():
                assert 1 == 1

            def test_sleep():
                time.sleep(1)
                assert 1 == 1

            def test_skip_call():
                pytest.skip("later")
            """");
        await AssertFindings("python", [
            "disabledTests tests/test_svc.py:16",
            "disabledTests tests/test_svc.py:24",
            "assertionFreeTests tests/test_svc.py:8",
            "mockedTestSubjects tests/test_svc.py:12",
            "typeErasingCasts app/svc.py:29",
            "blanketWarningSuppressions app/svc.py:30",
            "weakCryptoCalls app/svc.py:26",
            "weakCryptoCalls app/svc.py:27",
            "injectionProneStrings app/svc.py:22",
            "injectionProneStrings app/svc.py:23",
            "injectionProneStrings app/svc.py:24",
            "deprecatedCalls app/svc.py:13",
            "blockingTestSleeps tests/test_svc.py:20",
            "debuggerLeftovers app/svc.py:28",
            "overCommentedMethods app/dup.py:22",
            "emojiInCode app/svc.py:32",
            "clutterNames app/svc.py:34",
            "backupSourceFiles app/helper_copy.py:0",
            "duplicateMethodBodies app/dup.py:1",
            "duplicateMethodBodies app/dup.py:10"]);
    }

    [Test]
    public async Task JavaScriptSuiteReportsExactFindings()
    {
        Write("package.json", """"
            { "name": "app", "dependencies": { "express": "1" }, "devDependencies": { "jest": "1" } }
            """");
        Write("src/svc.test.ts", """"
            jest.mock('./svc');
            describe('svc', () => {
              it('no assert', () => { run(); });
              it('asserts', () => { expect(run()).toBe(1); });
              it.skip('skipped', () => { expect(1).toBe(1); });
              xit('x skipped', () => { expect(1).toBe(1); });
              it('only defined', () => { expect(run()).toBeDefined(); });
            });
            """");
        Write("src/svc.ts", """"
            import express from 'express';
            import lodash from 'lodash';
            import fs from 'fs';
            import { x } from './local';
            const axios = require('axios');
            const crypto = require('crypto');
            /** @deprecated use other */
            export function old() { }
            export function caller() { old(); }
            export function run(id: string, el: any, db: any) {
              console.log("debug", id);
              db.query("select * from t where id=" + id);
              db.query(`select * from t where id=${id}`);
              db.query("select 1");
              crypto.createHash('md5');
              document.write("<b>" + id + "</b>");
              const v = id as any;
              // @ts-ignore
              const w = 1;
              // eslint-disable-next-line no-console
              const q = 2;
              setTimeout(() => {}, 1000);
            }
            """");
        await AssertFindings("javascript", [
            "disabledTests src/svc.test.ts:5",
            "disabledTests src/svc.test.ts:6",
            "assertionFreeTests src/svc.test.ts:3",
            "mockedTestSubjects src/svc.test.ts:1",
            "typeErasingCasts src/svc.ts:17",
            "blanketWarningSuppressions src/svc.ts:18",
            "weakCryptoCalls src/svc.ts:15",
            "injectionProneStrings src/svc.ts:12",
            "injectionProneStrings src/svc.ts:13",
            "injectionProneStrings src/svc.ts:16",
            "undeclaredDependencies src/svc.ts:2",
            "undeclaredDependencies src/svc.ts:5",
            "deprecatedCalls src/svc.ts:9",
            "deprecatedCalls src/svc.ts:16"]);
    }

    [Test]
    public async Task GoSuiteReportsExactFindings()
    {
        Write("go.mod", """"
            module example.com/m

            go 1.22

            require github.com/lib/pq v1.0.0
            """");
        Write("svc/svc_test.go", """"
            package svc

            import "testing"

            func TestNoAssert(t *testing.T) {
                Run(nil, "")
            }

            func TestSkipped(t *testing.T) {
                t.Skip("later")
            }

            func TestOk(t *testing.T) {
                if Run2() != 1 {
                    t.Errorf("bad")
                }
            }
            """");
        Write("svc/svc.go", """"
            package svc

            import (
                "crypto/md5"
                "database/sql"
                "fmt"
                "os/exec"
                "time"
                "github.com/lib/pq"
                "github.com/missing/dep"
                "example.com/m/other"
            )

            func Run(db *sql.DB, id string) {
                db.Query("select * from t where id=" + id)
                db.Query(fmt.Sprintf("select %s", id))
                db.Query("select $1", id)
                exec.Command("sh", "-c", "ls "+id)
                md5.Sum([]byte(id))
                fmt.Println("debug")
                time.Sleep(5 * time.Second)
                x := 1 //nolint
                _ = x
            }
            """");
        await AssertFindings("go", [
            "disabledTests svc/svc_test.go:10",
            "assertionFreeTests svc/svc_test.go:5",
            "blanketWarningSuppressions svc/svc.go:22",
            "weakCryptoCalls svc/svc.go:19",
            "injectionProneStrings svc/svc.go:15",
            "injectionProneStrings svc/svc.go:16",
            "injectionProneStrings svc/svc.go:18",
            "undeclaredDependencies svc/svc.go:10"]);
    }

    [Test]
    public async Task JavaSuiteReportsExactFindings()
    {
        Write("src/main/java/app/Svc.java", """"
            package app;
            import java.security.MessageDigest;
            import javax.crypto.Cipher;
            public class Svc {
                @Deprecated
                public void old() { }
                public void caller() { old(); }
                public void swallow() { try { work(); } catch (Exception e) { int x = 1; } }
                public void run(java.sql.Statement st, String id) throws Exception {
                    st.executeQuery("select * from t where id=" + id);
                    st.executeQuery("select 1");
                    MessageDigest.getInstance("MD5");
                    MessageDigest.getInstance("SHA-256");
                    Cipher.getInstance("AES/ECB/PKCS5Padding");
                    Cipher.getInstance("AES/GCM/NoPadding");
                    System.out.println("debug");
                    Thread.sleep(100);
                    new RuntimeException().printStackTrace();
                }
                public void work() { }
            }
            """");
        Write("src/test/java/app/SvcTest.java", """"
            package app;
            public class SvcTest {
                @Test public void noAssert() { new Svc().work(); }
                @Test public void mocked() { Svc s = Mockito.mock(Svc.class); assertNotNull(s); }
                @Disabled @Test public void skipped() { assertEquals(1, 1); }
            }
            """");
        await AssertFindings("java", [
            "disabledTests src/test/java/app/SvcTest.java:5",
            "assertionFreeTests src/test/java/app/SvcTest.java:3",
            "existenceOnlyAssertions src/test/java/app/SvcTest.java:4",
            "mockedTestSubjects src/test/java/app/SvcTest.java:4",
            "weakCryptoCalls src/main/java/app/Svc.java:12",
            "weakCryptoCalls src/main/java/app/Svc.java:14",
            "injectionProneStrings src/main/java/app/Svc.java:10",
            "deprecatedCalls src/main/java/app/Svc.java:7",
            "fixedDelays src/main/java/app/Svc.java:17"]);
    }

    [Test]
    public async Task RustSuiteReportsExactFindings()
    {
        Write("Cargo.toml", """"
            [package]
            name = "app"
            version = "0.1.0"

            [dependencies]
            md-5 = "0.10"
            """");
        Write("src/lib.rs", """"
            use md5::compute;
            use serde_json::Value;
            use std::thread;
            mod util;
            use util::helper;

            #[allow(warnings)]
            pub fn noisy() { }

            pub fn run(conn: &Conn, id: &str) {
                conn.execute(&format!("select * from t where id={}", id), &[]);
                let d = md5::compute(id);
                dbg!(id);
                println!("debug");
                thread::sleep(std::time::Duration::from_secs(1));
                todo!();
            }

            #[cfg(test)]
            mod tests {
                #[test]
                fn no_assert() { super::noisy(); }
                #[test]
                #[ignore]
                fn skipped() { assert_eq!(1, 1); }
                #[test]
                fn ok() { assert_eq!(1, 1); }
            }
            """");
        Write("src/util.rs", """"
            pub fn helper() {}
            """");
        Write("tests/it.rs", """"
            use app::noisy;

            #[test]
            fn no_assert() { noisy(); }

            #[test]
            #[ignore]
            fn skipped() { assert_eq!(1, 1); }

            #[test]
            fn ok() { assert_eq!(1, 1); }

            #[test]
            fn sleepy() { std::thread::sleep(std::time::Duration::from_millis(5)); assert!(true); }
            """");
        await AssertFindings("rust", [
            "constantAssertions tests/it.rs:14",
            "disabledTests tests/it.rs:8",
            "assertionFreeTests tests/it.rs:4",
            "blanketWarningSuppressions src/lib.rs:8",
            "weakCryptoCalls src/lib.rs:12",
            "injectionProneStrings src/lib.rs:11",
            "undeclaredDependencies src/lib.rs:2",
            "blockingTestSleeps tests/it.rs:14",
            "fixedDelays src/lib.rs:15"]);
    }

    [Test]
    public async Task GoMultiplicationArgumentParsesAsBinaryNotMemberAccess()
    {
        Write("svc.go", "package svc\n\nimport \"time\"\n\nfunc Run() {\n    time.Sleep(5 * time.Second)\n}");
        await AssertOutput("go",
            "foreach cb.Calls.Where(item.MemberName == 'Sleep') => '{item.ArgumentExpressions[0].Kind} {item.ArgumentExpressions[0].Name}'",
            ["binary *"]);
    }

    private void Write(string filename, string source)
    {
        string path = Path.Combine(_root, "target", filename);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source + "\n");
    }

    private Task AssertFindings(string language, string[] expected) =>
        AssertOutput(language, "foreach codeQualityChecks(cb) => '{item.Check} {item.File}:{item.Line}'", expected);

    private async Task AssertOutput(string language, string command, string[] expected)
    {
        File.WriteAllText(Path.Combine(_root, "main.cop"), $$"""
            import code
            import code-quality-checks
            import {{language}}
            let cb = {{language}}.parse()
            command MAIN = {{command}}
            """);
        for (int run = 0; run < 2; run++)
        {
            var result = await Engine.RunAsync(_root, Path.Combine(_root, "target"), new EngineRunOptions
            {
                AdditionalFeedPaths = [Path.Combine(_repo, "packages")]
            });
            Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
            Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
            Assert.That(result.Outputs.Select(output => output.Message), Is.EqualTo(expected), $"Run {run}");
        }
    }
}
