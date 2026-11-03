using Avalonia.Media.Imaging;
using PdfReader.Rendering;

namespace PdfReader.App.Services;

/// <summary>Identifies one rendered bitmap. ScaleKey -1 marks the low-res whole-page preview.</summary>
public readonly record struct TileKey(long DocumentId, int Page, long ScaleKey, PageColors Colors, int TileX, int TileY);

/// <summary>
/// LRU cache of rendered tiles shared by all open documents, bounded by memory rather than
/// count. UI thread only. Visible tiles are touched on every frame so they are never evicted.
/// </summary>
public sealed class TileCache(long budgetBytes)
{
    public static TileCache Shared { get; } = new(384L * 1024 * 1024);

    private readonly Dictionary<TileKey, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _lru = new();
    private long _bytes;

    public long Bytes => _bytes;

    public Bitmap? Get(TileKey key, bool touch = true)
    {
        if (!_map.TryGetValue(key, out var node))
            return null;
        if (touch)
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
        return node.Value.Bitmap;
    }

    public bool Contains(TileKey key) => _map.ContainsKey(key);

    public void Add(TileKey key, Bitmap bitmap)
    {
        if (_map.Remove(key, out var existing))
            RemoveNode(existing);

        var bytes = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
        _map[key] = _lru.AddFirst(new Entry(key, bitmap, bytes));
        _bytes += bytes;

        while (_bytes > budgetBytes && _lru.Count > 1)
        {
            var last = _lru.Last!;
            _map.Remove(last.Value.Key);
            RemoveNode(last);
        }
    }

    public void RemoveDocument(long documentId)
    {
        foreach (var key in _map.Keys.Where(k => k.DocumentId == documentId).ToList())
        {
            _map.Remove(key, out var node);
            RemoveNode(node!);
        }
    }

    private void RemoveNode(LinkedListNode<Entry> node)
    {
        _lru.Remove(node);
        _bytes -= node.Value.Bytes;
        node.Value.Bitmap.Dispose();
    }

    private sealed record Entry(TileKey Key, Bitmap Bitmap, long Bytes);
}
