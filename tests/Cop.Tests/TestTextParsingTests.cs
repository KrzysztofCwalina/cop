using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class TestTextParsingTests
{
    [TestCase("plain\r\noutput", "plain\r\noutput", TestName = "StripAnsi_PlainText")]
    [TestCase("\x1b[31mred\x1b[0m", "red", TestName = "StripAnsi_Color")]
    [TestCase("\x1b[mdefault\x1b[1;32mbold\x1b[0m", "defaultbold", TestName = "StripAnsi_EmptyAndMultipleParameters")]
    [TestCase("a\x1b[2Kb\x1b[?25lc", "abc", TestName = "StripAnsi_EraseAndPrivateMode")]
    [TestCase("a\x1b[1 qz", "az", TestName = "StripAnsi_IntermediateBytes")]
    [TestCase("a\x1b[31", "a\x1b[31", TestName = "StripAnsi_IncompleteSequence")]
    [TestCase("a\x1b[12\nb", "a\x1b[12\nb", TestName = "StripAnsi_MalformedSequence")]
    [TestCase("a\x1b]title\x07z", "a\x1b]title\x07z", TestName = "StripAnsi_NonCsiSequence")]
    [TestCase("\x1b[\x1b[31mred", "\x1b[red", TestName = "StripAnsi_ValidSequenceAfterMalformedPrefix")]
    public void StripAnsi_RemovesCompleteCsiSequencesOnly(string text, string expected) =>
        Assert.That(TestTextParsing.StripAnsi(text), Is.EqualTo(expected));

    [TestCase("\x1b[31mred\x1b[0m", "red", TestName = "StripAnsiColors_Color")]
    [TestCase("\x1b[mdefault\x1b[1;32mbold\x1b[0m", "defaultbold", TestName = "StripAnsiColors_EmptyAndMultipleParameters")]
    [TestCase("a\x1b[2Kb", "a\x1b[2Kb", TestName = "StripAnsiColors_PreservesErase")]
    [TestCase("a\x1b[?25mb", "a\x1b[?25mb", TestName = "StripAnsiColors_PreservesPrivateMode")]
    [TestCase("a\x1b[1 mb", "a\x1b[1 mb", TestName = "StripAnsiColors_PreservesIntermediateBytes")]
    [TestCase("a\x1b[31", "a\x1b[31", TestName = "StripAnsiColors_PreservesIncompleteSequence")]
    public void StripAnsi_ColorsOnlyPreservesOtherControlSequences(string text, string expected) =>
        Assert.That(TestTextParsing.StripAnsi(text, colorsOnly: true), Is.EqualTo(expected));
}
