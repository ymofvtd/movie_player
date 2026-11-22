using System.Buffers.Binary;
using System.Text;
using MyCinema.Core.Playback;

namespace MyCinema.Infrastructure.Services;

internal readonly record struct MatroskaTrackFlags(bool Original, bool Commentary, bool Forced);

internal sealed class MatroskaSubtitles(Stream stream)
{
    private const uint Segment = 0x18538067, Info = 0x1549A966, Tracks = 0x1654AE6B, Cluster = 0x1F43B675;
    private const uint TrackEntry = 0xAE, BlockGroup = 0xA0, Block = 0xA1, SimpleBlock = 0xA3, BlockDuration = 0x9B;
    private readonly SortedDictionary<ulong, Track> _tracks = [];
    private double _timestampScale = 1_000_000;

    public static (uint Codec, SubtitleTimeline Timeline)[] Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024);
        return new MatroskaSubtitles(file).Read();
    }

    public static (MatroskaTrackFlags[] Audio, MatroskaTrackFlags[] Subtitles) ReadTrackFlags(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192);
        return new MatroskaSubtitles(file).ReadTrackFlags();
    }

    private (uint Codec, SubtitleTimeline Timeline)[] Read()
    {
        if (FindSegment() is { } segment) ReadSegment(segment.End);
        return _tracks.Values.Select(track => (FourCc(track.Codec), new SubtitleTimeline(track.Cues))).ToArray();
    }

    private Element? FindSegment()
    {
        while (stream.Position < stream.Length)
        {
            var element = ReadElement(stream.Length);
            if (element.Id == Segment) return element;
            stream.Position = element.End;
        }
        return null;
    }

    private (MatroskaTrackFlags[] Audio, MatroskaTrackFlags[] Subtitles) ReadTrackFlags()
    {
        if (FindSegment() is not { } segment) return ([], []);
        foreach (var element in Children(segment))
        {
            if (element.UnknownSize) break;
            if (element.Id != Tracks) continue;
            var audio = new SortedDictionary<ulong, MatroskaTrackFlags>();
            var subtitles = new SortedDictionary<ulong, MatroskaTrackFlags>();
            foreach (var entry in Children(element))
            {
                if (entry.Id != TrackEntry) continue;
                ulong number = 0, type = 0;
                bool original = false, commentary = false, forced = false;
                foreach (var field in Children(entry))
                {
                    switch (field.Id)
                    {
                        case 0xD7: number = ReadUnsigned(field); break;
                        case 0x83: type = ReadUnsigned(field); break;
                        case 0x55AE: original = ReadUnsigned(field) == 1; break;
                        case 0x55AF: commentary = ReadUnsigned(field) == 1; break;
                        case 0x55AA: forced = ReadUnsigned(field) == 1; break;
                    }
                }
                if (number != 0 && type is 2 or 0x11)
                    (type == 2 ? audio : subtitles).TryAdd(number, new(original, commentary, forced));
            }
            return (audio.Values.ToArray(), subtitles.Values.ToArray());
        }
        return ([], []);
    }

    private void ReadSegment(long end)
    {
        while (stream.Position < end)
        {
            var element = ReadElement(end);
            switch (element.Id)
            {
                case Info:
                    foreach (var child in Children(element))
                        if (child.Id == 0x2AD7B1) _timestampScale = ReadUnsigned(child);
                    break;
                case Tracks:
                    foreach (var child in Children(element))
                        if (child.Id == TrackEntry) ReadTrack(child);
                    break;
                case Cluster when _tracks.Count > 0 || element.UnknownSize:
                    ReadCluster(element);
                    continue;
            }
            stream.Position = element.End;
        }
    }

    private void ReadTrack(Element entry)
    {
        ulong number = 0, type = 0, defaultDuration = 0;
        var codec = string.Empty;
        var encoded = false;
        foreach (var element in Children(entry))
        {
            switch (element.Id)
            {
                case 0xD7: number = ReadUnsigned(element); break;
                case 0x83: type = ReadUnsigned(element); break;
                case 0x86: codec = ReadUtf8(element); break;
                case 0x23E383: defaultDuration = ReadUnsigned(element); break;
                case 0x6D80: encoded = true; break;
            }
        }
        if (number > 0 && type == 0x11 && FourCc(codec) != 0)
            _tracks.TryAdd(number, new Track(codec, defaultDuration, encoded));
    }

    private void ReadCluster(Element cluster)
    {
        ulong timestamp = 0;
        var blocks = new List<(Track Track, short Offset, ulong Duration, string Text)>();
        while (stream.Position < cluster.End)
        {
            var start = stream.Position;
            var element = ReadElement(cluster.End);
            if (cluster.UnknownSize && element.Id is Cluster or Tracks or Info or 0x114D9B74 or 0x1C53BB6B or 0x1254C367 or 0x1941A469 or 0x1043A770)
            {
                stream.Position = start;
                break;
            }
            if (element.Id == 0xE7) timestamp = ReadUnsigned(element);
            if (element.Id == SimpleBlock && ReadBlock(element) is { } simple)
                blocks.Add((simple.Track, simple.Offset, 0, simple.Text));
            if (element.Id == BlockGroup)
            {
                ulong duration = 0;
                (Track Track, short Offset, string Text)? block = null;
                foreach (var child in Children(element))
                {
                    if (child.Id == BlockDuration) duration = ReadUnsigned(child);
                    if (child.Id == Block) block = ReadBlock(child);
                }
                if (block is { } cue) blocks.Add((cue.Track, cue.Offset, duration, cue.Text));
            }
            stream.Position = element.End;
        }
        foreach (var block in blocks)
        {
            var start = ((double)timestamp + block.Offset) * _timestampScale / 1_000_000;
            var duration = (block.Duration > 0 ? block.Duration * _timestampScale : block.Track.DefaultDuration) / 1_000_000;
            if (duration > 0 && start + duration < long.MaxValue && block.Text.Length > 0)
                block.Track.Cues.Add(new((long)start, (long)(start + duration), block.Text));
        }
    }

    private (Track Track, short Offset, string Text)? ReadBlock(Element element)
    {
        var (number, _) = ReadVInt(element.End);
        if (!_tracks.TryGetValue(number, out var track) || track.Encoded || element.End - stream.Position < 3)
            return null;

        Span<byte> header = stackalloc byte[3];
        stream.ReadExactly(header);
        if ((header[2] & 0x06) != 0) return null; // Lacing is not used by the supported subtitle mappings.
        var offset = BinaryPrimitives.ReadInt16BigEndian(header);
        var raw = ReadUtf8(element);
        var text = FourCc(track.Codec) == Ssa ? DecodeAssBlock(raw) : SubtitleFiles.PlainText(raw);
        return (track, offset, text);
    }

    private static string DecodeAssBlock(string raw)
    {
        var fields = raw.Split(',', 9);
        return fields.Length == 9 ? SubtitleFiles.AssText(fields[8]) : string.Empty;
    }

    internal const uint Subt = 's' | 'u' << 8 | 'b' << 16 | 't' << 24;
    internal const uint Ssa = 's' | 's' << 8 | 'a' << 16 | ' ' << 24;
    internal const uint Vtt = 'w' | 'v' << 8 | 't' << 16 | 't' << 24;

    private static uint FourCc(string codec) => codec switch
    {
        "S_TEXT/UTF8" or "S_TEXT/ASCII" => Subt,
        "S_TEXT/ASS" or "S_TEXT/SSA" or "S_ASS" or "S_SSA" => Ssa,
        "S_TEXT/WEBVTT" or "D_WEBVTT/SUBTITLES" => Vtt,
        _ => 0
    };

    private IEnumerable<Element> Children(Element element)
    {
        while (stream.Position < element.End)
        {
            var child = ReadElement(element.End);
            yield return child;
            stream.Position = child.End;
        }
    }

    private ulong ReadUnsigned(Element element)
    {
        var length = element.End - stream.Position;
        if (length > 8) throw new InvalidDataException("Invalid Matroska integer.");
        ulong value = 0;
        for (var i = 0; i < length; i++) value = value << 8 | (byte)ReadByte(element.End);
        return value;
    }

    private string ReadUtf8(Element element)
    {
        var length = element.End - stream.Position;
        if (length > 64 * 1024) throw new InvalidDataException("Invalid Matroska subtitle text size.");
        var bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    private Element ReadElement(long parentEnd)
    {
        var (id, _) = ReadVInt(parentEnd, keepMarker: true);
        if (id > uint.MaxValue) throw new InvalidDataException("Invalid Matroska element ID.");
        var (size, unknown) = ReadVInt(parentEnd);
        if (unknown && id is not (Segment or Cluster) || !unknown && size > (ulong)(parentEnd - stream.Position))
            throw new InvalidDataException("Invalid Matroska element size.");
        return new((uint)id, unknown ? parentEnd : stream.Position + (long)size, unknown);
    }

    private (ulong Value, bool Unknown) ReadVInt(long end, bool keepMarker = false)
    {
        var first = ReadByte(end);
        if (first == 0) throw new InvalidDataException("Invalid Matroska variable-length integer.");
        var marker = 0x80;
        var width = 1;
        while ((first & marker) == 0)
        {
            marker >>= 1;
            width++;
        }
        ulong value = (uint)(keepMarker ? first : first & (marker - 1));
        for (var i = 1; i < width; i++) value = value << 8 | (byte)ReadByte(end);
        return (value, !keepMarker && value == (1UL << (width * 7)) - 1);
    }

    private int ReadByte(long end)
    {
        var value = stream.Position < end ? stream.ReadByte() : -1;
        return value >= 0 ? value : throw new EndOfStreamException("The Matroska subtitle data ended early.");
    }

    private readonly record struct Element(uint Id, long End, bool UnknownSize);

    private sealed record Track(string Codec, ulong DefaultDuration, bool Encoded)
    {
        public List<SubtitleCue> Cues { get; } = [];
    }
}
