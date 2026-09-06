using CCaaS.Application.Identity;
using CCaaS.Domain.Identity;
using CCaaS.Infrastructure.Identity;
using CCaaS.Tests.Unit.Fakes;
using Xunit;

namespace CCaaS.Tests.Unit.Identity;

public class AuthServiceChangePasswordTests
{
    [Fact]
    public async Task ChangePasswordAsync_WithValidCurrentPassword_ChangesHashAndRevokesTokens()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var user = new User
        {
            TenantId = Guid.NewGuid(),
            Email = "admin@example.test",
            DisplayName = "Admin",
            PasswordHash = hasher.Hash("OldPassword@123")
        };
        var token = new RefreshToken
        {
            TenantId = user.TenantId,
            UserId = user.Id,
            TokenHash = "token",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            CreatedByIp = "test"
        };

        var sut = CreateSut(hasher, out var users, out var tokens, out var unitOfWork);
        users.Items.Add(user);
        tokens.Items.Add(token);

        var result = await sut.ChangePasswordAsync(user.Id,
            new ChangePasswordRequest("OldPassword@123", "NewPassword@456", "NewPassword@456"));

        Assert.True(result.Succeeded);
        Assert.True(hasher.Verify("NewPassword@456", user.PasswordHash));
        Assert.NotNull(token.RevokedAt);
        Assert.Equal(1, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task ChangePasswordAsync_WithIncorrectCurrentPassword_DoesNotChangeHash()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var originalHash = hasher.Hash("OldPassword@123");
        var user = new User
        {
            TenantId = Guid.NewGuid(),
            Email = "admin@example.test",
            DisplayName = "Admin",
            PasswordHash = originalHash
        };

        var sut = CreateSut(hasher, out var users, out _, out var unitOfWork);
        users.Items.Add(user);

        var result = await sut.ChangePasswordAsync(user.Id,
            new ChangePasswordRequest("WrongPassword@1", "NewPassword@456", "NewPassword@456"));

        Assert.False(result.Succeeded);
        Assert.Equal(originalHash, user.PasswordHash);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task ChangePasswordAsync_WithWeakNewPassword_IsRejected()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var sut = CreateSut(hasher, out _, out _, out var unitOfWork);

        var result = await sut.ChangePasswordAsync(Guid.NewGuid(),
            new ChangePasswordRequest("OldPassword@123", "weak", "weak"));

        Assert.False(result.Succeeded);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    private static AuthService CreateSut(
        IPasswordHasher hasher,
        out FakeRepository<User> users,
        out FakeRepository<RefreshToken> tokens,
        out FakeUnitOfWork unitOfWork)
    {
        users = new FakeRepository<User>();
        tokens = new FakeRepository<RefreshToken>();
        unitOfWork = new FakeUnitOfWork();

        return new AuthService(
            users,
            new FakeRepository<UserRole>(),
            new FakeRepository<Role>(),
            new FakeRepository<RolePermission>(),
            new FakeRepository<Permission>(),
            tokens,
            unitOfWork,
            hasher,
            new FakeJwtTokenGenerator());
    }

    private sealed class FakeJwtTokenGenerator : IJwtTokenGenerator
    {
        public string GenerateAccessToken(User user, Guid tenantId, IReadOnlyCollection<string> roles,
            IReadOnlyCollection<string> permissions) => "access-token";

        public string GenerateRefreshToken() => "refresh-token";
    }
}
