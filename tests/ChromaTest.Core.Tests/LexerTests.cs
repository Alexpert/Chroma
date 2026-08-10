using ChromaTest.Core.Sdl.Lexing;
using ChromaTest.Core.Sdl.Source;

namespace ChromaTest.Core.Tests;

public sealed class LexerTests
{
    [Fact]
    public void Recognises_every_punctuation_token()
    {
        (IReadOnlyList<Token> tokens, var diagnostics) = TestSource.Lex("{}[]():,;=+-*/");

        Assert.Empty(diagnostics);
        Assert.Equal(
            [
                TokenKind.LeftBrace, TokenKind.RightBrace,
                TokenKind.LeftBracket, TokenKind.RightBracket,
                TokenKind.LeftParen, TokenKind.RightParen,
                TokenKind.Colon, TokenKind.Comma, TokenKind.Semicolon, TokenKind.Equals,
                TokenKind.Plus, TokenKind.Minus, TokenKind.Star, TokenKind.Slash,
                TokenKind.EndOfFile,
            ],
            tokens.Select(t => t.Kind));
    }

    [Fact]
    public void Reads_a_string_without_its_quotes()
    {
        (IReadOnlyList<Token> tokens, var diagnostics) = TestSource.Lex("\"bezier\"");

        Assert.Empty(diagnostics);
        Assert.Equal(TokenKind.String, tokens[0].Kind);
        Assert.Equal("bezier", tokens[0].Text);
    }

    [Fact]
    public void Reports_an_unterminated_string_without_swallowing_the_next_line()
    {
        // Stopping at the newline is the whole point: running to end of file instead would
        // consume the rest of the scene and report the mistake somewhere unrelated to it.
        (IReadOnlyList<Token> tokens, var diagnostics) = TestSource.Lex("\"bezier\nsphere { }");

        Assert.Contains(diagnostics, d => d.Message.Contains("unterminated string"));
        Assert.Contains(tokens, t => t.Kind == TokenKind.Identifier && t.Text == "sphere");
    }

    [Fact]
    public void Treats_let_as_the_only_keyword()
    {
        (IReadOnlyList<Token> tokens, _) = TestSource.Lex("let letter sphere");

        Assert.Equal(TokenKind.Let, tokens[0].Kind);
        Assert.Equal(TokenKind.Identifier, tokens[1].Kind);
        Assert.Equal(TokenKind.Identifier, tokens[2].Kind);
    }

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("42", 42.0)]
    [InlineData("1.5", 1.5)]
    [InlineData("0.125", 0.125)]
    [InlineData("1e3", 1000.0)]
    [InlineData("2.5e-2", 0.025)]
    [InlineData("1E+2", 100.0)]
    public void Reads_numbers(string text, double expected)
    {
        (IReadOnlyList<Token> tokens, var diagnostics) = TestSource.Lex(text);

        Assert.Empty(diagnostics);
        Assert.Equal(TokenKind.Number, tokens[0].Kind);
        Assert.Equal(expected, tokens[0].NumberValue, 10);
    }

    [Fact]
    public void Reads_a_decimal_point_under_a_comma_decimal_culture()
    {
        // The regression this guards against is silent and machine-dependent: without an
        // invariant parse, '1.5' fails to load on a French or German machine only.
        TestSource.InCommaDecimalCulture(() =>
        {
            (IReadOnlyList<Token> tokens, var diagnostics) = TestSource.Lex("1.5");

            Assert.Empty(diagnostics);
            Assert.Equal(1.5, tokens[0].NumberValue, 10);
        });
    }

    [Fact]
    public void Stops_a_number_at_a_trailing_dot()
    {
        // '1.' is a number followed by something else, not a malformed number: the dot has
        // no meaning in this language yet, so it must surface as its own problem.
        (IReadOnlyList<Token> tokens, _) = TestSource.Lex("1.");

        Assert.Equal(TokenKind.Number, tokens[0].Kind);
        Assert.Equal(1.0, tokens[0].NumberValue);
    }

    [Fact]
    public void Skips_line_comments()
    {
        (IReadOnlyList<Token> tokens, var diagnostics) = TestSource.Lex("1 // two 3\n4");

        Assert.Empty(diagnostics);
        Assert.Equal([1.0, 4.0], tokens.Where(t => t.Kind == TokenKind.Number).Select(t => t.NumberValue));
    }

    [Fact]
    public void Skips_block_comments()
    {
        (IReadOnlyList<Token> tokens, var diagnostics) = TestSource.Lex("1 /* two\n3 */ 4");

        Assert.Empty(diagnostics);
        Assert.Equal([1.0, 4.0], tokens.Where(t => t.Kind == TokenKind.Number).Select(t => t.NumberValue));
    }

    [Fact]
    public void Reports_an_unterminated_block_comment()
    {
        (_, var diagnostics) = TestSource.Lex("1 /* two");

        Diagnostic error = Assert.Single(diagnostics);
        Assert.Contains("unterminated block comment", error.Message);
    }

    [Fact]
    public void Reports_an_unexpected_character_and_keeps_going()
    {
        (IReadOnlyList<Token> tokens, var diagnostics) = TestSource.Lex("1 @ 2");

        Diagnostic error = Assert.Single(diagnostics);
        Assert.Contains("unexpected character '@'", error.Message);

        // The token after the bad one still has to be produced, or one typo would hide
        // every later problem in the file.
        Assert.Equal([1.0, 2.0], tokens.Where(t => t.Kind == TokenKind.Number).Select(t => t.NumberValue));
    }

    [Fact]
    public void Always_ends_with_an_end_of_file_token()
    {
        (IReadOnlyList<Token> tokens, _) = TestSource.Lex("   ");

        Token last = Assert.Single(tokens);
        Assert.Equal(TokenKind.EndOfFile, last.Kind);
    }
}
