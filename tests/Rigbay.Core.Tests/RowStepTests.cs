using Xunit;
using Rigbay.Abstractions;
using Rigbay.Core;

namespace Rigbay.Core.Tests;

/// <summary>UF (polish before 0.1.2): a script step can name a row whose header is a literal ("=example-tunnel"), although '=' separates the value.</summary>
public class RowStepTests
{
    [Theory]
    [InlineData("row.mode.header=max", "row.mode.header", "max")]
    [InlineData("row.add.header", "row.add.header", null)]
    [InlineData("row.name.header=", "row.name.header", "")]
    [InlineData("row.name.header=a=b", "row.name.header", "a=b")]
    [InlineData("==example-tunnel=on", "=example-tunnel", "on")]
    [InlineData("==example-tunnel", "=example-tunnel", null)]
    [InlineData("==a==b=c=d", "=a=b", "c=d")]
    [InlineData("a===v", "a=", "v")]
    [InlineData("", "", null)]
    public void The_first_single_equals_sign_separates_and_a_doubled_one_belongs_to_the_key(string arg, string key, string? value)
    {
        Assert.Equal((key, value), RowStep.Parse(arg));
    }

    [Fact]
    public void A_literal_header_written_with_Escape_comes_back_unchanged()
    {
        foreach (var k in new[] { Text.Literal("example-tunnel"), Text.Literal("a=b"), "row.mode.header", Text.Literal("") })
        {
            Assert.Equal((k, null), RowStep.Parse(RowStep.Escape(k)));
            Assert.Equal((k, "on"), RowStep.Parse(RowStep.Escape(k) + "=on"));
        }
        Assert.Equal("==example-tunnel", RowStep.Escape(Text.Literal("example-tunnel")));
    }

    [Fact]
    public void Null_is_an_empty_key()
    {
        Assert.Equal(("", (string?)null), RowStep.Parse(null));
    }
}
