using CCaaS.Domain.Common;
using Xunit;

namespace CCaaS.Tests.Unit.Common;

// Task 2.1 - phone normalisation.
//
// These tests exist because the whole outbound compliance layer compares phone numbers as
// strings. If "01712345678" and "+8801712345678" can both be stored, a Do-Not-Call entry on
// one form will not match the other and the platform will place a call it was told not to.
// Every case below is therefore a compliance test wearing a formatting costume.
public class PhoneNumberTests
{
    // ---------------------------------------------------------------- Bangladesh, local forms

    [Theory]
    [InlineData("01712345678")]          // plain local
    [InlineData("017 1234 5678")]        // spaces
    [InlineData("017-1234-5678")]        // dashes
    [InlineData("(017) 1234-5678")]      // punctuation
    [InlineData("  01712345678  ")]      // padding
    [InlineData("8801712345678")]        // country code, no plus
    [InlineData("+8801712345678")]       // canonical
    [InlineData("+880 1712 345678")]     // canonical with spaces
    [InlineData("008801712345678")]      // international access code
    public void TryNormalize_BangladeshMobile_AllWrittenFormsCollapseToOneValue(string raw)
    {
        var result = PhoneNumber.TryNormalize(raw, "BD");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("+8801712345678", result.E164);
    }

    [Theory]
    [InlineData("01312345678", "+8801312345678")]
    [InlineData("01912345678", "+8801912345678")]
    [InlineData("01512345678", "+8801512345678")]
    public void TryNormalize_BangladeshOperatorPrefixes_AreAllAccepted(string raw, string expected)
    {
        var result = PhoneNumber.TryNormalize(raw, "BD");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(expected, result.E164);
    }

    [Theory]
    [InlineData("0171234567")]    // one digit short
    [InlineData("017123456789")]  // one digit long
    [InlineData("1712345")]       // far too short
    public void TryNormalize_BangladeshWrongLength_FailsInsteadOfGuessing(string raw)
    {
        var result = PhoneNumber.TryNormalize(raw, "BD");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    // ---------------------------------------------------------------- other regions

    [Theory]
    [InlineData("5550102030", "US", "+15550102030")]
    [InlineData("(555) 010-2030", "US", "+15550102030")]
    [InlineData("15550102030", "US", "+15550102030")]      // trunk "1" already present
    [InlineData("+1 555 010 2030", "US", "+15550102030")]
    public void TryNormalize_UnitedStates_UsesNanpRules(string raw, string region, string expected)
    {
        var result = PhoneNumber.TryNormalize(raw, region);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(expected, result.E164);
    }

    [Theory]
    [InlineData("+971501234567")]   // UAE
    [InlineData("+6591234567")]     // Singapore
    [InlineData("+447911123456")]   // UK
    public void TryNormalize_AlreadyInternational_IsReturnedUnchangedRegardlessOfDefaultRegion(string raw)
    {
        // The default region must be ignored when the value carries its own country code,
        // otherwise a Gulf number imported by a Bangladeshi tenant would be rewritten.
        var result = PhoneNumber.TryNormalize(raw, "BD");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(raw.Replace(" ", ""), result.E164);
    }

    [Fact]
    public void TryNormalize_UnknownDefaultRegion_FailsWithAnActionableMessage()
    {
        var result = PhoneNumber.TryNormalize("0123456789", "ZZ");

        Assert.False(result.Succeeded);
        Assert.Contains("ZZ", result.Error);
    }

    // ---------------------------------------------------------------- rejected input

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalize_EmptyValue_Fails(string? raw)
    {
        var result = PhoneNumber.TryNormalize(raw, "BD");

        Assert.False(result.Succeeded);
        Assert.Null(result.E164);
    }

    [Theory]
    [InlineData("not a phone number")]
    [InlineData("N/A")]
    [InlineData("-")]
    public void TryNormalize_NoDigits_Fails(string raw)
    {
        var result = PhoneNumber.TryNormalize(raw, "BD");

        Assert.False(result.Succeeded);
        Assert.Contains("no digits", result.Error);
    }

    [Fact]
    public void TryNormalize_MoreThanFifteenDigits_FailsBecauseE164ForbidsIt()
    {
        var result = PhoneNumber.TryNormalize("+1234567890123456", "BD");

        Assert.False(result.Succeeded);
        Assert.Contains("15", result.Error);
    }

    [Fact]
    public void TryNormalize_TooFewDigitsInternationally_Fails()
    {
        var result = PhoneNumber.TryNormalize("+12345", "BD");

        Assert.False(result.Succeeded);
    }

    // ---------------------------------------------------------------- idempotence

    [Theory]
    [InlineData("01712345678", "BD")]
    [InlineData("5550102030", "US")]
    [InlineData("+971501234567", "BD")]
    public void TryNormalize_IsIdempotent_SoRepeatedWritesCannotDrift(string raw, string region)
    {
        var once = PhoneNumber.TryNormalize(raw, region);
        var twice = PhoneNumber.TryNormalize(once.E164, region);

        Assert.True(twice.Succeeded, twice.Error);
        Assert.Equal(once.E164, twice.E164);
    }

    // ---------------------------------------------------------------- Normalize / IsE164

    [Fact]
    public void Normalize_InvalidValue_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => PhoneNumber.Normalize("abc", "BD"));

        Assert.NotNull(ex.Message);
    }

    [Fact]
    public void Normalize_ValidValue_ReturnsE164()
    {
        Assert.Equal("+8801712345678", PhoneNumber.Normalize("01712345678", "BD"));
    }

    [Theory]
    [InlineData("+8801712345678", true)]
    [InlineData("+15550102030", true)]
    [InlineData("8801712345678", false)]   // missing plus
    [InlineData("+880 1712345678", false)] // contains a space
    [InlineData("+12345", false)]          // too short
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsE164_RecognisesOnlyTheCanonicalStoredForm(string? value, bool expected)
    {
        Assert.Equal(expected, PhoneNumber.IsE164(value));
    }

    [Fact]
    public void IsE164_AcceptsEveryValueTryNormalizeProduces()
    {
        string[] inputs = ["01712345678", "8801712345678", "+8801712345678", "008801712345678"];

        foreach (var input in inputs)
        {
            var result = PhoneNumber.TryNormalize(input, "BD");
            Assert.True(result.Succeeded, result.Error);
            Assert.True(PhoneNumber.IsE164(result.E164), $"'{input}' normalised to a non-canonical value.");
        }
    }
}
