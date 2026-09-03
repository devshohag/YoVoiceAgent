using CCaaS.Infrastructure.Identity;
using Xunit;

namespace CCaaS.Tests.Unit.Identity;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_ThenVerify_WithCorrectPassword_Succeeds()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var hash = hasher.Hash("Correct-Horse-Battery-Staple-1");

        Assert.True(hasher.Verify("Correct-Horse-Battery-Staple-1", hash));
    }

    [Fact]
    public void Verify_WithWrongPassword_Fails()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var hash = hasher.Hash("Correct-Horse-Battery-Staple-1");

        Assert.False(hasher.Verify("something-else", hash));
    }

    [Fact]
    public void Hash_ProducesDifferentOutputForSamePasswordEachTime()
    {
        // Confirms a random salt is actually being used per call.
        var hasher = new Pbkdf2PasswordHasher();
        var hash1 = hasher.Hash("same-password");
        var hash2 = hasher.Hash("same-password");

        Assert.NotEqual(hash1, hash2);
    }
}
