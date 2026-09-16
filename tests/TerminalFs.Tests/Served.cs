using NineP.Client;
using NineP.Protocol;
using NineP.Protocol.Transports;
using NineP.Server;
using TerminalFs.Core;
using TerminalFs.Core.Permissions;
using TerminalFs.Internal.Server;

namespace TerminalFs.Tests;

/// <summary>
/// A real server on a real socket, with a real client on the other end.
/// </summary>
/// <remarks>
/// The suites below drive the protocol rather than the model, because what they are checking is
/// the part the model cannot see: that a clunk runs the command, that a read of a growing file
/// ends, that removing a directory reaches the registry, and that a refusal arrives as an error
/// a caller can act on. A stand-in for the server would be a stand-in for exactly that.
/// </remarks>
internal sealed class Served : IAsyncDisposable
{
    private readonly string root;
    private readonly NinePServer server;
    private readonly Task serving;
    private readonly SettingsWatcher? watcher;

    private Served(
        string root,
        CommandRegistry registry,
        NinePServer server,
        Task serving,
        SettingsWatcher? watcher)
    {
        this.root = root;
        Registry = registry;
        this.server = server;
        this.serving = serving;
        this.watcher = watcher;
    }

    internal CommandRegistry Registry { get; }

    internal NinePAddress Address => server.Endpoints[0];

    /// <summary>Starts a server on a port the kernel picks.</summary>
    internal static async Task<Served> StartAsync(
        IEnumerable<string>? deny = null,
        TimeSpan? keep = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "terminalfs-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        SettingsWatcher? watcher = null;

        if (deny is not null)
        {
            string settings = Path.Combine(root, "settings.json");

            await File.WriteAllTextAsync(
                settings,
                $$"""{ "permissions": { "deny": [{{string.Join(", ", deny.Select(rule => $"\"{rule}\""))}}] } }""");

            watcher = SettingsWatcher.Start(settings, TimeSpan.FromSeconds(60));
        }

        CommandRegistry registry = CommandRegistry.Create(new CommandOptions
        {
            OutputRoot = Path.Combine(root, "out"),
            WorkingDirectory = root,
            KeepAfterExit = keep ?? TimeSpan.FromSeconds(60),
            WaitTimeout = TimeSpan.FromSeconds(20),
            Settings = watcher,
        });

        var server = new NinePServer(new ServerOptions
        {
            Listen = [NinePAddress.Parse("tcp://127.0.0.1:0")],
        });

        Task serving = server.ServeAsync(new TerminalTree(registry));
        await server.ListeningAsync();

        return new Served(root, registry, server, serving, watcher);
    }

    /// <summary>Connects and attaches a client, speaking the dialect a Linux mount speaks.</summary>
    internal async Task<NinePSession> ConnectAsync()
    {
        NinePSession session = await NinePClient.ConnectAsync(
            Address,
            new ClientOptions
            {
                Dialects = [Dialect.P9_2000_L],
                MinDialect = Dialect.P9_2000_L,
                Uname = "root",
            });

        // The path-level calls all start from the root fid, which only exists after an attach.
        // The fid is not disposed here: disposing it clunks the root the session goes on using,
        // and the session clunks whatever is left when it is disposed.
        _ = await session.AttachAsync();

        return session;
    }

    public async ValueTask DisposeAsync()
    {
        await server.StopAsync(TimeSpan.FromSeconds(5));

        try
        {
            await serving;
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
        }

        await server.DisposeAsync();

        Registry.Dispose();
        watcher?.Dispose();

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }
}
