namespace CCaaS.Domain.Common;

/// <summary>
/// Result of normalising a raw, human-entered phone number into E.164.
/// </summary>
/// <param name="Succeeded">True when <paramref name="E164"/> holds a usable number.</param>
/// <param name="E164">The normalised number, e.g. "+8801712345678". Null on failure.</param>
/// <param name="Error">Human-readable reason the value could not be normalised. Null on success.</param>
public readonly record struct PhoneNormalizationResult(bool Succeeded, string? E164, string? Error)
{
    public static PhoneNormalizationResult Ok(string e164) => new(true, e164, null);
    public static PhoneNormalizationResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// Dependency-free E.164 normaliser.
///
/// WHY THIS EXISTS
/// ---------------
/// Every outbound-compliance decision (Do-Not-Call matching, consent lookup, duplicate
/// detection on list upload) compares phone numbers as strings. If the same subscriber is
/// stored once as "01712345678" and once as "+8801712345678", a DNC entry on one form will
/// NOT match the other, and the platform will place a call it was told never to place.
///
/// Therefore: numbers are normalised ONCE, at write time, and every comparison uses the
/// normalised form. Reading code must never normalise on the fly.
///
/// SCOPE
/// -----
/// This handles the country set the product actually dials today plus a generic fallback.
/// It is deliberately not a full E.164 implementation. If/when the product needs carrier
/// lookup, number-type detection (mobile vs landline vs premium) or full national numbering
/// plans, replace the internals with libphonenumber-csharp — the public surface here
/// (TryNormalize / Normalize / IsE164) is intentionally the same shape so that swap is local.
/// </summary>
public static class PhoneNumber
{
    /// <summary>E.164 allows at most 15 digits including the country code.</summary>
    private const int MaxE164Digits = 15;

    /// <summary>Shortest number we will accept at all (guards against "12" style junk).</summary>
    private const int MinE164Digits = 8;

    /// <summary>
    /// A national numbering plan, reduced to what normalisation actually needs.
    /// </summary>
    /// <param name="CountryCode">Digits after the plus, e.g. "880".</param>
    /// <param name="TrunkPrefix">National dialling prefix stripped before prepending the
    /// country code, e.g. "0" in Bangladesh. Empty when the plan has none.</param>
    /// <param name="MinNationalDigits">Shortest valid subscriber number, trunk prefix excluded.</param>
    /// <param name="MaxNationalDigits">Longest valid subscriber number, trunk prefix excluded.</param>
    private sealed record NumberingPlan(
        string CountryCode,
        string TrunkPrefix,
        int MinNationalDigits,
        int MaxNationalDigits);

    /// <summary>
    /// Regions the product dials. Keyed by ISO 3166-1 alpha-2, upper case.
    /// Add a row here rather than special-casing a call site.
    /// </summary>
    private static readonly Dictionary<string, NumberingPlan> Plans = new(StringComparer.OrdinalIgnoreCase)
    {
        // Bangladesh mobile is 01[3-9] + 8 digits => 10 national digits after the trunk "0".
        ["BD"] = new NumberingPlan("880", "0", 10, 10),

        // North American Numbering Plan: 3-digit area code + 7 digits, no trunk prefix in E.164 terms.
        ["US"] = new NumberingPlan("1", "1", 10, 10),
        ["CA"] = new NumberingPlan("1", "1", 10, 10),

        ["GB"] = new NumberingPlan("44", "0", 9, 10),
        ["IN"] = new NumberingPlan("91", "0", 10, 10),

        // Gulf + South-East Asia: the markets with large Bangla-speaking populations.
        ["AE"] = new NumberingPlan("971", "0", 8, 9),
        ["SA"] = new NumberingPlan("966", "0", 8, 9),
        ["QA"] = new NumberingPlan("974", "", 8, 8),
        ["KW"] = new NumberingPlan("965", "", 8, 8),
        ["OM"] = new NumberingPlan("968", "", 8, 8),
        ["MY"] = new NumberingPlan("60", "0", 9, 10),
        ["SG"] = new NumberingPlan("65", "", 8, 8),
    };

    /// <summary>
    /// Normalises <paramref name="raw"/> to E.164 without throwing.
    /// </summary>
    /// <param name="raw">Anything a human or an imported spreadsheet might contain:
    /// "+880 1712-345678", "01712345678", "008801712345678", "(555) 010-2030".</param>
    /// <param name="defaultRegion">ISO 3166-1 alpha-2 region applied when the value carries no
    /// country code of its own. Pass the tenant's own region, not a hard-coded constant.</param>
    public static PhoneNormalizationResult TryNormalize(string? raw, string defaultRegion = "BD")
    {
        if (string.IsNullOrWhiteSpace(raw))
            return PhoneNormalizationResult.Fail("The phone number is empty.");

        // Keep only digits, remembering whether the caller wrote an explicit "+".
        var trimmed = raw.Trim();
        var hadPlus = trimmed.StartsWith('+');
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());

        if (digits.Length == 0)
            return PhoneNormalizationResult.Fail($"'{raw}' contains no digits.");

        // "00" is the international access code in most of the world; treat it as a written "+".
        if (!hadPlus && digits.StartsWith("00", StringComparison.Ordinal))
        {
            hadPlus = true;
            digits = digits[2..];
        }

        if (hadPlus)
            return FromInternational(digits, raw);

        if (!Plans.TryGetValue(defaultRegion, out var plan))
            return PhoneNormalizationResult.Fail(
                $"Region '{defaultRegion}' has no numbering plan configured, so '{raw}' cannot be normalised. " +
                "Store the number in full international form (starting with +) or add the region to PhoneNumber.Plans.");

        // The value may already carry its own country code even without a "+",
        // e.g. a spreadsheet column holding "8801712345678".
        if (digits.StartsWith(plan.CountryCode, StringComparison.Ordinal))
        {
            var withoutCountryCode = digits[plan.CountryCode.Length..];
            if (IsNationalLengthValid(withoutCountryCode, plan))
                return PhoneNormalizationResult.Ok("+" + plan.CountryCode + withoutCountryCode);
        }

        var national = digits;

        // A value that opens with the national dialling prefix MUST be valid once that prefix
        // is removed. Falling back to the unstripped form would quietly accept a short number:
        // Bangladeshi "0171234567" (one digit missing) is exactly 10 digits with the leading
        // zero still attached, so a length check alone would pass it and produce the wrong
        // subscriber, +8800171234567.
        if (plan.TrunkPrefix.Length > 0 && national.StartsWith(plan.TrunkPrefix, StringComparison.Ordinal))
        {
            var stripped = national[plan.TrunkPrefix.Length..];
            if (!IsNationalLengthValid(stripped, plan))
                return InvalidNationalLength(raw, defaultRegion, stripped.Length, plan);

            national = stripped;
        }
        else if (!IsNationalLengthValid(national, plan))
        {
            return InvalidNationalLength(raw, defaultRegion, national.Length, plan);
        }

        return PhoneNormalizationResult.Ok("+" + plan.CountryCode + national);
    }

    /// <summary>
    /// Normalises to E.164 or throws. Use from command handlers that already translate
    /// <see cref="ArgumentException"/> into a validation response; prefer
    /// <see cref="TryNormalize"/> inside bulk import loops, which must collect errors per row.
    /// </summary>
    public static string Normalize(string? raw, string defaultRegion = "BD")
    {
        var result = TryNormalize(raw, defaultRegion);
        if (!result.Succeeded)
            throw new ArgumentException(result.Error, nameof(raw));
        return result.E164!;
    }

    /// <summary>
    /// True when <paramref name="value"/> is already stored in the canonical form this class
    /// produces: a leading "+" followed only by digits, 8-15 of them.
    /// </summary>
    public static bool IsE164(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '+')
            return false;

        var digits = value.AsSpan(1);
        if (digits.Length is < MinE164Digits or > MaxE164Digits)
            return false;

        foreach (var c in digits)
            if (!char.IsDigit(c))
                return false;

        return true;
    }

    /// <summary>
    /// Handles a value the caller already expressed internationally, so the country code is
    /// whatever the digits say and no default region applies.
    /// </summary>
    private static PhoneNormalizationResult FromInternational(string digits, string raw)
    {
        if (digits.Length < MinE164Digits)
            return PhoneNormalizationResult.Fail(
                $"'{raw}' has only {digits.Length} digits; an international number needs at least {MinE164Digits}.");

        if (digits.Length > MaxE164Digits)
            return PhoneNormalizationResult.Fail(
                $"'{raw}' has {digits.Length} digits; E.164 allows at most {MaxE164Digits}.");

        return PhoneNormalizationResult.Ok("+" + digits);
    }

    private static PhoneNormalizationResult InvalidNationalLength(
        string raw, string region, int actualDigits, NumberingPlan plan)
    {
        var expected = plan.MinNationalDigits == plan.MaxNationalDigits
            ? $"{plan.MinNationalDigits} digits"
            : $"{plan.MinNationalDigits}-{plan.MaxNationalDigits} digits";

        return PhoneNormalizationResult.Fail(
            $"'{raw}' is not a valid {region.ToUpperInvariant()} number. " +
            $"Expected {expected} after the national prefix, but found {actualDigits}.");
    }

    private static bool IsNationalLengthValid(string national, NumberingPlan plan) =>
        national.Length >= plan.MinNationalDigits && national.Length <= plan.MaxNationalDigits;
}
