using System.Text.RegularExpressions;
using CCaaS.Domain.Ai;

namespace CCaaS.Application.Routing;

public enum TurnRoute
{
    General,
    Appointment,
    HumanHandoff
}

public static partial class DeterministicTurnRouter
{
    public static TurnRoute Select(IReadOnlyList<AiConversationTurn> history, string input)
    {
        if (HumanRequestRegex().IsMatch(input)) return TurnRoute.HumanHandoff;
        if (history.Any(turn => turn.Speaker == AiSpeaker.System
            && turn.Text.StartsWith("[APPOINTMENT_STATE]", StringComparison.Ordinal)))
            return TurnRoute.Appointment;
        return AppointmentIntentRegex().IsMatch(input) ? TurnRoute.Appointment : TurnRoute.General;
    }

    [GeneratedRegex(@"appointment|book(?:ing)?|schedule|doctor|অ্যাপয়েন্টমেন্ট|এপয়েন্টমেন্ট|বুকিং|ডাক্তার|সময়\s*(?:চাই|নিতে)", RegexOptions.IgnoreCase)]
    private static partial Regex AppointmentIntentRegex();

    [GeneratedRegex(@"\b(human|operator|representative|supervisor|live\s+(person|agent)|real\s+(person|agent)|transfer\s+me)\b|মানুষ|হিউম্যান|এজেন্ট|অপারেটর|প্রতিনিধি|সুপারভাইজার|কথা\s*বলতে\s*চাই|কল\s*ট্রান্সফার", RegexOptions.IgnoreCase)]
    private static partial Regex HumanRequestRegex();
}