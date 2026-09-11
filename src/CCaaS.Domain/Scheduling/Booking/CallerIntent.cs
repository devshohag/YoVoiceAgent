using System.Text.RegularExpressions;

namespace CCaaS.Domain.Scheduling.Booking;

/// <summary>
/// The small set of conversational moves the booking flow has to recognise on top of dates and
/// times. Everything else is "the caller said something we could not use".
/// </summary>
public enum CallerMove
{
    /// <summary>Nothing recognisable. Not an error - most utterances carry a date instead.</summary>
    None,

    /// <summary>"Yes", "that's right", "go ahead", "perfect".</summary>
    Affirmative,

    /// <summary>"No", "that's wrong", "not that one".</summary>
    Negative,

    /// <summary>"I don't know", "not sure", "whatever suits you". Distinct from a plain no.</summary>
    Unsure,

    /// <summary>"Let me speak to someone", "is there a person", "agent". Always honoured.</summary>
    WantsHuman,

    /// <summary>"Forget it", "I'll call back", "not interested", "stop calling".</summary>
    WantsToEnd
}

/// <summary>
/// Recognises the conversational moves above from raw transcript text.
///
/// This is keyword matching on purpose, not a model. These six decisions gate irreversible
/// actions - booking, transferring, hanging up - and a deterministic rule that is wrong in a
/// known way beats a classifier that is wrong in an unknown one. It also costs no latency,
/// which matters because this runs on the turn-taking path of every single utterance.
/// </summary>
public static class CallerIntent
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Checked FIRST, before the negatives: "I'm not sure" contains "not", and treating it as a
    // refusal would answer a question the caller has not actually answered.
    private static readonly Regex Unsure = new(
        // "anything" is deliberately absent: "is there anything on Tuesday" is a question with a
        // date in it, not a caller expressing no preference.
        @"\b(?:dont know|do not know|dunno|not sure|no idea|either|whatever|"
        + @"you (?:choose|decide|pick)|up to you|any ?time)\b", Opts);

    // Checked before Negative too: "no I want to talk to a person" is a transfer, not a no.
    private static readonly Regex WantsHuman = new(
        @"\b(?:real person|a person|human|someone else|somebody else|"
        + @"(?:speak|talk|transfer|connect|put me)\s+(?:to|with|through)\b|"
        + @"customer service|representative|operator|manager|(?:an?\s+)?agent)\b", Opts);

    private static readonly Regex WantsToEnd = new(
        @"\b(?:not interested|no thanks|no thank you|forget it|never ?mind|"
        + @"cancel(?: it| that)?|stop calling|remove me|take me off|"
        + @"call me (?:back|later)|call back later|maybe later|bye|goodbye|hang up)\b", Opts);

    private static readonly Regex Affirmative = new(
        @"\b(?:yes|yeah|yep|yup|yah|sure|ok|okay|correct|right|"
        + @"thats right|thats correct|thats it|sounds good|works|"
        + @"perfect|great|fine|please do|go ahead|confirm(?:ed)?|book it|lets do it)\b", Opts);

    private static readonly Regex Negative = new(
        @"\b(?:no|nope|nah|not that|wrong|incorrect|thats not|"
        + @"cant|cannot|wont work|doesnt work|does not work|not good|"
        + @"different|another|something else|change (?:it|that))\b", Opts);

    /// <summary>
    /// Classifies one utterance. Order matters and is the whole design: the moves that carry an
    /// obligation (a human, an ending) are recognised before the ones that merely answer a
    /// question, because a caller who asks for a person while saying "no" is asking for a person.
    /// </summary>
    public static CallerMove Detect(string? utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance))
            return CallerMove.None;

        var text = Flatten(utterance);

        if (WantsHuman.IsMatch(text)) return CallerMove.WantsHuman;
        if (WantsToEnd.IsMatch(text)) return CallerMove.WantsToEnd;
        if (Unsure.IsMatch(text)) return CallerMove.Unsure;

        var yes = Affirmative.IsMatch(text);
        var no = Negative.IsMatch(text);

        // "No, yes, sorry - Tuesday" happens. When both appear, the negative wins, because
        // acting on a false yes books the wrong thing and acting on a false no only re-asks.
        if (no) return CallerMove.Negative;
        if (yes) return CallerMove.Affirmative;

        return CallerMove.None;
    }

    /// <summary>
    /// Pulls a caller's name out of an utterance, conservatively.
    ///
    /// Returns null far more often than a greedy extractor would, and that is the point: a
    /// wrong name is read back to the caller, printed on the voucher and texted to them, while
    /// a missing one only costs one more question.
    /// </summary>
    public static string? ExtractName(string? utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance))
            return null;

        var text = utterance.Trim();

        // An explicit introduction is the only high-confidence case.
        var introduced = Introduction.Match(text);
        if (introduced.Success)
            return CleanName(introduced.Groups[1].Value);

        // Otherwise only accept a short bare answer - the shape of someone replying "Rahim
        // Uddin" to "and your name?". Anything longer is a sentence, and a sentence is not a name.
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is >= 1 and <= 3 && words.All(IsNameLike))
            return CleanName(text);

        return null;
    }

    private static readonly Regex Introduction = new(
        @"\b(?:my name is|name is|this is|i am|im|its|it is|call me|speaking)\s+"
        + @"([a-z]+(?:\s+[a-z]+){0,2})\b", Opts);

    private static bool IsNameLike(string word) =>
        word.Length > 1 && word.All(c => char.IsLetter(c) || c == '-' || c == '\'');

    private static string? CleanName(string raw)
    {
        var cleaned = string.Join(' ', raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !Courtesy.Contains(w.Trim('.', ',')))
            .Select(w => w.Trim('.', ',')));

        if (cleaned.Length < 2)
            return null;

        // "I'm free on Tuesday" must not yield the name "Free". Any everyday word in the answer
        // means this was a sentence that happened to start like an introduction.
        if (cleaned.Split(' ').Any(w => NotNames.Contains(w)))
            return null;

        // Title-case, because the transcript arrives lower case and this ends up on a voucher.
        return string.Join(' ', cleaned.Split(' ')
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }

    /// <summary>
    /// Words that prove an utterance is a sentence rather than a name. Short and boring on
    /// purpose - it only has to cover what people actually say around a booking.
    /// </summary>
    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Free", "Busy", "Available", "Calling", "Here", "There", "Fine", "Good", "Well",
        "Not", "Booking", "Appointment", "Time", "Day", "Morning", "Afternoon", "Evening",
        "Night", "Today", "Tomorrow", "Yesterday", "Monday", "Tuesday", "Wednesday",
        "Thursday", "Friday", "Saturday", "Sunday", "Interested", "Sorry", "Waiting",
        "Looking", "Trying", "Wondering", "Working", "Home", "Office", "Customer"
    };

    private static readonly HashSet<string> Courtesy = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "miss", "dr", "sir", "madam", "maam",
        "please", "thanks", "thank", "you", "sure", "yes", "yeah", "ok", "okay", "um", "uh"
    };

    private static string Flatten(string raw)
    {
        var lowered = raw.ToLowerInvariant().Replace("'", "").Replace("’", "");
        var chars = lowered.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return Regex.Replace(new string(chars), @"\s+", " ").Trim();
    }
}
