using System.Text.RegularExpressions;

namespace CCaaS.Application.Appointment.Voice;

public record ParsedAppointmentInput(DateOnly? Date, TimeOnly? Time, bool AmbiguousTime,
    string? Phone, string? Name, string? Reference, string? Error);

public static class BanglaAppointmentParser
{
    public static string Normalize(string input)
    {
        const string digits = "০১২৩৪৫৬৭৮৯";
        return new string(input.Trim().ToLowerInvariant().Select(c => digits.IndexOf(c) is var n && n >= 0 ? (char)('0' + n) : c).ToArray());
    }
    public static bool Yes(string text) => Regex.IsMatch(Normalize(text), @"^(হ্যাঁ|হ্যা|হ্যঁা|জি|ঠিক আছে|নিশ্চিত করুন|yes|yes please|confirm|okay|ok)[.!।\s]*$");
    public static bool No(string text) => Regex.IsMatch(Normalize(text), @"^(না|না ধন্যবাদ|no|no thanks)[.!।\s]*$");
    public static string? Intent(string input)
    {
        var text = Normalize(input);
        if (Regex.IsMatch(text, "reschedule|পুনর্নির্ধারণ|সময় বদল|সময় বদল|তারিখ বদল")) return "reschedule";
        if (Regex.IsMatch(text, @"\bcancel\b|বাতিল")) return "cancel";
        if (Regex.IsMatch(text, "availability|খালি সময়|খালি সময়")) return "query";
        if (Regex.IsMatch(text, "appointment|অ্যাপয়েন্টমেন্ট|অ্যাপয়েন্টমেন্ট|অ্যাপয়েন্ট|অ্যাপয়েন্ট|অ্যাপয়েন|বুকিং|বুক করুন|অ্যাপয়ন্ট")) return "book";
        return null;
    }
    public static ParsedAppointmentInput Parse(string input, DateOnly today)
    {
        var text = Normalize(input);
        DateOnly? date = null; TimeOnly? time = null; string? error = null;
        var phone = Regex.Match(text, @"(?<!\d)(?:\+?88)?01[3-9]\d{8}(?!\d)");
        string? contact = phone.Success ? phone.Value.Replace("+", "") : null;
        if (contact?.StartsWith("88") == true) contact = contact[2..];
        if (!phone.Success && Regex.IsMatch(Regex.Replace(text, @"\bapt-\d{8}-[a-f0-9]{10}\b", ""), @"(?<!\d)\+?\d{7,}(?!\d)")) error = "invalid_phone";
        var reference = Regex.Match(text, @"\bapt-\d{8}-[a-f0-9]{10}\b", RegexOptions.IgnoreCase);
        var nameMatch = Regex.Match(input, @"(?:my name is|name is|আমার নাম)\s*[:,-]?\s*(.+?)(?=\s+(?:and|phone|contact|ফোন|নম্বর)|$)", RegexOptions.IgnoreCase);
        var name = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : null;
        // Strip phone/reference so their digits can never become an appointment time/date.
        text = Regex.Replace(text, @"(?<!\d)(?:\+?88)?01[3-9]\d{8}(?!\d)|\bapt-\d{8}-[a-f0-9]{10}\b", " ");
        var explicitDate = Regex.Match(text, @"(?<!\d)(20\d{2})[-/](\d{1,2})[-/](\d{1,2})(?!\d)");
        var shortDate = Regex.Match(text, @"(?<!\d)(\d{1,2})/(\d{1,2})(?:/(20\d{2}))?(?!\d)");
        try
        {
            if (explicitDate.Success)
                date = new DateOnly(int.Parse(explicitDate.Groups[1].Value), int.Parse(explicitDate.Groups[2].Value), int.Parse(explicitDate.Groups[3].Value));
            else if (shortDate.Success)
                date = new DateOnly(shortDate.Groups[3].Success ? int.Parse(shortDate.Groups[3].Value) : today.Year,
                    int.Parse(shortDate.Groups[2].Value), int.Parse(shortDate.Groups[1].Value));
            else if (Regex.IsMatch(text, "পরশু|day after tomorrow")) date = today.AddDays(2);
            else if (Regex.IsMatch(text, "আগামীকাল|tomorrow|^কাল(?:\\s|$)")) date = today.AddDays(1);
            else if (Regex.IsMatch(text, "গতকাল|yesterday")) date = today.AddDays(-1);
            else if (Regex.IsMatch(text, "আজ|today")) date = today;
            else
            {
                string[] days = ["রবিবার|sunday", "সোমবার|monday", "মঙ্গলবার|tuesday", "বুধবার|wednesday", "বৃহস্পতিবার|thursday", "শুক্রবার|friday", "শনিবার|saturday"];
                for (var i = 0; i < days.Length; i++)
                    if (Regex.IsMatch(text, days[i])) { var delta = (i - (int)today.DayOfWeek + 7) % 7; date = today.AddDays(delta == 0 ? 7 : delta); break; }
                if (date is null)
                {
                    string[] months = ["জানুয়ারি|জানুয়ারি|january", "ফেব্রুয়ারি|ফেব্রুয়ারি|february", "মার্চ|march", "এপ্রিল|april", "মে|may", "জুন|june", "জুলাই|july", "আগস্ট|august", "সেপ্টেম্বর|september", "অক্টোবর|october", "নভেম্বর|november", "ডিসেম্বর|december"];
                    for (var i = 0; i < months.Length; i++)
                    {
                        var m = Regex.Match(text, @"(?<!\d)(\d{1,2})\s*(?:" + months[i] + @")(?:\s+(20\d{2}))?");
                        if (!m.Success) continue;
                        date = new DateOnly(m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : today.Year, i + 1, int.Parse(m.Groups[1].Value)); break;
                    }
                    var dayOnly = Regex.Match(text, @"(?<!\d)(\d{1,2})\s*তারিখ");
                    if (date is null && dayOnly.Success) date = new DateOnly(today.Year, today.Month, int.Parse(dayOnly.Groups[1].Value));
                }
            }
        }
        catch (ArgumentOutOfRangeException) { error = "invalid_date"; }
        if (date < today) error = "past_date";
        text = Regex.Replace(text, @"(?<!\d)20\d{2}[-/]\d{1,2}[-/]\d{1,2}|(?<!\d)\d{1,2}/\d{1,2}(?:/20\d{2})?", " ");
        string[] numbers = ["শূন্য", "এক", "দুই", "তিন", "চার", "পাঁচ", "ছয়|ছয়", "সাত", "আট", "নয়|নয়", "দশ", "এগারো", "বারো"];
        for (var i = 0; i < numbers.Length; i++)
            text = Regex.Replace(text, @"(?<![\p{L}\p{M}])(?:" + numbers[i] + @")(?=\s*(?:টা|টায়|টায়|am\b|pm\b)|\s*$)", i.ToString());
        text = Regex.Replace(text, "দেড়টা|দেড়টা", "1:30টা");
        text = Regex.Replace(text, "আড়াইটা|আড়াইটা", "2:30টা");
        var match = Regex.Match(text, @"(?<!\d)(?:(সাড়ে|সাড়ে|পৌনে)\s*)?(\d{1,2})(?::(\d{2}))?\s*(টা(?:য়|য়|ই)?|am\b|pm\b)");
        if (!match.Success) match = Regex.Match(text, @"(?<!\d)(\d{1,2}):(\d{2})(?!\d)");
        bool ambiguous = false;
        if (match.Success)
        {
            bool full = match.Groups.Count > 4;
            var hour = int.Parse(match.Groups[full ? 2 : 1].Value);
            var minuteGroup = match.Groups[full ? 3 : 2];
            var minute = minuteGroup.Success ? int.Parse(minuteGroup.Value) : 0;
            if (full && match.Groups[1].Success)
            {
                if (match.Groups[1].Value == "পৌনে") { hour = (hour + 11) % 12; minute = 45; }
                else minute = 30;
            }
            var night = text.Contains("রাত");
            var pm = Regex.IsMatch(text, @"\bpm\b|দুপুর|বিকাল|বিকেল|সন্ধ্যা") || (night && hour is > 5 and < 12);
            var am = Regex.IsMatch(text, @"\bam\b|সকাল|ভোর") || (night && (hour <= 5 || hour == 12));
            if (pm && am) error = "ambiguous_time";
            else if (hour > 23 || minute > 59 || ((am || pm) && hour > 12)) error = "invalid_time";
            else
            {
                ambiguous = !am && !pm && hour is >= 1 and <= 12;
                if (pm && hour < 12) hour += 12;
                if (am && hour == 12) hour = 0;
                time = new TimeOnly(hour, minute);
            }
        }
        return new(date, time, ambiguous, contact, name, reference.Success ? reference.Value.ToUpperInvariant() : null, error);
    }
}
