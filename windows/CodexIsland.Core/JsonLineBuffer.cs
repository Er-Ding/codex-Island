namespace CodexIsland.Core;

/// <summary>Frame bytes before decoding UTF-8; a character can straddle pipe reads.</summary>
public sealed class JsonLineBuffer(int maximumLineBytes = 4 * 1024 * 1024)
{
    private readonly MemoryStream pending = new();

    public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<byte[]>();
        while (!bytes.IsEmpty)
        {
            var newline = bytes.IndexOf((byte)'\n');
            var part = newline < 0 ? bytes : bytes[..newline];
            if (pending.Length + part.Length > maximumLineBytes) throw new QuotaException(QuotaError.InvalidResponse);
            pending.Write(part);
            if (newline < 0) break;
            if (pending.Length > 0) lines.Add(pending.ToArray());
            pending.SetLength(0);
            bytes = bytes[(newline + 1)..];
        }
        return lines;
    }
}
