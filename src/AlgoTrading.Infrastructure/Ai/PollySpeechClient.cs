using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>
/// Amazon Polly's SynthesizeSpeech (<c>POST polly.{region}.amazonaws.com/v1/speech</c>): text in, MP3 out, for the
/// assistant's spoken replies, on Telegram and the console. Signed with SigV4 (service <c>polly</c>) by the same signer and the same AWS
/// role as the open models on Bedrock (<see cref="IBedrockRequestSigner"/>); no key anywhere in the desk's files.
/// </summary>
/// <remarks>
/// A role without <c>polly:SynthesizeSpeech</c> is refused with a 403 that IAM words "not authorized to perform". That
/// one throws <see cref="PollyNotPermittedException"/>, so the caller can answer in text and say once what is missing;
/// every other failure is an <see cref="AiAttemptFailedException"/>.
/// </remarks>
public sealed class PollySpeechClient(IHttpClientFactory httpClientFactory, IOptionsMonitor<AiSettings> settings, IBedrockRequestSigner signer)
{
    public const string HttpClientName = "ai-polly";

    /// <summary>The service name a signature is scoped to.</summary>
    public const string Service = "polly";

    /// <summary>The one permission the server's role needs for spoken replies.</summary>
    public const string Permission = "polly:SynthesizeSpeech";

    /// <summary>The longest a reply may take to synthesize: a few hundred characters take well under a second.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The voice, engine and language the settings name, as a call's model: <c>polly:Kajal/neural</c>.</summary>
    public static string ModelOf(AiVoiceSettings voice) => $"{AiCatalog.PollyPrefix}{voice.PollyVoice}/{voice.PollyEngine}";

    /// <summary>
    /// <paramref name="text"/> spoken, as MP3 bytes. Throws <see cref="PollyNotPermittedException"/> when the role may not
    /// call Polly, <see cref="AiAttemptFailedException"/> on any other failure.
    /// </summary>
    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken)
    {
        var voice = settings.CurrentValue.Voice;
        byte[] body = Encoding.UTF8.GetBytes(RequestBody(text, voice));
        using var message = new HttpRequestMessage(HttpMethod.Post, AiCatalog.PollyEndpoint(voice.PollyRegion)) { Content = new ByteArrayContent(body) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var timer = new CancellationTokenSource(Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);

        try
        {
            await signer.SignAsync(message, body, voice.PollyRegion, Service, linked.Token);
            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(message, linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                string detail = await ErrorDetailAsync(response, linked.Token);
                if (status == 403 && detail.Contains("not authorized to perform", StringComparison.OrdinalIgnoreCase))
                {
                    throw new PollyNotPermittedException();
                }

                throw new AiAttemptFailedException(detail.Length > 0 ? $"http {status}: {detail}" : $"http {status}", status);
            }

            byte[] audio = await response.Content.ReadAsByteArrayAsync(linked.Token);
            if (audio.Length == 0) throw new AiAttemptFailedException("empty answer");
            return audio;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiAttemptFailedException($"timeout: no answer within {Timeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
            throw new AiAttemptFailedException($"unreachable ({ex.HttpRequestError})");
        }
    }

    /// <summary>The JSON body sent, as it goes over the wire. Public so a test can pin it.</summary>
    public static string RequestBody(string text, AiVoiceSettings voice) => new JsonObject
    {
        ["OutputFormat"] = "mp3",
        ["Text"] = text,
        ["VoiceId"] = voice.PollyVoice,
        ["Engine"] = voice.PollyEngine,
        ["LanguageCode"] = voice.PollyLanguage,
    }.ToJsonString();

    /// <summary>What a reply of <paramref name="characters"/> characters costs at <paramref name="voice"/>'s list price, in US dollars.</summary>
    public static decimal Cost(int characters, AiVoiceSettings voice) => characters * voice.PollyUsdPerMillionChars / 1_000_000m;

    /// <summary>Polly's own words for a refusal, on one line, short, with any AWS account number masked.</summary>
    private static async Task<string> ErrorDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                var node = JsonNode.Parse(text);
                text = node?["message"]?.ToString() ?? node?["Message"]?.ToString() ?? text;
            }
            catch (JsonException)
            {
                // Not JSON: keep the text.
            }

            string flat = string.Join(' ', BedrockRefusals.Mask(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (flat.StartsWith('<')) return string.Empty;
            return flat.Length <= 160 ? flat : flat[..157] + "...";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            return string.Empty;
        }
    }
}

/// <summary>The server's AWS role may not call Amazon Polly: <see cref="PollySpeechClient.Permission"/> is missing from its policy.</summary>
public sealed class PollyNotPermittedException()
    : Exception($"the server's AWS role may not call Amazon Polly ({PollySpeechClient.Permission} is not allowed)");
