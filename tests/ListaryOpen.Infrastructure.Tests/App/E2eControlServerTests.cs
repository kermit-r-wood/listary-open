using System.IO.Pipes;
using System.Text;
using ListaryOpen.App;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class E2eControlServerTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void TryParseRequiresOneExplicitArgumentWithA256BitNonce()
    {
        Assert.Null(E2eControlOptions.TryParse(Array.Empty<string>()));
        Assert.Null(E2eControlOptions.TryParse(new[] { "--listary-e2e-control=true" }));
        Assert.Null(E2eControlOptions.TryParse(new[] { $"--listary-e2e-control={Nonce}", "extra" }));
        Assert.Null(E2eControlOptions.TryParse(new[] { $"--listary-e2e-control={Nonce[..^1]}g" }));

        var options = Assert.IsType<E2eControlOptions>(
            E2eControlOptions.TryParse(new[] { $"--listary-e2e-control={Nonce.ToUpperInvariant()}" }));

        Assert.Equal(Nonce, options.Nonce);
        Assert.Equal($"ListaryOpen.E2E.{Nonce}", options.PipeName);
    }

    [Fact]
    public void GlobalInputPermissionIsOffByDefaultAndCanBeRevoked()
    {
        using var service = new GlobalTextInputService();

        Assert.False(service.AcceptInjectedInputForTesting);

        service.AcceptInjectedInputForTesting = true;
        Assert.True(service.AcceptInjectedInputForTesting);

        service.AcceptInjectedInputForTesting = false;
        Assert.False(service.AcceptInjectedInputForTesting);
    }

    [Fact]
    public async Task AuthenticatedProtocolOnlyAllowsLifecycleInputPermissionAndReadOnlyPrecaptureProofCommands()
    {
        var options = new E2eControlOptions(CreateRandomNonce());
        var injectedInputAllowed = false;
        var shutdownRequested = false;
        using var server = new E2eControlServer(
            options,
            allowed =>
            {
                injectedInputAllowed = allowed;
                return true;
            },
            () => shutdownRequested = true,
            processId => processId == 4242
                ? new E2eDialogPrecaptureProof(processId, FirefoxFileDialogUtility: true, PreloadConfirmedBeforeDialog: true)
                : null);
        server.Start();

        using var client = new NamedPipeClientStream(
            ".",
            options.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(5_000);
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        Assert.StartsWith("Ready ", await ExchangeAsync($"{options.Nonce} Ready", reader, writer));
        Assert.Equal(
            "PrecaptureProof 4242 True True",
            await ExchangeAsync($"{options.Nonce} DialogPrecaptureProof 4242", reader, writer));
        Assert.Equal(
            "Unavailable",
            await ExchangeAsync($"{options.Nonce} DialogPrecaptureProof 4243", reader, writer));
        Assert.Equal("UnsupportedCommand", await ExchangeAsync($"{options.Nonce} OpenSettings", reader, writer));
        Assert.False(injectedInputAllowed);
        Assert.False(shutdownRequested);

        Assert.Equal("Ok", await ExchangeAsync($"{options.Nonce} AllowInjectedInput", reader, writer));
        Assert.True(injectedInputAllowed);
        Assert.False(shutdownRequested);

        Assert.Equal("Ok", await ExchangeAsync($"{options.Nonce} Shutdown", reader, writer));
        Assert.True(SpinWait.SpinUntil(
            () => shutdownRequested && !injectedInputAllowed,
            TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task IncorrectNonceCannotChangeEitherPermissionOrLifecycle()
    {
        var options = new E2eControlOptions(CreateRandomNonce());
        var injectedInputAllowed = false;
        var shutdownRequested = false;
        using var server = new E2eControlServer(
            options,
            allowed =>
            {
                injectedInputAllowed = allowed;
                return true;
            },
            () => shutdownRequested = true);
        server.Start();

        using var client = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5_000);
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        var wrongNonce = CreateRandomNonce();
        Assert.Equal("Unauthorized", await ExchangeAsync($"{wrongNonce} AllowInjectedInput", reader, writer));
        Assert.Equal("Unauthorized", await ExchangeAsync($"{wrongNonce} DialogPrecaptureProof 4242", reader, writer));
        Assert.Equal("Unauthorized", await ExchangeAsync($"{wrongNonce} Shutdown", reader, writer));
        Assert.False(injectedInputAllowed);
        Assert.False(shutdownRequested);
    }

    [Fact]
    public async Task InputPermissionReportsUnavailableWhenTheHookCannotAcceptIt()
    {
        var options = new E2eControlOptions(CreateRandomNonce());
        using var server = new E2eControlServer(options, _ => false, () => { });
        server.Start();

        using var client = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5_000);
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        Assert.Equal("Unavailable", await ExchangeAsync($"{options.Nonce} AllowInjectedInput", reader, writer));
    }

    private static async Task<string?> ExchangeAsync(
        string request,
        StreamReader reader,
        StreamWriter writer)
    {
        await writer.WriteLineAsync(request);
        return await reader.ReadLineAsync();
    }

    private static string CreateRandomNonce()
    {
        var bytes = new byte[E2eControlOptions.NonceByteLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
