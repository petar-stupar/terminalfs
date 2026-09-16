namespace TerminalFs.Core;

/// <summary>
/// One of a command's two output streams, held in a file outside the tree.
/// </summary>
/// <remarks>
/// <para>
/// A file rather than a buffer, because the output of a command is not bounded by anything this
/// program controls and a caller reads it at whatever offset they like, long after the bytes
/// arrived. It lives outside the served tree so that a command redirecting into its own
/// directory cannot happen: what is under <c>/cmd</c> is this program's account of the command,
/// not a place to put things.
/// </para>
/// <para>
/// <see cref="Length"/> is what a reader is told, and it is raised only after the bytes are in
/// the file and flushed. A size advertised ahead of the content would make a reader ask for bytes
/// that are not there yet, and a short read is how a client decides a file has ended.
/// </para>
/// </remarks>
public sealed class OutputFile : IDisposable
{
    private readonly Lock gate = new();
    private FileStream? writer;
    private long length;
    private uint revision;
    private DateTimeOffset lastWrite;
    private bool released;

    internal OutputFile(string path, DateTimeOffset now)
    {
        Path = path;
        lastWrite = now;

        writer = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
    }

    /// <summary>Where the bytes are.</summary>
    public string Path { get; }

    /// <summary>How many bytes have arrived and been flushed.</summary>
    public long Length => Volatile.Read(ref length);

    /// <summary>
    /// Moves every time bytes arrive, so that a qid version can say the file changed even though
    /// it is at the same path.
    /// </summary>
    public uint Revision => Volatile.Read(ref revision);

    /// <summary>When bytes last arrived.</summary>
    public DateTimeOffset LastWrite
    {
        get
        {
            lock (gate)
            {
                return lastWrite;
            }
        }
    }

    /// <summary>Opens the file for reading. The writer may still be appending to it.</summary>
    /// <exception cref="CommandException">The command has been removed.</exception>
    public Stream OpenRead()
    {
        lock (gate)
        {
            if (released)
            {
                throw new CommandException("that command is gone", CommandErrno.NotFound);
            }
        }

        try
        {
            return new FileStream(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            throw new CommandException("that command is gone", CommandErrno.NotFound);
        }
        catch (DirectoryNotFoundException)
        {
            throw new CommandException("that command is gone", CommandErrno.NotFound);
        }
    }

    /// <summary>Appends what the process produced, and only then says the file grew.</summary>
    internal async ValueTask AppendAsync(ReadOnlyMemory<byte> data, DateTimeOffset now, CancellationToken cancellationToken)
    {
        FileStream? stream;

        lock (gate)
        {
            stream = writer;
        }

        if (stream is null)
        {
            return;
        }

        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        lock (gate)
        {
            lastWrite = now;
        }

        Volatile.Write(ref length, Volatile.Read(ref length) + data.Length);
        Interlocked.Increment(ref revision);
    }

    /// <summary>Appends a sentence of this program's own, when there is no process to produce one.</summary>
    internal void Append(string text, DateTimeOffset now)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);

        FileStream? stream;

        lock (gate)
        {
            stream = writer;
        }

        if (stream is null)
        {
            return;
        }

        stream.Write(bytes);
        stream.Flush();

        lock (gate)
        {
            lastWrite = now;
        }

        Volatile.Write(ref length, Volatile.Read(ref length) + bytes.Length);
        Interlocked.Increment(ref revision);
    }

    /// <summary>Closes the writer. Readers already open keep reading what is there.</summary>
    internal void CloseWriter()
    {
        FileStream? stream;

        lock (gate)
        {
            stream = writer;
            writer = null;
        }

        stream?.Dispose();
    }

    /// <summary>Marks the file gone, so a later open is refused rather than resurrecting it.</summary>
    internal void Release()
    {
        CloseWriter();

        lock (gate)
        {
            released = true;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Release();
}
