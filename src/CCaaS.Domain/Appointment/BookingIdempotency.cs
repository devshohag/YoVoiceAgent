using System.Security.Cryptography;
using System.Text;

namespace CCaaS.Domain.Appointment;

public static class BookingIdempotency
{
    public static string Derive(Guid tenantId, Guid slotId, string normalisedContact)
    {
        if (tenantId == Guid.Empty || slotId == Guid.Empty)
            throw new ArgumentException("Tenant and slot are required.");
        if (string.IsNullOrWhiteSpace(normalisedContact))
            throw new ArgumentException("A normalised contact is required.");
        var material = $"{tenantId:N}:{slotId:N}:{normalisedContact.Trim().ToLowerInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
