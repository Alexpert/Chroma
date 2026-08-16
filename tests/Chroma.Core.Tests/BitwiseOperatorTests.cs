using Chroma.Core.Model;
using Chroma.Core.Model.Geometry.Primitives;
using Chroma.Core.Sdl.Source;

namespace Chroma.Core.Tests;

/// <summary>
/// <c>&amp;</c>, <c>|</c>, <c>^</c>, <c>~</c>, <c>&lt;&lt;</c> and <c>&gt;&gt;</c> — the rest
/// of C's operator set, and the two readings each of the first three carries.
/// </summary>
public sealed class BitwiseOperatorTests
{
    private static float Radius(string expression) =>
        Assert.IsType<Sphere>(TestSource.LoadValid($"sphere {{ radius: {expression} }}").Roots[0])
            .Radius;

    /// <summary>The value of an expression that produces a boolean, as a radius of 1 or 2.</summary>
    private static bool Truth(string expression) =>
        Radius($"({expression}) ? 1 : 2") == 1f;

    [Theory]
    [InlineData("12 & 10", 8)]
    [InlineData("12 | 10", 14)]
    [InlineData("12 ^ 10", 6)]
    [InlineData("~0", -1)]
    [InlineData("~5", -6)]
    [InlineData("1 << 10", 1024)]
    [InlineData("1024 >> 3", 128)]
    [InlineData("-8 >> 1", -4)]
    [InlineData("(-1) & 255", 255)]
    public void Applies_a_bitwise_operator_to_whole_numbers(string expression, int expected)
    {
        // '>>' keeps the sign, which is C's behaviour on a signed operand and the only one a
        // scene would expect from a language whose numbers are signed and nothing else.
        Assert.Equal(expected, Radius(expression));
    }

    [Theory]
    [InlineData("true & true", true)]
    [InlineData("true & false", false)]
    [InlineData("false | true", true)]
    [InlineData("false | false", false)]
    [InlineData("true ^ false", true)]
    [InlineData("true ^ true", false)]
    [InlineData("false ^ false", false)]
    public void Applies_a_logical_connective_to_booleans(string expression, bool expected)
    {
        // '^' is the reason this entry exists: exclusive or had no spelling at all, and
        // "exactly one of these" had to be written '(a || b) && !(a && b)'.
        Assert.Equal(expected, Truth(expression));
    }

    [Fact]
    public void A_bitwise_connective_evaluates_both_sides()
    {
        // The difference from '&&' and '||', and the reason C keeps both spellings. Observed
        // through a right-hand side that reports: under '&&' it would never be reached.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { radius: (false & missing) ? 1 : 2 }");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("unknown name 'missing'"));
    }

    // One case per rung of the ladder, each written so that the other reading gives a
    // different answer. The ladder is C's, top to bottom:
    //
    //     unary - ! ~   *  /  %   +  -   <<  >>   <  <=  >  >=   ==  !=
    //     &   ^   |   &&   ||   ? :
    //
    // Every level is left-associative except the ternary and the unary operators, which
    // group to the right. Reproducing the order matters more than liking it: a scene written
    // by someone who knows C must not quietly mean something else here.
    [Theory]

    // Unary above multiplicative: (~1) * 2 is -4, and ~(1 * 2) would be -3.
    [InlineData("~1 * 2", -4)]

    // Multiplicative above additive, which the language already had.
    [InlineData("1 + 2 * 3", 7)]

    // Additive above the shifts, which is C's placement and C's surprise: this shifts by 3.
    [InlineData("1 << 1 + 2", 8)]

    // The shifts above the comparisons: (1 << 3) > 4, not 1 << (3 > 4).
    [InlineData("(1 << 3 > 4) ? 1 : 2", 1)]

    // The comparisons above equality: (1 < 2) == true.
    [InlineData("(1 < 2 == true) ? 1 : 2", 1)]

    // '&' above '^' above '|': (3 & 4) is 0, (2 ^ 0) is 2, (1 | 2) is 3. Left to right
    // throughout would give 7 instead.
    [InlineData("1 | 2 ^ 3 & 4", 3)]

    // All three connectives above '&&': (true & true) && (false | true).
    [InlineData("(true & true && false | true) ? 1 : 2", 1)]

    // '&&' above '||': (false && false) || true.
    [InlineData("(false && false || true) ? 1 : 2", 1)]

    // '||' above the ternary, which is the lowest thing there is: (false || true) ? 1 : 2.
    [InlineData("false || true ? 1 : 2", 1)]
    public void Follows_Cs_precedence(string expression, int expected)
    {
        Assert.Equal(expected, Radius(expression));
    }

    [Theory]

    // Only the shifts can say anything here: '&', '^' and '|' are associative, so no scene
    // can tell which way they grouped, and a test asserting it would pass either way.
    [InlineData("16 >> 2 >> 1", 2)]
    [InlineData("1 << 2 << 3", 32)]
    public void Groups_a_repeated_shift_to_the_left(string expression, int expected)
    {
        Assert.Equal(expected, Radius(expression));
    }

    [Fact]
    public void Keeps_the_C_precedence_that_makes_a_masked_comparison_a_mistake()
    {
        // C's table puts '==' tighter than '&', so this reads '1 & (1 == 1)' and not
        // '(1 & 1) == 1'. Reproducing that order matters more than liking it, and the type
        // rule turns the trap into a diagnostic rather than into a wrong number.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load("sphere { radius: 1 & 1 == 1 }");

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("takes two booleans or two whole numbers"));
    }

    [Theory]
    [InlineData("1.5 & 1")]
    [InlineData("~0.5")]
    [InlineData("1 << 0.5")]
    public void Refuses_a_fractional_operand(string expression)
    {
        // Reported rather than truncated, which is the choice a whole-number field already
        // makes: the language has one numeric type, so rounding silently would answer a
        // question the file did not ask.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load($"sphere {{ radius: {expression} }}");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("whole number"));
    }

    [Theory]
    [InlineData("true & 1")]
    [InlineData("1 | false")]
    [InlineData("true ^ 0")]
    public void Refuses_one_boolean_and_one_number(string expression)
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load($"sphere {{ radius: {expression} }}");

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("takes two booleans or two whole numbers"));
    }

    [Theory]
    [InlineData("[1, 2, 3] & 1")]
    [InlineData("~[1, 2, 3]")]
    public void Refuses_a_vector(string expression)
    {
        // Arithmetic broadcasts across a vector because a coordinate scaled is still a
        // coordinate. A bit pattern per component is not something a scene has ever wanted.
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load($"sphere {{ radius: {expression} }}");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("a vector of 3 components"));
    }

    [Theory]
    [InlineData("1 << 64")]
    [InlineData("1 >> -1")]
    public void Refuses_a_shift_count_with_no_answer(string expression)
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load($"sphere {{ radius: {expression} }}");

        Assert.Null(scene);
        Assert.Contains(diagnostics, d => d.Message.Contains("shifts by 0 to 63 places"));
    }

    [Theory]

    // An operand past 2^53, where a 64-bit float stops holding every whole number...
    [InlineData("18014398509481984 & 1")]

    // ... and a left shift, the one operator that can carry two operands in range out of it.
    [InlineData("1 << 62")]
    public void Refuses_a_whole_number_it_could_not_hold_exactly(string expression)
    {
        (Scene? scene, IReadOnlyList<Diagnostic> diagnostics) =
            TestSource.Load($"sphere {{ radius: {expression} }}");

        Assert.Null(scene);
        Assert.Contains(
            diagnostics,
            d => d.Message.Contains("largest whole number a scene can hold exactly"));
    }
}
