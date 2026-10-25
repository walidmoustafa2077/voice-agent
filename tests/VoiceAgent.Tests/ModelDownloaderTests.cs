using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using VoiceAgent.Server;

namespace VoiceAgent.Tests;

public sealed class ModelDownloaderTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Bytes(byte[] payload) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };

    [Fact]
    public async Task Downloads_when_the_file_is_missing()
    {
        var dir = Directory.CreateTempSubdirectory("va-models-");
        var path = Path.Combine(dir.FullName, "tiny.bin");
        var model = new ModelFile("tiny", "https://example/tiny.bin", path, MinBytes: 4);
        using var http = new HttpClient(new Handler(_ => Bytes([1, 2, 3, 4, 5])));

        try
        {
            await ModelDownloader.EnsureAsync(model, http, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await File.ReadAllBytesAsync(path));
            Assert.False(File.Exists(path + ".part"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Rejects_a_response_below_the_minimum()
    {
        var dir = Directory.CreateTempSubdirectory("va-models-");
        var path = Path.Combine(dir.FullName, "partial.bin");
        var model = new ModelFile("partial", "https://example/partial.bin", path, MinBytes: 100);
        using var http = new HttpClient(new Handler(_ => Bytes([9, 9, 9])));

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ModelDownloader.EnsureAsync(model, http, NullLogger.Instance, CancellationToken.None));

            Assert.Contains("incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".part"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Skips_when_the_existing_file_is_big_enough()
    {
        var dir = Directory.CreateTempSubdirectory("va-models-");
        var path = Path.Combine(dir.FullName, "ready.bin");
        await File.WriteAllBytesAsync(path, new byte[200]);
        var model = new ModelFile("ready", "https://example/ready.bin", path, MinBytes: 100);
        var handler = new Handler(_ => throw new InvalidOperationException("should not download"));
        using var http = new HttpClient(handler);

        try
        {
            await ModelDownloader.EnsureAsync(model, http, NullLogger.Instance, CancellationToken.None);
            Assert.Equal(0, handler.Calls);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
