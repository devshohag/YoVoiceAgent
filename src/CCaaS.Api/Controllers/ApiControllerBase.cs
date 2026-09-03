using Microsoft.AspNetCore.Mvc;

namespace CCaaS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>
    /// Section 7 security invariant: every controller reads TenantId from the validated JWT
    /// claim - NEVER from a route/query parameter - so a caller can never pass someone else's
    /// tenant id and reach across tenant boundaries.
    /// </summary>
    protected Guid TenantId
    {
        get
        {
            var claim = User.FindFirst("tenant_id")?.Value;
            return claim is not null && Guid.TryParse(claim, out var id) ? id : throw new UnauthorizedAccessException("Missing tenant_id claim.");
        }
    }
}
