using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CCaaS.Domain.Calls;

/// <summary>Stable identity of one campaign contact's one dial attempt.</summary>
public static class CallIdempotency
{
    public static string Derive(Guid tenantId, Guid campaignId, Guid contactId, int attemptNumber)
    {
        if (tenantId == Guid.Empty || campaignId == Guid.Empty || contactId == Guid.Empty)
            throw new ArgumentException("Tenant, campaign and contact are required.");
        if (attemptNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(attemptNumber), "Attempt number must be positive.");

        var material = string.Join(':', tenantId.ToString("N"), campaignId.ToString("N"),
            contactId.ToString("N"), attemptNumber.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
