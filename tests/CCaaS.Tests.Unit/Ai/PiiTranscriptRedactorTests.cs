using CCaaS.Infrastructure.Ai;

namespace CCaaS.Tests.Unit.Ai;

public class PiiTranscriptRedactorTests
{
    [Fact]
    public void Redact_MasksEmailPhoneAndCardNumber()
    {
        var sut = new PiiTranscriptRedactor();

        var result = sut.Redact("Email me at customer@example.com, call +1 202-555-0198, card 4111 1111 1111 1111.");

        Assert.DoesNotContain("customer@example.com", result);
        Assert.DoesNotContain("202-555-0198", result);
        Assert.DoesNotContain("4111 1111 1111 1111", result);
        Assert.Contains("[EMAIL-REDACTED]", result);
        Assert.Contains("[PHONE-REDACTED]", result);
        Assert.Contains("[CARD-REDACTED]", result);
    }
}
