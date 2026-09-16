using System.Globalization;

namespace TerminalFs.Internal.Mount;

/// <summary>
/// The container image the SMB bridge runs. It is built here rather than pulled, so nothing is
/// fetched from a registry this project does not control, and the three files it is built from
/// are short enough to read.
/// </summary>
internal static class BridgeImage
{
    private const string Dockerfile = """
        FROM alpine:3
        RUN apk add --no-cache samba-server
        COPY smb.conf /etc/samba/smb.conf
        COPY entrypoint.sh /usr/local/bin/entrypoint.sh
        ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
        """;

    private const string SmbConf = """
        [global]
           workgroup = WORKGROUP
           server string = terminalfs
           security = user
           map to guest = Bad User
           guest account = nobody
           server min protocol = SMB2
           disable netbios = yes
           load printers = no
           printing = bsd
           printcap name = /dev/null
           unix extensions = no
           log level = 1

           # A command runs when the control file is closed, so a close that the client defers
           # defers the command with it. An oplock or a lease is exactly permission to defer one:
           # the client keeps the handle after close(2) returns and hands it back when it feels
           # like it, which turns 'echo … > ctl' into a command that runs at some point. Off.
           oplocks = no
           level2 oplocks = no
           kernel oplocks = no
           smb2 leases = no
           durable handles = no
           strict sync = yes

           # A read of a wait file does not answer until the command stops. Without asynchronous
           # reads that read occupies the smbd process serving this client, and everything else
           # the client asks for waits behind it — an 'ls' in another shell would hang for as
           # long as the command runs. A threshold of one byte makes every read asynchronous.
           aio read size = 1
           aio write size = 1

        [terminalfs]
           path = /srv/terminalfs
           browseable = yes
           guest ok = yes
           force user = nobody

           # The share must not be read only, or Samba strips the write bits from every file it
           # exports and /ctl cannot be written through the mount — which is the documented way
           # to use it. Nothing is made writable by this: the 9P server owns the permissions and
           # answers every write but /ctl with a refusal, which is where that check belongs.
           read only = no
        """;

    /// <summary>
    /// Mounts the host's 9P server and then serves it. The host address is resolved to an
    /// address first: v9fs takes an IP for <c>trans=tcp</c> and answers a host name with
    /// <c>EINVAL</c>, which is a confusing way to be told to use a number.
    /// </summary>
    private const string Entrypoint = """
        #!/bin/sh
        set -e

        : "${TERMINALFS_HOST:=host.docker.internal}"
        : "${TERMINALFS_PORT:=15641}"

        ip=$(getent hosts "$TERMINALFS_HOST" | awk '{print $1}' | head -1)
        : "${ip:=$TERMINALFS_HOST}"

        mkdir -p /srv/terminalfs

        echo "terminalfs-bridge: mounting 9p from $ip:$TERMINALFS_PORT"
        # uname/dfltuid pin the attach identity to root, which is who the server says owns the
        # tree; without them v9fs attaches as "nobody" with uid -1, which can never match an owner
        # and so cannot write /ctl. access=any lets Samba reach the mount as the unprivileged user
        # it serves files with, while the 9P identity stays the one above. The tree's own
        # permissions still refuse every write but /ctl.
        #
        # cache=none because every caching mode pins one 9P fid per cached dentry for as long as
        # the dentry cache holds it, access=any puts the whole container on a single connection and
        # so a single fid table, and the server caps fids per connection at 65536. The tree has
        # 72,282 directories at type depth alone, so one recursive find walked past the cap and
        # wedged the mount: every open after it was refused ENFILE, which arrives here as "Too many
        # open files", and nothing recovered it short of a remount — the VM never prunes a dentry
        # cache it has the memory to keep. Uncached dentries are deleted on release and the fid is
        # clunked with them. What that costs: find -maxdepth 4 over SMB takes 168 s and answers all
        # 72,282 directories without one error.
        mount -t 9p -o "trans=tcp,port=$TERMINALFS_PORT,version=9p2000.L,msize=262144,cache=none,uname=root,dfltuid=0,access=any" "$ip" /srv/terminalfs

        echo "terminalfs-bridge: exporting /srv/terminalfs over SMB"
        exec smbd --foreground --no-process-group --debug-stdout
        """;

    /// <summary>Whether the bridge image has already been built on this machine.</summary>
    internal static async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        CommandResult result = await ProcessRunner.RunAsync(
            "docker",
            ["image", "inspect", "--format", "{{.Id}}", MountSettings.ImageTag],
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.Ok;
    }

    /// <summary>
    /// Builds the image if it is not already built. Docker's own layer cache makes a rebuild
    /// free, so this is called every time rather than guarded by a check that could go stale.
    /// </summary>
    internal static async Task<CommandResult> BuildAsync(CancellationToken cancellationToken = default)
    {
        // An unpredictable name at 0700, in one call. What goes in here becomes the entrypoint of
        // a container run with SYS_ADMIN, so a name another local user can guess and write to
        // before the build reads it is not a directory to create with the default mode.
        string context = Directory.CreateTempSubdirectory("terminalfs-bridge-").FullName;

        try
        {
            await File.WriteAllTextAsync(Path.Combine(context, "Dockerfile"), Dockerfile, cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(context, "smb.conf"), SmbConf, cancellationToken)
                .ConfigureAwait(false);

            string entrypoint = Path.Combine(context, "entrypoint.sh");
            await File.WriteAllTextAsync(entrypoint, Entrypoint, cancellationToken).ConfigureAwait(false);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    entrypoint,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            return await ProcessRunner.RunAsync(
                "docker",
                ["build", "-q", "-t", MountSettings.ImageTag, context],
                TimeSpan.FromMinutes(5),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(context, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover temporary directory is not worth failing a mount over.
            }
        }
    }
}
