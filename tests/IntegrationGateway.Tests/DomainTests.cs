using IntegrationGateway.Domain;

namespace IntegrationGateway.Tests;

public class DomainTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(5, 32)]
    [InlineData(20, 300)]
    public void Backoff_is_exponential_and_capped(int attempt, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RetryPolicy.Delay(attempt, 2, 300));

    [Fact]
    public void Mapper_selects_nested_fields_and_array_items()
    {
        var payload = """{"orderId":"A1","customer":{"name":"Thandi","email":"t@x.co.za"},"items":[{"sku":"S1"},{"sku":"S2"}]}""";
        var mapped = PayloadMapper.Map(payload, new Dictionary<string, string>
        {
            ["ref"] = "$.orderId", ["email"] = "$.customer.email", ["secondSku"] = "$.items[1].sku", ["missing"] = "$.nope.deeper",
        });
        Assert.Equal("""{"ref":"A1","email":"t@x.co.za","secondSku":"S2","missing":null}""", mapped);
    }

    [Fact]
    public void Mapper_passes_through_without_map() =>
        Assert.Equal("""{"a":1}""", PayloadMapper.Map("""{"a":1}""", new Dictionary<string, string>()));

    [Fact]
    public void Hmac_signature_roundtrip()
    {
        var sig = Signatures.Sign("secret", "{\"a\":1}");
        Assert.StartsWith("sha256=", sig);
        Assert.True(Signatures.Verify("secret", "{\"a\":1}", sig));
        Assert.False(Signatures.Verify("secret", "{\"a\":2}", sig));
        Assert.False(Signatures.Verify("secret", "{\"a\":1}", null));
    }
}
