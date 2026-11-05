using System.Text.Json;
using IDevelop.Clients;

namespace IDevelop.Core.Tests;

public sealed class JsonFieldsTests
{
    [Fact]
    public void Parse_returns_a_json_value_that_outlives_the_call()
    {
        Assert.Equal("banana", JsonFields.Parse("""{"name":"banana"}""")?.String("name"));
        Assert.Equal(JsonValueKind.Array, JsonFields.Parse("[1]")?.ValueKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"name":""")]
    [InlineData("""{"a":1} {"b":2}""")]
    public void Parse_returns_null_for_text_that_is_not_one_json_value(string text) => Assert.Null(JsonFields.Parse(text));
}
