using Npgsql;
using System.Data;

namespace CraneJournal.Web.Services;

/// <summary>At most one PostgreSQL chunk is streamed at a time; no complete video in process memory.</summary>
public sealed class ChunkStream : Stream
{
    private readonly NpgsqlConnection connection;
    private readonly NpgsqlCommand command;
    private readonly NpgsqlDataReader reader;
    private Stream? current;
    private readonly long length;
    private long position;
    private bool disposed;
    private ChunkStream(NpgsqlConnection connection, NpgsqlCommand command, NpgsqlDataReader reader, long length)
    { this.connection = connection; this.command = command; this.reader = reader; this.length = length; }
    public static async Task<ChunkStream> Open(string connectionString, Guid id, long length, CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            var command = new NpgsqlCommand("SELECT \"Data\" FROM \"Chunks\" WHERE \"MediaId\"=@id ORDER BY \"Index\"", connection);
            command.Parameters.AddWithValue("id", id);
            var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            return new(connection, command, reader, length);
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        ObjectDisposedException.ThrowIf(disposed, this);
        while (true)
        {
            if (current is null)
            {
                if (!await reader.ReadAsync(cancellationToken))
                {
                    if (position != length) throw new IOException("Incomplete stored attachment.");
                    return 0;
                }
                current = reader.GetStream(0);
            }
            int read = await current.ReadAsync(buffer, cancellationToken);
            if (read > 0) { position += read; return read; }
            await current.DisposeAsync(); current = null;
        }
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    public override bool CanRead => !disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed) { disposed = true; current?.Dispose(); reader.Dispose(); command.Dispose(); connection.Dispose(); }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            if (current is not null) await current.DisposeAsync();
            await reader.DisposeAsync(); await command.DisposeAsync(); await connection.DisposeAsync();
        }
        GC.SuppressFinalize(this);
    }
}
