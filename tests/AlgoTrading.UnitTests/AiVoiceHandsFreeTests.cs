using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services.AiVoice;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Hands-free on AI → Assistant: the browser sends each utterance it hears with <c>wake=1</c>, and the API says whether
/// it was said to the assistant. The matcher is pure; the endpoint runs on a scripted Bedrock, so nothing reaches AWS.
/// </summary>
public sealed class AiVoiceHandsFreeTests
{
    private readonly string _dbName = Guid.NewGuid().ToString("N");

    // ---------- the name: was it said to the assistant? ----------

    [Theory]
    [InlineData("Ira, aaj ka P&L kya hai?", "Aaj ka P&L kya hai?")]
    [InlineData("Hey Ira, what is today's P&L?", "What is today's P&L?")]
    [InlineData("ok hey Ira how are the runs", "How are the runs")]
    [InlineData("Arre suno Ira, Ghost ka kya haal hai?", "Ghost ka kya haal hai?")]
    [InlineData("Hello, Ira. Fulcrum band karo?", "Fulcrum band karo?")]
    [InlineData("IRA!!! P&L?", "P&L?")]
    [InlineData("Eera, P&L batao.", "P&L batao.")]
    [InlineData("Irah what's the P&L", "What's the P&L")]
    [InlineData("Aira, status?", "Status?")]
    [InlineData("Era, status?", "Status?")]
    [InlineData("Iraa — runs?", "Runs?")]
    [InlineData("I ra, P&L?", "P&L?")]
    [InlineData("Ira ji, P&L batao", "P&L batao")]
    [InlineData("इरा, आज का P&L क्या है?", "आज का P&L क्या है?")]
    [InlineData("हे इरा, P&L?", "P&L?")]
    [InlineData("अरे ईरा सुनो, Ghost?", "सुनो, Ghost?")]
    [InlineData("Ira.", "")]
    [InlineData("Hey Ira", "")]
    public void An_utterance_that_starts_with_the_name_is_addressed_and_asks_the_rest(string heard, string question) =>
        Assert.Equal(new VoiceWake(true, question), VoiceText.Wake(heard, "Ira"));

    [Theory]
    [InlineData("What is the P&L, Ira?")]
    [InlineData("Hey hey hey Ira, P&L?")]
    [InlineData("Please Ira tell me the P&L")]
    [InlineData("Are the runs live?")]
    [InlineData("Hira ne kaha tha")]
    [InlineData("Ara, P&L?")]
    [InlineData("Iran is in the news")]
    [InlineData("Error in the feed")]
    [InlineData("Haan bhai, chai peete hain")]
    [InlineData("")]
    [InlineData("   ...  ")]
    public void Anything_else_is_not_addressed(string heard) =>
        Assert.Equal(VoiceWake.NotAddressed, VoiceText.Wake(heard, "Ira"));

    [Theory]
    [InlineData("Ira")]
    [InlineData("ira")]
    [InlineData("IRA,")]
    [InlineData("Eera")]
    [InlineData("Irah")]
    [InlineData("Aira")]
    [InlineData("Ayra")]
    [InlineData("Era")]
    [InlineData("Iraa")]
    [InlineData("इरा")]
    [InlineData("ईरा")]
    [InlineData("एरा")]
    [InlineData("आइरा")]
    public void The_usual_spellings_and_mishearings_of_ira_sound_alike(string word) => Assert.Equal("ira", VoiceText.WakeKey(word));

    [Theory]
    [InlineData("Kavya, P&L?", "Kavya", true)]
    [InlineData("काव्या, P&L?", "Kavya", true)]
    [InlineData("Kaavya P&L?", "Kavya", true)]
    [InlineData("Ira, P&L?", "Kavya", false)]
    [InlineData("Jarvis, P&L?", "Jarvis", true)]
    [InlineData("Jarvish, P&L?", "Jarvis", true)]
    [InlineData("Jervis, P&L?", "Jarvis", true)]
    [InlineData("Service, P&L?", "Jarvis", false)]
    [InlineData("Mister Jarvis, P&L?", "Mister Jarvis", true)]
    [InlineData("Jarvis, P&L?", "Mister Jarvis", false)]
    public void Any_name_is_matched_the_same_way(string heard, string name, bool addressed) =>
        Assert.Equal(addressed, VoiceText.Wake(heard, name).Addressed);

    [Fact]
    public void An_alias_counts_as_the_name()
    {
        Assert.Equal(new VoiceWake(true, "P&L?"), VoiceText.Wake("Buddy, P&L?", "Ira", ["Buddy"]));
        Assert.True(VoiceText.Wake("Ira, P&L?", "Ira", ["Buddy"]).Addressed);
        Assert.False(VoiceText.Wake("Buddy, P&L?", "Ira").Addressed);
        Assert.False(VoiceText.Wake("Ira, P&L?", string.Empty).Addressed);
        Assert.True(VoiceText.Wake("Ira, P&L?", string.Empty, ["Ira"]).Addressed);
    }

    [Fact]
    public void Devanagari_is_written_in_roman_letters_as_hinglish_is_typed()
    {
        Assert.Equal("iraa", VoiceText.Romanize("इरा"));
        Assert.Equal("kavitaa", VoiceText.Romanize("कविता"));
        Assert.Equal("raam", VoiceText.Romanize("राम"));
        Assert.Equal("kaavyaa", VoiceText.Romanize("काव्या"));
        Assert.Equal("zaraa", VoiceText.Romanize("ज़रा"));
        Assert.Equal("P&L ok", VoiceText.Romanize("P&L ok"));
    }

    // ---------- the endpoint: wake=1 ----------

    [Fact]
    public async Task Hindi_from_transcribe_in_devanagari_is_addressed_by_the_name_as_written_in_it()
    {
        Setup(provider: AiVoiceSettings.AmazonTranscribe);
        _transcribe.Answers.Enqueue(TranscribeTestKit.Heard(("इरा, आज का P&L क्या है?", "hi-IN")));
        _transcribe.Answers.Enqueue(TranscribeTestKit.Heard(("चलो चाय पीते हैं", "hi-IN")));

        var addressed = await Transcribe(Recording(seconds: 2), "c-abc123");
        var ignored = await Transcribe(Recording(seconds: 2), "c-abc123");

        var heard = Assert.IsType<AiVoiceWakeTranscript>(Assert.IsType<OkObjectResult>(addressed).Value);
        Assert.Equal(("इरा, आज का P&L क्या है?", true, "आज का P&L क्या है?"), (heard.Text, heard.Addressed, heard.Question));
        Assert.False(Assert.IsType<AiVoiceWakeTranscript>(Assert.IsType<OkObjectResult>(ignored).Value).Addressed);
        await using var db = NewDb(_dbName);
        var rows = await db.AiCalls.OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(new[] { "इरा, आज का P&L क्या है?", AiVoiceService.NotAddressed + "चलो चाय पीते हैं" }, rows.Select(r => r.Summary));
        Assert.All(rows, r => Assert.Equal(AiCatalog.AmazonTranscribeModel, r.Model));
    }

    [Fact]
    public async Task An_addressed_utterance_returns_its_question_and_is_recorded_as_heard()
    {
        var (aws, _) = Setup();
        aws.Transcripts.Enqueue((200, Heard("Hey Ira, aaj ka P&L kya hai?")));

        var result = await Transcribe(Recording(seconds: 2), "c-abc123");

        var heard = Assert.IsType<AiVoiceWakeTranscript>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(("Hey Ira, aaj ka P&L kya hai?", true, "Aaj ka P&L kya hai?", 2.0), (heard.Text, heard.Addressed, heard.Question, heard.Seconds));
        await using var db = NewDb(_dbName);
        var call = await db.AiCalls.SingleAsync();
        Assert.Equal(heard.CallId, call.Id);
        Assert.Equal((AiCatalog.VoiceTranscribe, "console", "c-abc123", AiCallOutcome.Ok), (call.AgentKey, call.Source, call.ConversationId, call.Outcome));
        Assert.Equal("Hey Ira, aaj ka P&L kya hai?", call.Summary);
        Assert.Contains("[hands-free utterance, 2 s, 62.5 KB, sent as wav; the audio is not kept]", call.MessagesJson);
    }

    [Fact]
    public async Task An_utterance_not_said_to_the_assistant_is_recorded_as_not_addressed_without_its_audio()
    {
        var (aws, _) = Setup();
        aws.Transcripts.Enqueue((200, Heard("Haan bhai, chai peete hain.")));

        var result = await Transcribe(Recording(seconds: 1.5), "c-abc123");

        var heard = Assert.IsType<AiVoiceWakeTranscript>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(("Haan bhai, chai peete hain.", false, string.Empty), (heard.Text, heard.Addressed, heard.Question));
        await using var db = NewDb(_dbName);
        var call = await db.AiCalls.SingleAsync();
        Assert.Equal(("not addressed: Haan bhai, chai peete hain.", AiCallOutcome.Ok, "console"), (call.Summary, call.Outcome, call.Source));
        Assert.Equal("Haan bhai, chai peete hain.", call.Answer);
        Assert.DoesNotContain("UklGR", call.MessagesJson);
    }

    [Fact]
    public async Task The_name_given_with_slash_name_and_the_aliases_are_the_ones_listened_for()
    {
        var (aws, options) = Setup();
        await _voice.SetNameAsync("Kavya", "upendra", "test", CancellationToken.None);
        options.CurrentValue.Voice.NameAliases = ["Buddy"];
        aws.Transcripts.Enqueue((200, Heard("Ira, P&L?")));
        aws.Transcripts.Enqueue((200, Heard("काव्या, P&L?")));
        aws.Transcripts.Enqueue((200, Heard("Buddy, P&L?")));

        bool[] addressed = new bool[3];
        for (int i = 0; i < 3; i++)
        {
            var ok = Assert.IsType<OkObjectResult>(await Transcribe(Recording(seconds: 1)));
            addressed[i] = Assert.IsType<AiVoiceWakeTranscript>(ok.Value).Addressed;
        }

        Assert.Equal([false, true, true], addressed);
    }

    [Fact]
    public async Task Utterances_have_their_own_budget_a_minute_and_leave_the_question_limit_alone()
    {
        var (aws, options) = Setup();
        options.CurrentValue.Voice.HandsFreePerMinute = 2;
        options.CurrentValue.PerUserPer10Min = 1;
        for (int i = 0; i < 3; i++) aws.Transcripts.Enqueue((200, Heard("Haan bhai.")));

        Assert.IsType<OkObjectResult>(await Transcribe(Recording(seconds: 1)));
        _clock.Advance(TimeSpan.FromSeconds(20));
        Assert.IsType<OkObjectResult>(await Transcribe(Recording(seconds: 1)));
        var third = Console(Recording(seconds: 1));
        var refused = await third.Transcribe(_voice, null, CancellationToken.None, wake: "1");

        Assert.StartsWith("Hands-free has sent 2 utterances to be written down in the last minute", Error(refused, StatusCodes.Status429TooManyRequests));
        Assert.Equal("40", third.Response.Headers.RetryAfter.ToString());
        Assert.Equal(2, aws.Requests.Count);

        // The question limit (1 in ten minutes here) was not touched: a recording on the mic is still taken.
        aws.Transcripts.Enqueue((200, Heard("Ira, P&L?")));
        Assert.IsType<OkObjectResult>(await Console(Recording(seconds: 1)).Transcribe(_voice, null, CancellationToken.None));

        // A minute after the first, one more fits.
        _clock.Advance(TimeSpan.FromSeconds(41));
        Assert.IsType<OkObjectResult>(await Transcribe(Recording(seconds: 1)));
    }

    [Fact]
    public async Task Hands_free_switched_off_or_voice_switched_off_answers_503_and_calls_nothing()
    {
        var (aws, options) = Setup();
        options.CurrentValue.Voice.HandsFreePerMinute = 0;
        var off = await Transcribe(Recording(seconds: 1));
        options.CurrentValue.Voice.HandsFreePerMinute = 6;
        options.CurrentValue.Voice.Enabled = false;
        var voiceOff = await Transcribe(Recording(seconds: 1));

        Assert.StartsWith("Hands-free is switched off on this desk", Error(off, StatusCodes.Status503ServiceUnavailable));
        Assert.StartsWith("Voice is switched off on this desk", Error(voiceOff, StatusCodes.Status503ServiceUnavailable));
        Assert.Empty(aws.Requests);
    }

    [Fact]
    public async Task A_short_or_unreadable_utterance_is_refused_before_it_counts()
    {
        var (aws, options) = Setup();
        options.CurrentValue.Voice.HandsFreePerMinute = 1;

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<ObjectResult>(await Transcribe(Recording(seconds: 0.1))).StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<ObjectResult>(await Transcribe(Encoding.ASCII.GetBytes("RIFF....nope"))).StatusCode);
        aws.Transcripts.Enqueue((200, Heard("Ira, P&L?")));
        Assert.IsType<OkObjectResult>(await Transcribe(Recording(seconds: 1)));
    }

    [Fact]
    public async Task An_utterance_the_model_cannot_make_out_says_why_with_its_call()
    {
        var (aws, _) = Setup();
        aws.Transcripts.Enqueue((400, """{"error":{"message":"Unsupported audio format"}}"""));

        var result = await Transcribe(Recording(seconds: 1));

        await using var db = NewDb(_dbName);
        var call = await db.AiCalls.SingleAsync();
        Assert.Equal((AiCallOutcome.Failed, "Hands-free utterance of 1 s: not written down"), (call.Outcome, call.Summary));
        Assert.Equal($"The audio model could not make out what was said: http 400: Unsupported audio format (call #{call.Id}).", Error(result, StatusCodes.Status502BadGateway));
    }

    [Fact]
    public async Task The_page_is_told_the_name_the_switches_and_the_budget()
    {
        var (_, options) = Setup();
        var before = Assert.IsType<AiVoiceInfo>(Assert.IsType<OkObjectResult>(await Console().GetVoice(_voice, CancellationToken.None)).Value);
        await _voice.SetNameAsync("Kavya", "upendra", "test", CancellationToken.None);
        options.CurrentValue.Voice.SpeakReplies = false;
        options.CurrentValue.Voice.HandsFreePerMinute = 4;

        var after = Assert.IsType<AiVoiceInfo>(Assert.IsType<OkObjectResult>(await Console().GetVoice(_voice, CancellationToken.None)).Value);

        Assert.Equal(new AiVoiceInfo("Ira", true, true, 6), before);
        Assert.Equal(new AiVoiceInfo("Kavya", true, false, 4), after);
    }

    // ---------- helpers ----------

    private Services _ai = null!;
    private AiVoiceService _voice = null!;
    private readonly Clock _clock = new(new DateTime(2026, 10, 12, 4, 0, 0, DateTimeKind.Utc));

    /// <param name="provider">Voxtral unless a test says otherwise: these tests were written for its scripted answers.</param>
    private (FakeMantle Aws, AiTestKit.Monitor Options) Setup(string provider = AiVoiceSettings.Voxtral)
    {
        _ai = Build(null, NewDb(_dbName));
        _ai.Options.CurrentValue.Voice.TranscribeProvider = provider;
        var aws = new FakeMantle();
        var services = new ServiceCollection();
        services.AddScoped(_ => NewDb(_dbName));
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        _transcribe = new TranscribeTestKit.ScriptedTranscribe();
        _voice = new AiVoiceService(scopes, new BedrockOpenChatClient(new Factory(aws), _ai.Options, new FakeSigner()),
            TranscribeTestKit.Client(_ai.Options, _transcribe), new PollySpeechClient(new Factory(aws), _ai.Options, new FakeSigner()), _ai.Options,
            NullLogger<AiVoiceService>.Instance, _clock);
        return (aws, _ai.Options);
    }

    private TranscribeTestKit.ScriptedTranscribe _transcribe = null!;

    /// <summary>The console's controller, signed in as upendra (user 1), with <paramref name="body"/> as the request's WAV.</summary>
    private AiController Console(byte[]? body = null)
    {
        var controller = _ai.Controller();
        if (body is not null)
        {
            controller.Request.Body = new MemoryStream(body);
            controller.Request.ContentType = "audio/wav";
        }

        return controller;
    }

    private Task<IActionResult> Transcribe(byte[] wav, string? conversationId = null) =>
        Console(wav).Transcribe(_voice, conversationId, CancellationToken.None, wake: "1");

    /// <summary>A 440 Hz tone of <paramref name="seconds"/> as the console records it: 16 kHz mono 16-bit WAV.</summary>
    private static byte[] Recording(double seconds)
    {
        var samples = new short[(int)(16_000 * seconds)];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 16_000.0));
        return OggOpusDecoder.Wav(samples, 16_000);
    }

    private static string Error(IActionResult result, int status)
    {
        var refused = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, refused.StatusCode);
        return JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(refused.Value))!["error"]!.GetValue<string>();
    }

    /// <summary>Bedrock's OpenAI-compatible answer, not streamed, as Voxtral gives it.</summary>
    private static string Heard(string text) => new JsonObject
    {
        ["id"] = "chatcmpl-test",
        ["object"] = "chat.completion",
        ["model"] = "mistral.voxtral-small-24b-2507",
        ["choices"] = new JsonArray(new JsonObject
        {
            ["index"] = 0,
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text },
            ["finish_reason"] = "stop",
        }),
        ["usage"] = new JsonObject { ["prompt_tokens"] = 410, ["completion_tokens"] = 12, ["total_tokens"] = 422 },
    }.ToJsonString();

    /// <summary>Bedrock's OpenAI-compatible endpoint, scripted: every request is kept, nothing leaves the test.</summary>
    private sealed class FakeMantle : HttpMessageHandler
    {
        public Queue<(int Status, string Body)> Transcripts { get; } = new();

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.Host);
            var (status, json) = request.RequestUri.Host.StartsWith("bedrock-mantle.", StringComparison.Ordinal) && Transcripts.Count > 0
                ? Transcripts.Dequeue()
                : (500, """{"message":"no script"}""");
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Clock(DateTime utc) : TimeProvider
    {
        private DateTime _now = utc;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => new(_now, TimeSpan.Zero);
    }
}
