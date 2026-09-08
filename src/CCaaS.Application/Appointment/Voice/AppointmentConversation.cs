using System.Text.RegularExpressions;

namespace CCaaS.Application.Appointment.Voice;

public record AppointmentConversationState(Guid TenantId, Guid SessionId)
{
    public string Stage { get; init; } = "intent";
    public string Operation { get; init; } = "book";
    public bool Bengali { get; init; } = true;
    public DateOnly? Date { get; init; }
    public TimeOnly? Time { get; init; }
    public bool AmbiguousTime { get; init; }
    public Guid? SlotId { get; init; }
    public DateTime? SlotUtc { get; init; }
    public string? Name { get; init; }
    public string? Phone { get; init; }
    public string? Reference { get; init; }
    public Guid? BookingId { get; init; }
    public string? LastTurnId { get; init; }
    public string? LastReply { get; init; }
}
public record AppointmentConversationReply(AppointmentConversationState State, string Text, string Action);

// No LLM calls. State is supplied/persisted by the caller; clock and timezone are explicit.
public sealed class AppointmentConversation(IAppointmentService appointments)
{
    public async Task<AppointmentConversationReply> StepAsync(Guid tenantId, Guid sessionId,
        AppointmentConversationState? current, string turnId, string input, DateTimeOffset now,
        TimeZoneInfo zone, bool bengali = true, CancellationToken ct = default)
    {
        try { return await StepCoreAsync(tenantId, sessionId, current, turnId, input, now, zone, bengali, ct); }
        catch (Exception ex) when (ex is not UnauthorizedAccessException && ex is not ArgumentException
            && ex is not OperationCanceledException)
        {
            // A timeout may happen after a commit. Never announce failure/success as certain.
            var state = current ?? new AppointmentConversationState(tenantId, sessionId) { Bengali = bengali };
            var message = state.Bengali ? "ফলাফল এখন নিশ্চিত করতে পারছি না। অনুগ্রহ করে প্রতিনিধির সাহায্য নিন।"
                : "I cannot verify the result right now. Please ask a representative for help.";
            return new(state with { LastTurnId = turnId, LastReply = message }, message, "backend_error");
        }
    }

    private async Task<AppointmentConversationReply> StepCoreAsync(Guid tenantId, Guid sessionId,
        AppointmentConversationState? current, string turnId, string input, DateTimeOffset now,
        TimeZoneInfo zone, bool bengali = true, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || sessionId == Guid.Empty || string.IsNullOrWhiteSpace(turnId))
            throw new ArgumentException("Tenant, session and turn are required.");
        if (current is not null && (current.TenantId != tenantId || current.SessionId != sessionId))
            throw new UnauthorizedAccessException("Conversation belongs to another tenant/session.");
        var state = current ?? new AppointmentConversationState(tenantId, sessionId) { Bengali = bengali };
        if (state.LastTurnId == turnId)
            return new(state, state.LastReply ?? "", "replay");
        var text = BanglaAppointmentParser.Normalize(input);
        if (text.Contains("in english")) state = state with { Bengali = false };
        if (text.Contains("বাংলায়") || text.Contains("বাংলায়")) state = state with { Bengali = true };
        AppointmentConversationReply Say(string bn, string en, string action = "prompt")
        {
            var reply = state.Bengali ? bn : en;
            state = state with { LastTurnId = turnId, LastReply = reply };
            return new(state, reply, action);
        }
        if (state.Stage == "done")
        {
            if (!Regex.IsMatch(text, "নতুন|another|new"))
                return Say("আগের কাজটি সম্পন্ন হয়েছে। নতুন বুকিং চাইলে নতুন অ্যাপয়েন্টমেন্ট বলুন।", "The previous request is complete. Say new appointment to start another.");
            state = new(tenantId, sessionId) { Bengali = state.Bengali };
        }
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var parsed = BanglaAppointmentParser.Parse(input, today);
        if (parsed.Error == "invalid_phone")
            return Say("ফোন নম্বরটি ঠিক বুঝিনি। সম্পূর্ণ ১১ সংখ্যার মোবাইল নম্বরটি আবার দিন।", "Please repeat the complete 11-digit mobile number.");
        if (parsed.Error is not null)
            return Say("তারিখ বা সময়টি সঠিকভাবে বুঝিনি। ভবিষ্যতের তারিখ এবং সকাল বা বিকালসহ সময় বলুন।", "Please give a valid future date and a time with AM or PM.");
        var intent = BanglaAppointmentParser.Intent(input);
        if (state.Stage == "intent")
        {
            if (intent is null) return Say("আপনি কি অ্যাপয়েন্টমেন্ট বুক, বাতিল, নাকি সময় পরিবর্তন করতে চান?", "Would you like to book, cancel or reschedule an appointment?");
            state = state with { Operation = intent, Stage = intent is "cancel" or "reschedule" ? "reference" : "date" };
        }
        if (intent == "cancel" && state.Operation == "book")
        {
            state = state with { Stage = "done" };
            return Say("চলমান বুকিং অনুরোধ বন্ধ করেছি। আগে করা বুকিং পরিবর্তন হয়নি।", "The current booking request is closed. Existing bookings are unchanged.", "aborted");
        }
        // Do not interpret 'no' as cancellation of an existing DB booking.
        if (BanglaAppointmentParser.No(input))
        {
            state = state with { Stage = "correction", SlotId = null, SlotUtc = null };
            return Say("কোন তথ্যটি পরিবর্তন করবেন? তারিখ, সময়, নাম বা ফোন নম্বরটি আবার বলুন।", "What should I change? Please repeat the date, time, name or phone.");
        }
        if (Regex.IsMatch(text, "অনুরোধ বাদ|stop request|abort request"))
        {
            state = state with { Stage = "done" };
            return Say("এই অনুরোধ বন্ধ করেছি। আগে করা কোনো বুকিং পরিবর্তন হয়নি।", "This request is closed. Existing bookings are unchanged.", "aborted");
        }
        var before = state;
        if (parsed.Date is not null) state = state with { Date = parsed.Date };
        if (parsed.Time is not null) state = state with { Time = parsed.Time, AmbiguousTime = parsed.AmbiguousTime };
        if (parsed.Phone is not null) state = state with { Phone = parsed.Phone };
        if (parsed.Name is not null) state = state with { Name = parsed.Name };
        if (parsed.Reference is not null) state = state with { Reference = parsed.Reference };
        if (before.Stage == "name" && parsed.Name is null && parsed.Phone is null && parsed.Date is null && parsed.Time is null
            && intent is null && !BanglaAppointmentParser.Yes(input) && ValidName(input))
            state = state with { Name = input.Trim() };
        bool changed = before.Date != state.Date || before.Time != state.Time || before.Name != state.Name
            || before.Phone != state.Phone || before.Reference != state.Reference || before.AmbiguousTime != state.AmbiguousTime;
        if (changed) state = state with { SlotId = null, SlotUtc = null, Stage = "details" };

        if (state.Operation is "cancel" or "reschedule")
        {
            if (state.Reference is null) { state = state with { Stage = "reference" }; return Say("আপনার বুকিং রেফারেন্সটি দিন।", "Please provide your booking reference."); }
            if (state.Phone is null) { state = state with { Stage = "phone" }; return Say("বুকিংয়ে দেওয়া সম্পূর্ণ ফোন নম্বরটি দিন।", "Please provide the full phone number used for the booking."); }
            var owned = await appointments.FindForVoiceAsync(tenantId, state.Reference, state.Phone, ct);
            if (owned is null) return Say("রেফারেন্স ও ফোন নম্বরের সঙ্গে বুকিং মেলেনি। তথ্য সংশোধন করুন।", "The reference and phone did not match a booking. Please correct them.", "not_found");
            state = state with { BookingId = owned.BookingId, Name = owned.CustomerName };
            if (state.Operation == "cancel")
            {
                if (before.Stage != "confirm" || changed || !BanglaAppointmentParser.Yes(input))
                {
                    state = state with { Stage = "confirm" };
                    return Say($"{state.Reference} বুকিংটি বাতিল করব? নিশ্চিত হলে হ্যাঁ বলুন।", $"Cancel booking {state.Reference}? Say yes to confirm.");
                }
                try
                {
                    await appointments.CancelForVoiceAsync(tenantId, state.BookingId.Value, state.Phone, ct);
                    state = state with { Stage = "done" };
                    return Say("বুকিংটি বাতিল হয়েছে।", "The booking has been cancelled.", "cancelled");
                }
                catch (InvalidOperationException) { return Say("বুকিংটি এখন বাতিল করা যায়নি। কোনো সফলতা নিশ্চিত করছি না।", "The booking could not be cancelled. Cancellation is not confirmed.", "conflict"); }
            }
        }
        if (state.Date is null) { state = state with { Stage = "date" }; return Say("কোন তারিখে অ্যাপয়েন্টমেন্ট চান?", "What date would you like?"); }
        if (state.Time is null && state.Operation != "query") { state = state with { Stage = "time" }; return Say("কোন সময় চান? সকাল বা বিকালসহ বলুন।", "What time? Please include AM or PM."); }
        if (state.AmbiguousTime && state.Operation != "query")
        { state = state with { Stage = "time" }; return Say("সময়টি সকাল না বিকাল? পুরো সময়টি আবার বলুন।", "Is that AM or PM? Please repeat the complete time."); }
        if (state.SlotId is null)
        {
            // Service accepts UTC dates. Query all UTC dates intersecting the local day.
            var localStart = state.Date.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            var utcStart = TimeZoneInfo.ConvertTimeToUtc(localStart, zone);
            var utcEnd = TimeZoneInfo.ConvertTimeToUtc(localStart.AddDays(1), zone);
            var slots = new List<AvailableAppointmentSlot>();
            for (var date = DateOnly.FromDateTime(utcStart); date <= DateOnly.FromDateTime(utcEnd.AddTicks(-1)); date = date.AddDays(1))
                slots.AddRange(await appointments.GetAvailabilityAsync(tenantId, date, ct));
            slots = slots.Where(s => s.StartsAtUtc >= utcStart && s.StartsAtUtc < utcEnd && s.StartsAtUtc > now.UtcDateTime).DistinctBy(s => s.SlotId).ToList();
            if (state.Operation == "query")
            {
                state = state with { Stage = "done" };
                var times = string.Join(", ", slots.Take(5).Select(s => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(s.StartsAtUtc, DateTimeKind.Utc), zone).ToString("HH:mm")));
                return Say(times.Length == 0 ? "ওই তারিখে কোনো সময় খালি নেই।" : $"খালি সময়: {times}।",
                    times.Length == 0 ? "There are no available times on that date." : $"Available times: {times}.", "availability");
            }
            var local = state.Date.Value.ToDateTime(state.Time!.Value, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
                return Say("এই সময়টি timezone পরিবর্তনের কারণে অস্পষ্ট। অন্য সময় দিন।", "That local time is ambiguous or invalid due to a clock change. Choose another time.");
            var requested = TimeZoneInfo.ConvertTimeToUtc(local, zone);
            var exact = slots.Where(s => s.StartsAtUtc == requested).ToList();
            if (exact.Count != 1)
            {
                state = state with { Stage = "time" };
                return Say(exact.Count == 0 ? "ওই সময়টি খালি নেই। অন্য সময় বা তারিখ বলুন।" : "একই সময়ে একাধিক provider আছে। প্রতিনিধি দিয়ে provider নির্বাচন করুন।",
                    exact.Count == 0 ? "That time is unavailable. Choose another date or time." : "Multiple providers match. Please ask a representative to choose the provider.", "unavailable");
            }
            state = state with { SlotId = exact[0].SlotId, SlotUtc = exact[0].StartsAtUtc };
        }
        if (state.Name is null) { state = state with { Stage = "name" }; return Say("কার নামে বুকিং করব?", "What name should I use?"); }
        if (!ValidName(state.Name)) { state = state with { Name = null, Stage = "name" }; return Say("নামটি আবার বলুন।", "Please repeat the name."); }
        if (state.Phone is null) { state = state with { Stage = "phone" }; return Say("সম্পূর্ণ ১১ সংখ্যার মোবাইল নম্বরটি দিন।", "Please provide the full 11-digit mobile number."); }
        if (before.Stage != "confirm" || changed || !BanglaAppointmentParser.Yes(input))
        {
            state = state with { Stage = "confirm" };
            var details = $"{state.Date:yyyy-MM-dd} {state.Time:HH:mm}, {state.Name}, {state.Phone}";
            return Say($"তথ্যগুলো: {details}। {(state.Operation == "reschedule" ? "সময় পরিবর্তন" : "বুকিং")} নিশ্চিত করব? হ্যাঁ বলুন।", $"Details: {details}. Confirm {state.Operation}? Say yes.");
        }
        try
        {
            var booked = state.Operation == "reschedule"
                ? await appointments.RescheduleForVoiceAsync(tenantId, state.BookingId!.Value, state.Phone, state.SlotId!.Value, ct)
                : await appointments.BookAsync(tenantId, new(state.SlotId!.Value, state.Name, state.Phone, null), ct);
            state = state with { Stage = "done", Reference = booked.BookingReference, BookingId = booked.BookingId };
            return Say($"আপনার বুকিং নিশ্চিত হয়েছে। রেফারেন্স {booked.BookingReference}।", $"Your booking is confirmed. Reference {booked.BookingReference}.", "confirmed");
        }
        catch (InvalidOperationException)
        {
            state = state with { Stage = "time", SlotId = null, SlotUtc = null };
            return Say("দুঃখিত, বুকিং সম্পন্ন হয়নি। সময়টি অন্য কেউ নিয়ে থাকতে পারেন। অন্য সময় দিন।", "The booking did not complete. The slot may have been taken. Choose another time.", "conflict");
        }
    }
    private static bool ValidName(string value) => value.Trim().Length is >= 2 and <= 60
        && Regex.IsMatch(value, @"^[\p{L}\p{M} .'-]+$");
}
