using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Services.AgentMemory;
using AlgoTrading.Api.Services.AiTelegram;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Amazon.Runtime;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// "Disha": a linked owner's voice note to the Telegram assistant is decoded, written down by a scripted Bedrock
/// audio model, answered as typed text, and spoken back by a scripted Amazon Polly. Nothing here reaches AWS or
/// Telegram: the voice notes are made in the test with Concentus's own encoder.
/// </summary>
public sealed class AiVoiceTests
{
    private const long Owner = 5550001;
    private const long Stranger = 5550002;
    private const string Voxtral = "mistral.voxtral-small-24b-2507";
    private static readonly byte[] Mp3 = [0xFF, 0xFB, 0x90, 0x64, 1, 2, 3, 4, 5, 6, 7, 8];
    private readonly string _dbName = Guid.NewGuid().ToString("N");

    // ---------- the voice note itself ----------

    [Fact]
    public void A_voice_note_becomes_16_khz_mono_16_bit_wav_with_its_sound()
    {
        // 48 kHz at 24 kb/s, as a phone records one: Opus's hybrid mode, which Concentus decoded to silence when asked
        // for 16 kHz directly.
        byte[] ogg = OggOpus(seconds: 2, sampleRate: 48_000, channels: 1);

        byte[] wav = OggOpusDecoder.ToWav(ogg, maxSeconds: 120);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(((short)1, (short)1, 16_000, (short)16), (
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)),
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)),
            BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)),
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34))));
        Assert.Equal(wav.Length - 44, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.InRange(OggOpusDecoder.Seconds(wav), 1.95, 2.1);
        Assert.InRange(Tone440(wav), 6000, 9000);
    }

    [Theory]
    [InlineData(48_000, 2)]
    [InlineData(16_000, 1)]
    [InlineData(8_000, 1)]
    public void A_note_recorded_at_any_rate_or_in_stereo_is_still_16_khz_mono(int sampleRate, int channels)
    {
        byte[] wav = OggOpusDecoder.ToWav(OggOpus(seconds: 1, sampleRate, channels), maxSeconds: 120);

        Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
        Assert.InRange(OggOpusDecoder.Seconds(wav), 0.95, 1.1);
        Assert.InRange(Tone440(wav), 6000, 9000);
    }

    [Fact]
    public void Audio_that_is_not_ogg_opus_or_too_long_is_refused_in_words()
    {
        var notOgg = Assert.Throws<InvalidDataException>(() => OggOpusDecoder.ToWav(Encoding.ASCII.GetBytes("ID3 this is an mp3, honest"), 120));
        var tooLong = Assert.Throws<InvalidDataException>(() => OggOpusDecoder.ToWav(OggOpus(seconds: 3, sampleRate: 48_000, channels: 1), maxSeconds: 2));

        Assert.StartsWith("not an Ogg/Opus file", notOgg.Message);
        Assert.Equal("longer than 2 s", tooLong.Message);
    }

    // ---------- the words around it ----------

    [Fact]
    public void Strategy_names_are_given_as_they_are_said_with_the_desks_words()
    {
        var names = VoiceText.SpokenNames(["GhostTangentCrossings", "Fulcrum", "Fulcrum2StraddleBuy20", "Fulcrum3StraddleBuy175", "SmcStructureBreak", "ChainFlowBuy"]);
        string instruction = VoiceText.Instruction(names, "Disha");

        Assert.Equal(new[] { "Ghost Tangent Crossings", "Fulcrum", "Fulcrum Straddle Buy", "Smc Structure Break", "Chain Flow Buy" }, names);
        Assert.StartsWith(VoiceText.BaseInstruction, instruction);
        Assert.EndsWith(
            "Names the speaker may say, spelled as the desk spells them; write one only if it is actually said: Ghost Tangent Crossings, Fulcrum, Fulcrum Straddle Buy, Smc Structure Break, Chain Flow Buy, NIFTY, BANKNIFTY, SENSEX, P&L, Disha.",
            instruction);
    }

    [Theory]
    [InlineData("Trader, why did you buy at 10:20?", "/trader why did you buy at 10:20?")]
    [InlineData("trader why did you buy?", "/trader why did you buy?")]
    [InlineData("Disha, trader: kyun kharida?", "/trader kyun kharida?")]
    [InlineData("Slash reviewer, run 339 kaisa tha?", "/reviewer run 339 kaisa tha?")]
    [InlineData("/news What moved?", "/news What moved?")]
    [InlineData("Trader.", "/trader")]
    [InlineData("Disha, aaj ka P&L kya hai?", "Disha, aaj ka P&L kya hai?")]
    [InlineData("Trader's book kya hai?", "Trader's book kya hai?")]
    [InlineData("Option chain mein max pain kya hai?", "Option chain mein max pain kya hai?")]
    [InlineData("/new positions kya hain?", "new positions kya hain?")]
    [InlineData("/unlink", "unlink")]
    [InlineData("जार्विस, आज का P&L?", "जार्विस, आज का P&L?")]
    public void A_transcript_is_asked_as_typed_with_an_agents_prefix_word_and_never_another_command(string heard, string asked)
    {
        Assert.Equal(asked, VoiceText.AsTyped(heard, TelegramAssistant.AgentCommands.Select(c => c.Command), "Disha"));
    }

    [Fact]
    public void An_answer_is_spoken_without_markdown_tables_links_or_tool_citations_and_numbers_as_written()
    {
        string spoken = VoiceText.Spoken("""
            ## Today
            The desk made **₹2,22,414** net (`get_runs`, 15:31 IST) across 23 runs.
            | Run | Net |
            |-----|-----|
            | 339 | -1,10,132.75 |
            - Ghost on NIFTY lost ₹1,10,132.75 → the worst run.
            - See [the run](https://openfno.com/runs/339) or https://openfno.com/x for more ✅
            """, 600);

        Assert.Equal(
            "Today. The desk made ₹2,22,414 net across 23 runs. Ghost on NIFTY lost ₹1,10,132.75 to the worst run. " +
            "See the run or for more. Full details are in the text above.",
            spoken);
    }

    [Fact]
    public void A_long_answer_is_cut_at_a_sentence_and_says_the_rest_is_above()
    {
        string answer = string.Join(' ', Enumerable.Range(1, 40).Select(i => $"Run {i} made ₹{i * 1000:N0} after charges."));

        string spoken = VoiceText.Spoken(answer, 200);

        Assert.True(spoken.Length <= 200, spoken);
        Assert.EndsWith("after charges. " + VoiceText.DetailsAbove, spoken);
        Assert.StartsWith("Run 1 made ₹1,000 after charges. Run 2 made", spoken);
    }

    [Fact]
    public void A_short_answer_without_a_table_is_spoken_whole()
    {
        Assert.Equal("Haan, aaj ka P&L ₹42,000 hai.", VoiceText.Spoken("Haan, aaj ka P&L **₹42,000** hai.", 600));
        Assert.Equal(200, TelegramVoice.MaxTokens(3));
        Assert.Equal(720, TelegramVoice.MaxTokens(119.6));
    }

    // ---------- the calls to AWS, scripted ----------

    [Fact]
    public async Task A_transcription_is_one_signed_request_with_the_audio_inline_as_wav()
    {
        var aws = new FakeAws();
        aws.Transcripts.Enqueue((200, Heard("Aaj ka P&L kya hai?")));
        var signer = new FakeSigner();
        var client = new BedrockOpenChatClient(new Factory(aws), new AiTestKit.Monitor(Settings()), signer);
        byte[] wav = OggOpusDecoder.ToWav(OggOpus(1, 48_000, 1), 120);

        var heard = await client.TranscribeAsync(Voxtral, wav, "wav", "Write it down.", 200, CancellationToken.None);

        Assert.Equal(("Aaj ka P&L kya hai?", "stop", 410, 12), (heard.Text, heard.FinishReason, heard.Usage!.PromptTokens, heard.Usage.CompletionTokens));
        var (host, path, body) = Assert.Single(aws.Requests);
        Assert.Equal(("bedrock-mantle.us-east-1.api.aws", "/v1/chat/completions"), (host, path));
        var json = JsonNode.Parse(body)!;
        Assert.Equal((Voxtral, 200, 0.0), (json["model"]!.GetValue<string>(), json["max_tokens"]!.GetValue<int>(), json["temperature"]!.GetValue<double>()));
        Assert.Null(json["stream"]);
        var content = json["messages"]![0]!["content"]!.AsArray();
        Assert.Equal(("input_audio", "wav"), (content[0]!["type"]!.GetValue<string>(), content[0]!["input_audio"]!["format"]!.GetValue<string>()));
        Assert.Equal(wav, Convert.FromBase64String(content[0]!["input_audio"]!["data"]!.GetValue<string>()));
        Assert.Equal(("text", "Write it down."), (content[1]!["type"]!.GetValue<string>(), content[1]!["text"]!.GetValue<string>()));
        var signed = Assert.Single(signer.Signed);
        Assert.Equal(("us-east-1", "bedrock-mantle"), (signed.Region, signed.Service));
    }

    [Fact]
    public async Task A_refused_transcription_reads_as_a_bedrock_refusal_with_the_account_masked()
    {
        var aws = new FakeAws();
        aws.Transcripts.Enqueue((403, """{"Message":"User: the desk's role in account 123456789012 is not authorized to perform: bedrock-mantle:CreateInference"}"""));
        var client = new BedrockOpenChatClient(new Factory(aws), new AiTestKit.Monitor(Settings()), new FakeSigner());

        var ex = await Assert.ThrowsAsync<AiAttemptFailedException>(() =>
            client.TranscribeAsync(Voxtral, [1, 2, 3], "mp3", "Write it down.", 200, CancellationToken.None));

        Assert.StartsWith("http 403: the server's AWS role is not allowed to call Bedrock", ex.Outcome);
        Assert.DoesNotContain("123456789012", ex.Outcome);
    }

    [Fact]
    public async Task Polly_is_asked_for_kajals_neural_mp3_in_mumbai_and_a_missing_permission_is_its_own_refusal()
    {
        var aws = new FakeAws();
        aws.Speech.Enqueue((200, Mp3));
        aws.Speech.Enqueue((403, Encoding.UTF8.GetBytes(PollyRefusal)));
        aws.Speech.Enqueue((500, Encoding.UTF8.GetBytes("""{"message":"InternalFailure"}""")));
        var signer = new FakeSigner();
        var polly = new PollySpeechClient(new Factory(aws), new AiTestKit.Monitor(Settings()), signer);

        byte[] audio = await polly.SynthesizeAsync("Aaj ka P&L ₹42,000 hai.", CancellationToken.None);
        await Assert.ThrowsAsync<PollyNotPermittedException>(() => polly.SynthesizeAsync("x", CancellationToken.None));
        var other = await Assert.ThrowsAsync<AiAttemptFailedException>(() => polly.SynthesizeAsync("x", CancellationToken.None));

        Assert.Equal(Mp3, audio);
        Assert.Equal(("polly.ap-south-1.amazonaws.com", "/v1/speech"), (aws.Requests[0].Host, aws.Requests[0].Path));
        Assert.Equal(("mp3", "Aaj ka P&L ₹42,000 hai.", "Kajal", "neural", "en-IN"), PollyBody(aws.Requests[0].Body));
        Assert.Equal(("ap-south-1", "polly"), (signer.Signed[0].Region, signer.Signed[0].Service));
        Assert.Equal(("http 500: InternalFailure", 500), (other.Outcome, other.HttpStatus));
        Assert.Equal("Amazon Polly", AiCatalog.ProviderOf(PollySpeechClient.ModelOf(Settings().Voice)));
        Assert.Equal(0.0096m, PollySpeechClient.Cost(600, Settings().Voice));
    }

    [Fact]
    public async Task The_sigv4_signer_signs_for_polly_in_its_region_with_made_up_credentials_and_sends_nothing()
    {
        var signer = new AwsSigV4BedrockSigner(_ => Task.FromResult<AWSCredentials>(new SessionAWSCredentials("AKIDTESTONLY", "not-a-secret", "not-a-token")));
        byte[] body = Encoding.UTF8.GetBytes("""{"Text":"x"}""");
        using var message = new HttpRequestMessage(HttpMethod.Post, AiCatalog.PollyEndpoint("ap-south-1"));

        await signer.SignAsync(message, body, "ap-south-1", PollySpeechClient.Service, CancellationToken.None);

        string auth = message.Headers.GetValues("Authorization").Single();
        Assert.Contains("/ap-south-1/polly/aws4_request", auth);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(body)), message.Headers.GetValues("X-Amz-Content-Sha256").Single());
    }

    // ---------- on Telegram ----------

    [Fact]
    public async Task The_owners_voice_note_is_heard_answered_as_typed_and_spoken_back()
    {
        var (ai, aws, bot, signer, assistant, _) = Setup();
        await Link(assistant);
        bot.Sent.Clear();
        bot.File = OggOpus(seconds: 2, sampleRate: 48_000, channels: 1);
        aws.Transcripts.Enqueue((200, Heard("Disha, aaj ka P&L kya hai?")));
        aws.Speech.Enqueue((200, Mp3));
        ai.Provider.On(Judge1, Answer("""
            Aaj desk ne **₹2,22,414** net kamaye (`get_runs`, 15:31 IST).
            | Run | Net |
            |-----|-----|
            | 339 | -1,10,132.75 |
            """));

        await assistant.HandleAsync(Voice(Owner, seconds: 2), CancellationToken.None);

        // Downloaded through getFile and the file's URL.
        Assert.Equal("?file_id=voice-file-1", Assert.Single(bot.FileRequests));
        Assert.Equal("/file/bot123:TEST/voice/file_7.oga", Assert.Single(bot.Downloads));
        // Written down from 16 kHz WAV, with the desk's words in the instruction.
        var transcription = JsonNode.Parse(aws.Requests[0].Body)!;
        var parts = transcription["messages"]![0]!["content"]!.AsArray();
        byte[] sent = Convert.FromBase64String(parts[0]!["input_audio"]!["data"]!.GetValue<string>());
        Assert.Equal(("RIFF", 16_000), (Encoding.ASCII.GetString(sent, 0, 4), BinaryPrimitives.ReadInt32LittleEndian(sent.AsSpan(24))));
        Assert.EndsWith("NIFTY, BANKNIFTY, SENSEX, P&L, Disha.", parts[1]!["text"]!.GetValue<string>());
        Assert.Equal(200, transcription["max_tokens"]!.GetValue<int>());
        // What was heard, then the answer as typed text gets it, then the voice.
        Assert.Equal("🎙 \"Disha, aaj ka P&L kya hai?\"", bot.Texts[0]);
        Assert.Contains("<b>₹2,22,414</b>", bot.Texts[1]);
        Assert.Contains("fb:-1:", bot.Sent.Last(s => s["method"] == "sendMessage")["reply_markup"]);
        var spoken = Assert.Single(bot.Voices);
        Assert.Equal((Owner, "reply.mp3", "audio/mpeg"), (spoken.ChatId, spoken.FileName, spoken.ContentType));
        Assert.Equal(Mp3, spoken.Audio);
        Assert.Equal(("mp3", "Aaj desk ne ₹2,22,414 net kamaye. Full details are in the text above.", "Kajal", "neural", "en-IN"),
            PollyBody(aws.Requests[1].Body));
        Assert.Equal(new[] { "bedrock-mantle", "polly" }, signer.Signed.Select(s => s.Service));

        await using var db = NewDb(_dbName);
        var calls = await db.AiCalls.OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(new[] { AiCatalog.VoiceTranscribe, AiCatalog.DeskAssistant, AiCatalog.VoiceSpeak }, calls.Select(c => c.AgentKey));
        Assert.All(calls, c => Assert.Equal(("telegram", "upendra", $"tg-{Owner}"), (c.Source, c.RequestedBy, c.ConversationId)));
        var heard = calls[0];
        Assert.Equal((AiCallOutcome.Ok, "bedrock-oss:" + Voxtral, 410, 12, 422), (heard.Outcome, heard.Model, heard.PromptTokens, heard.CompletionTokens, heard.TotalTokens));
        Assert.Equal(("Disha, aaj ka P&L kya hai?", "Disha, aaj ka P&L kya hai?"), (heard.Summary, heard.Answer));
        // The audio is not kept: only the instruction and a line describing it.
        Assert.Contains("the audio is not kept", heard.MessagesJson);
        Assert.DoesNotContain("UklGR", heard.MessagesJson);
        Assert.True(heard.MessagesJson.Length < 2000);
        // The question is the transcript, asked as typed text is, with the one voice line after the prompt.
        Assert.Equal("Disha, aaj ka P&L kya hai?", calls[1].Summary);
        Assert.EndsWith(VoiceText.PromptNote("Disha"), calls[1].SystemPrompt);
        Assert.StartsWith(AiCatalog.Agent(AiCatalog.DeskAssistant)!.SystemPrompt, calls[1].SystemPrompt);
        var said = calls[2];
        Assert.Equal((AiCallOutcome.Ok, "polly:Kajal/neural"), (said.Outcome, said.Model));
        Assert.Equal("Spoke 69 characters (Kajal, neural): $0.001104 at list price ($16 per 1M characters)", said.Summary);
    }

    [Fact]
    public async Task Without_the_polly_permission_answers_stay_text_and_the_owner_is_told_once()
    {
        var log = new ListLogger();
        var (ai, aws, bot, _, assistant, _) = Setup(log: log);
        await Link(assistant);
        bot.Sent.Clear();
        bot.File = OggOpus(seconds: 1, sampleRate: 48_000, channels: 1);
        aws.Transcripts.Enqueue((200, Heard("Aaj ka P&L?")));
        aws.Transcripts.Enqueue((200, Heard("Aur kal ka?")));
        aws.Speech.Enqueue((403, Encoding.UTF8.GetBytes(PollyRefusal)));
        ai.Provider.On(Judge1, Answer("₹42,000."), Answer("₹31,000."));

        await assistant.HandleAsync(Voice(Owner, seconds: 1), CancellationToken.None);
        await assistant.HandleAsync(Voice(Owner, seconds: 1), CancellationToken.None);

        Assert.Empty(bot.Voices);
        Assert.Single(bot.Texts, t => t == TelegramVoice.SpeechNotice);
        Assert.Contains("polly:SynthesizeSpeech", TelegramVoice.SpeechNotice);
        // Polly is not asked again for a while after the refusal.
        Assert.Single(aws.Requests, r => r.Host.StartsWith("polly.", StringComparison.Ordinal));
        Assert.Contains(bot.Texts, t => t.Contains("₹31,000."));
        await using var db = NewDb(_dbName);
        var said = await db.AiCalls.SingleAsync(c => c.AgentKey == AiCatalog.VoiceSpeak);
        Assert.Equal(AiCallOutcome.Refused, said.Outcome);
        Assert.Contains("polly:SynthesizeSpeech", said.Error);
        Assert.DoesNotContain("123456789012", said.Error + said.Summary + said.AttemptsJson);
        // Not a desk incident: nothing above Information.
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_strangers_voice_note_gets_nothing_and_is_not_downloaded()
    {
        var (_, aws, bot, _, assistant, _) = Setup();
        await Link(assistant);
        bot.Sent.Clear();

        await assistant.HandleAsync(Voice(Stranger, seconds: 2), CancellationToken.None);

        Assert.Empty(bot.Sent);
        Assert.Empty(bot.FileRequests);
        Assert.Empty(aws.Requests);
    }

    [Theory]
    [InlineData(121, null, "That voice note is 121 s long; I listen to 120 s at most.")]
    [InlineData(30, 3_000_000L, "That voice note is larger than 2 MB, the most I take.")]
    public async Task A_voice_note_past_the_limits_is_politely_refused_without_downloading_it(int seconds, long? size, string reply)
    {
        var (_, aws, bot, _, assistant, _) = Setup();
        await Link(assistant);
        bot.Sent.Clear();

        await assistant.HandleAsync(Voice(Owner, seconds, size), CancellationToken.None);

        Assert.StartsWith(reply, Assert.Single(bot.Texts));
        Assert.Empty(bot.FileRequests);
        Assert.Empty(aws.Requests);
    }

    [Fact]
    public async Task A_spoken_agent_prefix_asks_that_agent_like_the_typed_one()
    {
        var (ai, aws, bot, _, assistant, _) = Setup();
        await Link(assistant);
        await ai.Store.SetAgentEnabledAsync(AiCatalog.AiTrader, true, "upendra", null);
        bot.File = OggOpus(seconds: 1, sampleRate: 48_000, channels: 1);
        aws.Transcripts.Enqueue((200, Heard("Trader, why did you buy at 10:20?")));
        aws.Speech.Enqueue((200, Mp3));
        ai.Provider.On(Judge1, Answer("The 22650 CE broke out."));

        await assistant.HandleAsync(Voice(Owner, seconds: 1), CancellationToken.None);

        await using var db = NewDb(_dbName);
        var asked = await db.AiCalls.SingleAsync(c => c.AgentKey == AiCatalog.AiTrader);
        Assert.Equal("why did you buy at 10:20?", asked.Summary);
        Assert.Contains("— AI Trader ·", bot.Texts.Last());
        Assert.Single(bot.Voices);
    }

    [Fact]
    public async Task A_transcription_that_fails_says_why_and_asks_no_agent()
    {
        var (ai, aws, bot, _, assistant, _) = Setup();
        await Link(assistant);
        bot.Sent.Clear();
        bot.File = OggOpus(seconds: 1, sampleRate: 48_000, channels: 1);
        aws.Transcripts.Enqueue((400, """{"error":{"message":"Unsupported audio format"}}"""));

        await assistant.HandleAsync(Voice(Owner, seconds: 1), CancellationToken.None);

        await using var db = NewDb(_dbName);
        var call = await db.AiCalls.SingleAsync();
        Assert.Equal((AiCatalog.VoiceTranscribe, AiCallOutcome.Failed, "http 400: Unsupported audio format"), (call.AgentKey, call.Outcome, call.Error));
        Assert.Equal($"I could not make out that voice note: http 400: Unsupported audio format (call #{call.Id}). Please try again or type the question.", bot.Texts.Single());
        Assert.Empty(ai.Provider.Requests);
    }

    [Fact]
    public async Task Audio_that_cannot_be_read_asks_no_model()
    {
        var (_, aws, bot, _, assistant, _) = Setup();
        await Link(assistant);
        bot.Sent.Clear();
        bot.File = Encoding.ASCII.GetBytes("not a voice note at all");

        await assistant.HandleAsync(Voice(Owner, seconds: 1), CancellationToken.None);

        Assert.StartsWith("I could not read that voice note (not an Ogg/Opus file", bot.Texts.Single());
        Assert.Empty(aws.Requests);
    }

    [Fact]
    public async Task An_mp3_audio_file_goes_to_the_model_as_mp3_and_other_audio_is_refused()
    {
        var (_, aws, bot, _, assistant, _) = Setup();
        await Link(assistant);
        bot.Sent.Clear();
        bot.File = Mp3;
        aws.Transcripts.Enqueue((200, Heard("Hello.")));

        await assistant.HandleAsync(Audio(Owner, "audio/mpeg", "note.mp3"), CancellationToken.None);
        await assistant.HandleAsync(Audio(Owner, "audio/aac", "note.aac"), CancellationToken.None);

        var parts = JsonNode.Parse(aws.Requests[0].Body)!["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("mp3", parts[0]!["input_audio"]!["format"]!.GetValue<string>());
        Assert.Equal(Mp3, Convert.FromBase64String(parts[0]!["input_audio"]!["data"]!.GetValue<string>()));
        Assert.StartsWith("I can listen to voice notes, and to MP3, OGG or WAV audio.", bot.Texts.Last());
    }

    [Fact]
    public async Task Voice_switched_off_asks_for_the_question_typed()
    {
        var (_, aws, bot, _, assistant, monitor) = Setup();
        monitor.CurrentValue.Voice.Enabled = false;
        await Link(assistant);
        bot.Sent.Clear();

        await assistant.HandleAsync(Voice(Owner, seconds: 2), CancellationToken.None);

        Assert.Equal("Voice notes are switched off on this desk. Please type the question.", bot.Texts.Single());
        Assert.Empty(bot.FileRequests);
        Assert.Empty(aws.Requests);
    }

    // ---------- helpers ----------

    private const string PollyRefusal =
        """{"message":"User: the desk's role in account 123456789012 is not authorized to perform: polly:SynthesizeSpeech because no identity-based policy allows the polly:SynthesizeSpeech action"}""";

    /// <summary>A tone of <paramref name="seconds"/> as an OGG/Opus file, made with Concentus's encoder as a phone would.</summary>
    private static byte[] OggOpus(int seconds, int sampleRate, int channels)
    {
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        var encoder = OpusCodecFactory.CreateEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_VOIP, null);
        encoder.Bitrate = 24_000;
        using var file = new MemoryStream();
        var writer = new OpusOggWriteStream(encoder, file, new OpusTags(), sampleRate, channels, false);
        var pcm = new short[sampleRate * seconds * channels];
        for (int i = 0; i < sampleRate * seconds; i++)
        {
            short sample = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            for (int c = 0; c < channels; c++) pcm[i * channels + c] = sample;
        }

        writer.WriteSamples(pcm, 0, pcm.Length);
        writer.Finish();
        return file.ToArray();
    }

    /// <summary>Polly's request body, field by field; exactly these five fields.</summary>
    private static (string, string, string, string, string) PollyBody(string body)
    {
        var json = JsonNode.Parse(body)!.AsObject();
        Assert.Equal(new[] { "OutputFormat", "Text", "VoiceId", "Engine", "LanguageCode" }, json.Select(kv => kv.Key));
        string F(string name) => json[name]!.GetValue<string>();
        return (F("OutputFormat"), F("Text"), F("VoiceId"), F("Engine"), F("LanguageCode"));
    }

    /// <summary>The amplitude of the 440 Hz tone in a 16 kHz WAV (the fixtures' tone is 8,000): sound, not silence.</summary>
    private static double Tone440(byte[] wav)
    {
        int n = (wav.Length - 44) / 2;
        double re = 0, im = 0;
        for (int i = 0; i < n; i++)
        {
            short sample = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2));
            re += sample * Math.Cos(2 * Math.PI * 440 * i / 16_000.0);
            im += sample * Math.Sin(2 * Math.PI * 440 * i / 16_000.0);
        }

        return 2 * Math.Sqrt(re * re + im * im) / n;
    }

    /// <summary>Bedrock's OpenAI-compatible answer, not streamed, as Voxtral gave it.</summary>
    private static string Heard(string text) => new JsonObject
    {
        ["id"] = "chatcmpl-test",
        ["object"] = "chat.completion",
        ["model"] = Voxtral,
        ["choices"] = new JsonArray(new JsonObject
        {
            ["index"] = 0,
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text },
            ["finish_reason"] = "stop",
        }),
        ["usage"] = new JsonObject { ["prompt_tokens"] = 410, ["completion_tokens"] = 12, ["total_tokens"] = 422 },
    }.ToJsonString();

    private (Services Ai, FakeAws Aws, VoiceBot Bot, FakeSigner Signer, TelegramAssistant Assistant, AiTestKit.Monitor Options) Setup(ILogger<TelegramVoice>? log = null)
    {
        var ai = Build(null, NewDb(_dbName));
        var aws = new FakeAws();
        var bot = new VoiceBot();
        var signer = new FakeSigner();
        var services = new ServiceCollection();
        services.AddScoped(_ => NewDb(_dbName));
        services.AddScoped(_ => ai.Gateway);
        services.AddScoped(sp => new AiMemoryService(sp.GetRequiredService<TradingDbContext>(),
            new AiMemoryBook(sp.GetRequiredService<TradingDbContext>(), ai.Client, ai.Options), ai.Options));
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Telegram:BotToken"] = "123:TEST" }).Build();
        var voice = new TelegramVoice(scopes, new BedrockOpenChatClient(new Factory(aws), ai.Options, signer),
            new PollySpeechClient(new Factory(aws), ai.Options, signer), ai.Options, log ?? NullLogger<TelegramVoice>.Instance);
        _pairing = new TelegramPairing();
        var assistant = new TelegramAssistant(scopes, new Factory(bot), config, _pairing, ai.Options, NullLogger<TelegramAssistant>.Instance, voice: voice);
        return (ai, aws, bot, signer, assistant, ai.Options);
    }

    private TelegramPairing _pairing = new();

    private async Task Link(TelegramAssistant assistant) =>
        await assistant.HandleAsync(Message(Owner, $$"""{"text":"/pair {{_pairing.Create("upendra").Code}}"}"""), CancellationToken.None);

    private static JsonObject Voice(long user, int seconds, long? size = null) =>
        Message(user, $$$"""{"voice":{"duration":{{{seconds}}},"mime_type":"audio/ogg","file_id":"voice-file-1","file_unique_id":"u1","file_size":{{{size ?? 9000}}}}}""");

    private static JsonObject Audio(long user, string mime, string name) =>
        Message(user, $$$"""{"audio":{"duration":3,"mime_type":"{{{mime}}}","file_name":"{{{name}}}","file_id":"voice-file-1","file_unique_id":"u2","file_size":12}}""");

    /// <summary>A private message as Telegram sends it: parsed from JSON, so its numbers read as the poller's do.</summary>
    private static JsonObject Message(long user, string fields)
    {
        var message = JsonNode.Parse(fields)!.AsObject();
        message["chat"] = JsonNode.Parse($$"""{"id":{{user}},"type":"private"}""");
        message["from"] = JsonNode.Parse($$"""{"id":{{user}},"username":"{{(user == Owner ? "owner" : "someone")}}","first_name":"X"}""");
        return message;
    }

    /// <summary>Bedrock's OpenAI-compatible endpoint and Amazon Polly, scripted: every request is kept, nothing leaves the test.</summary>
    private sealed class FakeAws : HttpMessageHandler
    {
        public Queue<(int Status, string Body)> Transcripts { get; } = new();

        public Queue<(int Status, byte[] Body)> Speech { get; } = new();

        public List<(string Host, string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string host = request.RequestUri!.Host;
            Requests.Add((host, request.RequestUri.AbsolutePath, await request.Content!.ReadAsStringAsync(cancellationToken)));
            if (host.StartsWith("bedrock-mantle.", StringComparison.Ordinal))
            {
                var (status, json) = Transcripts.Count > 0 ? Transcripts.Dequeue() : (500, """{"message":"no script"}""");
                return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }

            if (host.StartsWith("polly.", StringComparison.Ordinal))
            {
                var (status, bytes) = Speech.Count > 0 ? Speech.Dequeue() : (500, Encoding.UTF8.GetBytes("""{"message":"no script"}"""));
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(status == 200 ? "audio/mpeg" : "application/json");
                return new HttpResponseMessage((HttpStatusCode)status) { Content = content };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// api.telegram.org for voice: getFile and the file's download, sendVoice's multipart form, and every JSON method
    /// recorded as <c>AiTelegramTests</c>' bot records them.
    /// </summary>
    private sealed class VoiceBot : HttpMessageHandler
    {
        private long _nextMessageId = 100;

        public byte[] File { get; set; } = [];

        public List<string> FileRequests { get; } = [];

        public List<string> Downloads { get; } = [];

        public List<Dictionary<string, string>> Sent { get; } = [];

        public List<(long ChatId, byte[] Audio, string FileName, string ContentType)> Voices { get; } = [];

        public List<string> Texts => Sent.Where(s => s["method"] == "sendMessage").Select(s => s["text"]).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/file/", StringComparison.Ordinal))
            {
                Downloads.Add(path);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File) };
            }

            string method = path[(path.LastIndexOf('/') + 1)..];
            if (method == "getFile")
            {
                FileRequests.Add(request.RequestUri.Query);
                return Ok($$"""{"file_id":"voice-file-1","file_unique_id":"u1","file_size":{{File.Length}},"file_path":"voice/file_7.oga"}""");
            }

            if (request.Content is MultipartFormDataContent form)
            {
                long chatId = 0;
                byte[] audio = [];
                string fileName = string.Empty, type = string.Empty;
                foreach (var part in form)
                {
                    string name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    if (name == "chat_id") chatId = long.Parse(await part.ReadAsStringAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
                    if (name == "voice")
                    {
                        audio = await part.ReadAsByteArrayAsync(cancellationToken);
                        fileName = part.Headers.ContentDisposition.FileName!.Trim('"');
                        type = part.Headers.ContentType!.MediaType!;
                    }
                }

                Voices.Add((chatId, audio, fileName, type));
                return Ok("""{"message_id":1}""");
            }

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            var entry = body.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty);
            entry["method"] = method;
            long id = Interlocked.Increment(ref _nextMessageId);
            Sent.Add(entry);
            return Ok($$"""{"message_id":{{id}}}""");
        }

        private static HttpResponseMessage Ok(string result) =>
            new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":" + result + "}", Encoding.UTF8, "application/json") };
    }

    /// <summary>Keeps every log entry, to show that nothing is logged as a warning.</summary>
    private sealed class ListLogger : ILogger<TelegramVoice>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
