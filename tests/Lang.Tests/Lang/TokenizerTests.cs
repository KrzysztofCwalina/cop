using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class TokenizerTests
{
    [Test]
    public void Tokenize_PredicateDefinition()
    {
        var tokens = new Tokenizer("IsClient(Type) => Type.Name:endsWith('Client')").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[0].Value, Is.EqualTo("IsClient"));
        Assert.That(tokens[1].Kind, Is.EqualTo(TokenKind.LParen));
        Assert.That(tokens[2].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[2].Value, Is.EqualTo("Type"));
        Assert.That(tokens[3].Kind, Is.EqualTo(TokenKind.RParen));
        Assert.That(tokens[4].Kind, Is.EqualTo(TokenKind.Arrow));
    }

    [Test]
    public void Tokenize_PrintHeader()
    {
        var tokens = new Tokenizer("foreach Statements:isVar => PRINT('msg')").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.ForeachKeyword));
        Assert.That(tokens[0].Value, Is.EqualTo("foreach"));
        Assert.That(tokens[1].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[1].Value, Is.EqualTo("Statements"));
        Assert.That(tokens[2].Kind, Is.EqualTo(TokenKind.Colon));
        Assert.That(tokens[3].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[3].Value, Is.EqualTo("isVar"));
        Assert.That(tokens[4].Kind, Is.EqualTo(TokenKind.Arrow));
        Assert.That(tokens[5].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[5].Value, Is.EqualTo("PRINT"));
        Assert.That(tokens[6].Kind, Is.EqualTo(TokenKind.LParen));
        Assert.That(tokens[7].Kind, Is.EqualTo(TokenKind.StringLiteral));
        Assert.That(tokens[8].Kind, Is.EqualTo(TokenKind.RParen));
    }

    [Test]
    public void Tokenize_StringWithBackslashEscapes()
    {
        // In single-quote strings, unknown escapes like \b and \s are preserved as-is
        var tokens = new Tokenizer("'\\bprint\\s*\\('").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo("\\bprint\\s*\\("));
    }

    [Test]
    public void Tokenize_Operators()
    {
        var tokens = new Tokenizer("&& || == != ! =>").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.AndAnd));
        Assert.That(tokens[1].Kind, Is.EqualTo(TokenKind.OrOr));
        Assert.That(tokens[2].Kind, Is.EqualTo(TokenKind.EqualEqual));
        Assert.That(tokens[3].Kind, Is.EqualTo(TokenKind.NotEqual));
        Assert.That(tokens[4].Kind, Is.EqualTo(TokenKind.Not));
        Assert.That(tokens[5].Kind, Is.EqualTo(TokenKind.Arrow));
    }

    [Test]
    public void Tokenize_Comments()
    {
        var tokens = new Tokenizer("a // comment\nb").Tokenize();
        Assert.That(tokens, Has.Count.EqualTo(3)); // a, b, EOF
        Assert.That(tokens[0].Value, Is.EqualTo("a"));
        Assert.That(tokens[1].Value, Is.EqualTo("b"));
    }

    [Test]
    public void Tokenize_Keywords()
    {
        var tokens = new Tokenizer("csharp python PRINT true false").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[0].Value, Is.EqualTo("csharp"));
        Assert.That(tokens[1].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[1].Value, Is.EqualTo("python"));
        Assert.That(tokens[2].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[2].Value, Is.EqualTo("PRINT"));
        Assert.That(tokens[3].Kind, Is.EqualTo(TokenKind.True));
        Assert.That(tokens[4].Kind, Is.EqualTo(TokenKind.False));
    }

    [Test]
    public void Tokenize_FormerKeywords_NowIdentifiers()
    {
        var tokens = new Tokenizer("check error warning info").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[1].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[2].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[3].Kind, Is.EqualTo(TokenKind.Identifier));
    }

    [Test]
    public void Tokenize_UppercaseActionsAreIdentifiers()
    {
        var tokens = new Tokenizer("ERROR WARNING INFO PRINT SAVE").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[0].Value, Is.EqualTo("ERROR"));
        Assert.That(tokens[1].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[1].Value, Is.EqualTo("WARNING"));
        Assert.That(tokens[2].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[2].Value, Is.EqualTo("INFO"));
        Assert.That(tokens[3].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[3].Value, Is.EqualTo("PRINT"));
        Assert.That(tokens[4].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[4].Value, Is.EqualTo("SAVE"));
    }

    [Test]
    public void Tokenize_ColonAndAllLanguages()
    {
        var tokens = new Tokenizer("Type:csharp java go typescript").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[0].Value, Is.EqualTo("Type"));
        Assert.That(tokens[1].Kind, Is.EqualTo(TokenKind.Colon));
        Assert.That(tokens[2].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[2].Value, Is.EqualTo("csharp"));
        Assert.That(tokens[3].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[3].Value, Is.EqualTo("java"));
        Assert.That(tokens[4].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[4].Value, Is.EqualTo("go"));
        Assert.That(tokens[5].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[5].Value, Is.EqualTo("typescript"));
    }

    [Test]
    public void Tokenize_UnterminatedString_Throws()
    {
        Assert.Throws<ParseException>(() =>
            new Tokenizer("'hello").Tokenize());
    }

    [Test]
    public void Tokenize_TracksLineNumbers()
    {
        var tokens = new Tokenizer("a\nb\nc").Tokenize();
        Assert.That(tokens[0].Line, Is.EqualTo(1));
        Assert.That(tokens[1].Line, Is.EqualTo(2));
        Assert.That(tokens[2].Line, Is.EqualTo(3));
    }

    [Test]
    public void Tokenize_HashSingleLineComment()
    {
        var tokens = new Tokenizer("a # comment\nb").Tokenize();
        Assert.That(tokens, Has.Count.EqualTo(3)); // a, b, EOF
        Assert.That(tokens[0].Value, Is.EqualTo("a"));
        Assert.That(tokens[1].Value, Is.EqualTo("b"));
    }

    [Test]
    public void Tokenize_DocComment()
    {
        var tokens = new Tokenizer("## This is a doc comment\nPRINT").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.DocComment));
        Assert.That(tokens[0].Value, Is.EqualTo("This is a doc comment"));
        Assert.That(tokens[0].Line, Is.EqualTo(1));
        Assert.That(tokens[1].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[1].Value, Is.EqualTo("PRINT"));
    }

    [Test]
    public void Tokenize_MultipleDocComments()
    {
        var tokens = new Tokenizer("## Line one\n## Line two\nPRINT").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.DocComment));
        Assert.That(tokens[0].Value, Is.EqualTo("Line one"));
        Assert.That(tokens[1].Kind, Is.EqualTo(TokenKind.DocComment));
        Assert.That(tokens[1].Value, Is.EqualTo("Line two"));
        Assert.That(tokens[2].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[2].Value, Is.EqualTo("PRINT"));
    }

    [Test]
    public void Tokenize_MultiLineComment()
    {
        var tokens = new Tokenizer("a\n#\nthis is ignored\nalso ignored\n#\nb").Tokenize();
        Assert.That(tokens, Has.Count.EqualTo(3)); // a, b, EOF
        Assert.That(tokens[0].Value, Is.EqualTo("a"));
        Assert.That(tokens[1].Value, Is.EqualTo("b"));
    }

    [Test]
    public void Tokenize_MultiLineComment_TracksLines()
    {
        var tokens = new Tokenizer("a\n#\nskipped\n#\nb").Tokenize();
        Assert.That(tokens[0].Line, Is.EqualTo(1));
        Assert.That(tokens[1].Line, Is.EqualTo(5));
    }

    [Test]
    public void Tokenize_HashAndSlashCommentsMixed()
    {
        var tokens = new Tokenizer("a // old comment\nb # new comment\nc").Tokenize();
        Assert.That(tokens, Has.Count.EqualTo(4)); // a, b, c, EOF
        Assert.That(tokens[0].Value, Is.EqualTo("a"));
        Assert.That(tokens[1].Value, Is.EqualTo("b"));
        Assert.That(tokens[2].Value, Is.EqualTo("c"));
    }

    [Test]
    public void Tokenize_HyphenatedIdentifier()
    {
        var tokens = new Tokenizer("list-types").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[0].Value, Is.EqualTo("list-types"));
    }

    [Test]
    public void Tokenize_HyphenatedIdentifier_KeywordSeparatedBySpace()
    {
        var tokens = new Tokenizer("let list-types").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.LetKeyword));
        Assert.That(tokens[1].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[1].Value, Is.EqualTo("list-types"));
    }

    [Test]
    public void Tokenize_HyphenatedIdentifier_MultipleHyphens()
    {
        var tokens = new Tokenizer("my-long-command-name").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[0].Value, Is.EqualTo("my-long-command-name"));
    }

    [Test]
    public void Tokenize_IntLiteral()
    {
        var tokens = new Tokenizer("42").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo("42"));
    }

    [Test]
    public void Tokenize_NumberLiteral()
    {
        var tokens = new Tokenizer("3.14").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.NumberLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo("3.14"));
    }

    [Test]
    public void Tokenize_NumberLiteral_TrailingDigits()
    {
        var tokens = new Tokenizer("42.0").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.NumberLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo("42.0"));
    }

    [Test]
    public void Tokenize_IntFollowedByDot_NotNumber()
    {
        // 42.Name should tokenize as int + dot + identifier, not as a number literal
        var tokens = new Tokenizer("42.Name").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.IntLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo("42"));
        Assert.That(tokens[1].Kind, Is.EqualTo(TokenKind.Dot));
        Assert.That(tokens[2].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[2].Value, Is.EqualTo("Name"));
    }

    [Test]
    public void Tokenize_MinusAfterParenIsMinusToken()
    {
        var tokens = new Tokenizer("Items) - Accepted").Tokenize();
        var minus = tokens.FirstOrDefault(t => t.Kind == TokenKind.Minus);
        Assert.That(minus, Is.Not.Null, "Standalone '-' after ')' should be a Minus token");
    }

    [Test]
    public void Tokenize_HyphenInsideIdentifierStaysIdentifier()
    {
        var tokens = new Tokenizer("empty-folders").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[0].Value, Is.EqualTo("empty-folders"));
    }

    [Test]
    public void Tokenize_RunKeyword()
    {
        var tokens = new Tokenizer("RUN CHECK(violations)").Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.RunKeyword));
        Assert.That(tokens[1].Kind, Is.EqualTo(TokenKind.Identifier));
        Assert.That(tokens[1].Value, Is.EqualTo("CHECK"));
    }

    // ── Triple-Quote String Tests ──

    [Test]
    public void Tokenize_TripleQuoteString_Simple()
    {
        var source = "'''\n    hello\n    '''";
        var tokens = new Tokenizer(source).Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo("hello"));
    }

    [Test]
    public void Tokenize_TripleQuoteString_MultiLine()
    {
        var source = "'''\n    line1\n    line2\n    '''";
        var tokens = new Tokenizer(source).Tokenize();
        Assert.That(tokens[0].Kind, Is.EqualTo(TokenKind.StringLiteral));
        Assert.That(tokens[0].Value, Is.EqualTo("line1\nline2"));
    }

    [Test]
    public void Tokenize_TripleQuoteString_IndentStripping()
    {
        var source = "'''\n        deeply indented\n        content\n    '''";
        var tokens = new Tokenizer(source).Tokenize();
        // Closing ''' has 4 spaces indent, content lines have 8 → 4 stripped → 4 remaining
        Assert.That(tokens[0].Value, Is.EqualTo("    deeply indented\n    content"));
    }

    [Test]
    public void Tokenize_TripleQuoteString_RawBackslash()
    {
        // Triple-quote strings are raw — backslash is literal
        var source = "'''\n    \\n\\t\n    '''";
        var tokens = new Tokenizer(source).Tokenize();
        Assert.That(tokens[0].Value, Is.EqualTo("\\n\\t"));
    }

    [Test]
    public void Tokenize_TripleQuoteString_PreservesJsonBraces()
    {
        var source = "'''\n    {\"name\": \"test\"}\n    '''";
        var tokens = new Tokenizer(source).Tokenize();
        Assert.That(tokens[0].Value, Is.EqualTo("{\"name\": \"test\"}"));
    }

    [Test]
    public void Tokenize_TripleQuoteString_Unterminated()
    {
        Assert.Throws<ParseException>(() =>
            new Tokenizer("'''\nhello").Tokenize());
    }
}
