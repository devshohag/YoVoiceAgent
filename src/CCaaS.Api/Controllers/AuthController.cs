using CCaaS.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CCaaS.Api.Controllers;

/// <summary>
/// Note: unlike other controllers, tenant id here comes from the request body/route for
/// register+login (there's no JWT yet to read it from) - the tenant a user is registering
/// into is a legitimate caller-supplied value at this one boundary. Every OTHER controller
/// in this template must keep reading TenantId from the validated claim, never from input.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService) => _authService = authService;

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        var result = await _authService.RegisterAsync(request, ct);
        return result.Succeeded ? Ok() : BadRequest(result.Error);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, [FromQuery] Guid tenantId, CancellationToken ct)
    {
        var result = await _authService.LoginAsync(request, tenantId, ct);
        return result.Succeeded ? Ok(new { result.AccessToken, result.RefreshToken }) : Unauthorized(result.Error);
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh([FromBody] string refreshToken, CancellationToken ct)
    {
        var result = await _authService.RefreshAsync(refreshToken, ct);
        return result.Succeeded ? Ok(new { result.AccessToken, result.RefreshToken }) : Unauthorized(result.Error);
    }
}
