using CCaaS.Domain.Scheduling.Booking;
using Xunit;

namespace CCaaS.Tests.Unit.Scheduling;

// Task 2.3 - recognising the six conversational moves that gate irreversible actions.
//
// Getting one of these wrong is expensive in a way that a mis-parsed date is not: a false
// Affirmative books an appointment nobody agreed to, a missed WantsHuman traps a caller with a
// robot, and a false WantsToEnd hangs up on a customer mid-sentence. Hence keyword rules with
// a fixed precedence rather than a classifier - wrong in a known way beats wrong in an unknown
// one, and it costs no latency on the turn-taking path.
public class CallerIntentTests
{
    [Theory]
    [InlineData("yes")]
    [InlineData("yeah that's right")]
    [InlineData("that's correct")]
    [InlineData("sounds good")]
    [InlineData("go ahead")]
    [InlineData("perfect, book it")]
    public void Affirmative_IsRecognised(string utterance)
        => Assert.Equal(CallerMove.Affirmative, CallerIntent.Detect(utterance));

    [Theory]
    [InlineData("no")]
    [InlineData("nope")]
    [InlineData("no that's wrong")]
    [InlineData("that doesn't work for me")]
    [InlineData("can I have a different time")]
    public void Negative_IsRecognised(string utterance)
        => Assert.Equal(CallerMove.Negative, CallerIntent.Detect(utterance));

    [Theory]
    [InlineData("can I talk to a real person please")]
    [InlineData("put me through to an agent")]
    [InlineData("is there a human there")]
    [InlineData("transfer me to customer service")]
    public void WantsHuman_IsRecognised(string utterance)
        => Assert.Equal(CallerMove.WantsHuman, CallerIntent.Detect(utterance));

    [Fact]
    public void AskingForAPersonOutranksTheWordNo()
    {
        // "No, I want to speak to someone else" is a request for a person. Reading it as a
        // refusal leaves the caller arguing with a robot, which is how complaints start.
        Assert.Equal(CallerMove.WantsHuman, CallerIntent.Detect("no I want to speak to someone else"));
    }

    [Theory]
    [InlineData("not interested")]
    [InlineData("no thanks")]
    [InlineData("call me back later")]
    [InlineData("stop calling me")]
    [InlineData("take me off your list")]
    public void WantsToEnd_IsRecognised(string utterance)
        => Assert.Equal(CallerMove.WantsToEnd, CallerIntent.Detect(utterance));

    [Theory]
    [InlineData("I'm not sure")]
    [InlineData("I don't know")]
    [InlineData("whatever suits you")]
    [InlineData("you decide")]
    public void Unsure_IsNotTheSameAsNo(string utterance)
    {
        // "I'm not sure" contains "not". Reading it as a refusal answers a question the caller
        // has not actually answered.
        Assert.Equal(CallerMove.Unsure, CallerIntent.Detect(utterance));
    }

    [Fact]
    public void WhenBothYesAndNoAppear_TheNoWins()
    {
        // Callers correct themselves mid-sentence. Acting on a false yes books the wrong thing;
        // acting on a false no only costs one more question.
        Assert.Equal(CallerMove.Negative, CallerIntent.Detect("yes no sorry not that one"));
    }

    [Theory]
    [InlineData("tomorrow at four")]
    [InlineData("is there anything on tuesday")]   // a question with a date, not a shrug
    [InlineData("")]
    [InlineData(null)]
    public void UtterancesThatCarryNoMove_ReturnNone(string? utterance)
        => Assert.Equal(CallerMove.None, CallerIntent.Detect(utterance));

    // ============================================================== names

    [Theory]
    [InlineData("my name is Rahim Uddin", "Rahim Uddin")]
    [InlineData("this is Mr Hasan", "Hasan")]          // courtesy title stripped
    [InlineData("Karim", "Karim")]
    [InlineData("rahim uddin", "Rahim Uddin")]         // transcripts arrive lower case
    public void ExtractName_TakesTheConfidentCases(string utterance, string expected)
        => Assert.Equal(expected, CallerIntent.ExtractName(utterance));

    [Theory]
    [InlineData("I am free on Tuesday afternoon")]
    [InlineData("im free tomorrow")]
    [InlineData("well I would like the appointment under the company account")]
    [InlineData("")]
    [InlineData(null)]
    public void ExtractName_RefusesSentences(string? utterance)
    {
        // A wrong name is read back, printed on the voucher and texted to the customer. A
        // missing one costs one more question. The extractor is biased accordingly.
        Assert.Null(CallerIntent.ExtractName(utterance));
    }
}
