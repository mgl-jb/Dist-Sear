using System.Text;
using DistSear.Index.Postings;

namespace DistSear.Index.Segments;

/// <summary>
/// Binary format for a sealed segment.
///
/// Postings are written verbatim: they are already variable-byte delta-gap encoded in memory, so
/// persisting them is a buffer copy rather than a re-encode, and restoring them needs no decode at
/// all. Everything else is length-prefixed so a reader never has to scan for a delimiter.
///
/// This is what a snapshot contains. A recovering replica downloads it and resumes the change feed
/// from the token recorded alongside, instead of replaying the whole partition from the beginning.
/// </summary>
public static class SegmentSerializer
{
    private const uint Magic = 0x47535344; // "DSSG"
    private const int FormatVersion = 1;

    private enum ValueTag : byte
    {
        Null = 0,
        String = 1,
        Long = 2,
        Double = 3,
        Boolean = 4,
        DateTimeOffset = 5,
        Array = 6
    }

    public static void Write(Segment segment, Stream destination)
    {
        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(segment.MaxDoc);

        foreach (var id in segment.ExternalIds)
        {
            writer.Write(id);
        }

        WriteAcls(writer, segment.Acls);
        WriteStoredFields(writer, segment.StoredFields);
        WriteFields(writer, segment.Fields);
        WriteDocValues(writer, segment.DocValues);
        WriteLiveDocs(writer, segment.LiveDocs);
    }

    public static Segment Read(Stream source)
    {
        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);

        if (reader.ReadUInt32() != Magic)
        {
            throw new InvalidDataException("Not a segment file.");
        }

        var version = reader.ReadInt32();

        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported segment format version {version}.");
        }

        var maxDoc = reader.ReadInt32();
        var externalIds = new string[maxDoc];

        for (var i = 0; i < maxDoc; i++)
        {
            externalIds[i] = reader.ReadString();
        }

        var acls = ReadAcls(reader, maxDoc);
        var stored = ReadStoredFields(reader, maxDoc);
        var fields = ReadFields(reader, maxDoc);
        var docValues = ReadDocValues(reader);

        var segment = new Segment(externalIds, fields, docValues, stored, acls);
        ReadLiveDocs(reader, segment.LiveDocs);

        return segment;
    }

    private static void WriteAcls(BinaryWriter writer, string[][] acls)
    {
        foreach (var acl in acls)
        {
            writer.Write7BitEncodedInt(acl.Length);

            foreach (var entry in acl)
            {
                writer.Write(entry);
            }
        }
    }

    private static string[][] ReadAcls(BinaryReader reader, int maxDoc)
    {
        var acls = new string[maxDoc][];

        for (var i = 0; i < maxDoc; i++)
        {
            var count = reader.Read7BitEncodedInt();
            var acl = new string[count];

            for (var j = 0; j < count; j++)
            {
                acl[j] = reader.ReadString();
            }

            acls[i] = acl;
        }

        return acls;
    }

    private static void WriteStoredFields(BinaryWriter writer, Dictionary<string, object?>[] stored)
    {
        foreach (var document in stored)
        {
            writer.Write7BitEncodedInt(document.Count);

            foreach (var (name, value) in document)
            {
                writer.Write(name);
                WriteValue(writer, value);
            }
        }
    }

    private static Dictionary<string, object?>[] ReadStoredFields(BinaryReader reader, int maxDoc)
    {
        var stored = new Dictionary<string, object?>[maxDoc];

        for (var i = 0; i < maxDoc; i++)
        {
            var count = reader.Read7BitEncodedInt();
            var document = new Dictionary<string, object?>(count, StringComparer.Ordinal);

            for (var j = 0; j < count; j++)
            {
                var name = reader.ReadString();
                document[name] = ReadValue(reader);
            }

            stored[i] = document;
        }

        return stored;
    }

    private static void WriteValue(BinaryWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.Write((byte)ValueTag.Null);
                break;

            case string s:
                writer.Write((byte)ValueTag.String);
                writer.Write(s);
                break;

            case bool b:
                writer.Write((byte)ValueTag.Boolean);
                writer.Write(b);
                break;

            case DateTimeOffset dto:
                writer.Write((byte)ValueTag.DateTimeOffset);
                writer.Write(dto.ToUnixTimeMilliseconds());
                break;

            case long or int or short or byte:
                writer.Write((byte)ValueTag.Long);
                writer.Write(Convert.ToInt64(value));
                break;

            case double or float or decimal:
                writer.Write((byte)ValueTag.Double);
                writer.Write(Convert.ToDouble(value));
                break;

            case System.Collections.IEnumerable enumerable:
            {
                var items = enumerable.Cast<object?>().ToList();
                writer.Write((byte)ValueTag.Array);
                writer.Write7BitEncodedInt(items.Count);

                foreach (var item in items)
                {
                    WriteValue(writer, item);
                }

                break;
            }

            default:
                // Anything unrecognised is persisted as its string form rather than failing the
                // whole snapshot for one odd field.
                writer.Write((byte)ValueTag.String);
                writer.Write(value.ToString() ?? string.Empty);
                break;
        }
    }

    private static object? ReadValue(BinaryReader reader)
    {
        var tag = (ValueTag)reader.ReadByte();

        switch (tag)
        {
            case ValueTag.Null:
                return null;
            case ValueTag.String:
                return reader.ReadString();
            case ValueTag.Long:
                return reader.ReadInt64();
            case ValueTag.Double:
                return reader.ReadDouble();
            case ValueTag.Boolean:
                return reader.ReadBoolean();
            case ValueTag.DateTimeOffset:
                return DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64());
            case ValueTag.Array:
            {
                var count = reader.Read7BitEncodedInt();
                var items = new object?[count];

                for (var i = 0; i < count; i++)
                {
                    items[i] = ReadValue(reader);
                }

                return items;
            }

            default:
                throw new InvalidDataException($"Unknown stored value tag {tag}.");
        }
    }

    private static void WriteFields(BinaryWriter writer, IReadOnlyDictionary<string, FieldTerms> fields)
    {
        writer.Write7BitEncodedInt(fields.Count);

        foreach (var (name, terms) in fields)
        {
            writer.Write(name);
            writer.Write(terms.DocumentCount);
            writer.Write(terms.SumFieldLength);

            foreach (var norm in terms.Norms)
            {
                writer.Write7BitEncodedInt(norm);
            }

            writer.Write7BitEncodedInt(terms.TermCount);

            foreach (var (term, postings) in terms.AllTerms)
            {
                writer.Write(term);
                writer.Write(postings.DocumentFrequency);
                writer.Write(postings.TotalTermFrequency);
                writer.Write(postings.HasPositions);

                writer.Write7BitEncodedInt(postings.Skips.Count);

                foreach (var skip in postings.Skips)
                {
                    writer.Write7BitEncodedInt(skip.DocId);
                    writer.Write7BitEncodedInt(skip.Offset);
                }

                // The encoded postings go out exactly as they sit in memory.
                writer.Write7BitEncodedInt(postings.ByteLength);
                writer.Write(postings.Data);
            }
        }
    }

    private static Dictionary<string, FieldTerms> ReadFields(BinaryReader reader, int maxDoc)
    {
        var count = reader.Read7BitEncodedInt();
        var fields = new Dictionary<string, FieldTerms>(count, StringComparer.Ordinal);

        for (var f = 0; f < count; f++)
        {
            var name = reader.ReadString();
            var documentCount = reader.ReadInt32();
            var sumFieldLength = reader.ReadInt64();

            var norms = new int[maxDoc];

            for (var i = 0; i < maxDoc; i++)
            {
                norms[i] = reader.Read7BitEncodedInt();
            }

            var termCount = reader.Read7BitEncodedInt();
            var terms = new Dictionary<string, PostingsList>(termCount, StringComparer.Ordinal);

            for (var t = 0; t < termCount; t++)
            {
                var term = reader.ReadString();
                var documentFrequency = reader.ReadInt32();
                var totalTermFrequency = reader.ReadInt64();
                var hasPositions = reader.ReadBoolean();

                var skipCount = reader.Read7BitEncodedInt();
                var skips = new SkipEntry[skipCount];

                for (var s = 0; s < skipCount; s++)
                {
                    skips[s] = new SkipEntry(reader.Read7BitEncodedInt(), reader.Read7BitEncodedInt());
                }

                var dataLength = reader.Read7BitEncodedInt();
                var data = reader.ReadBytes(dataLength);

                terms[term] = PostingsList.FromEncoded(
                    data,
                    skips,
                    documentFrequency,
                    totalTermFrequency,
                    hasPositions);
            }

            fields[name] = new FieldTerms(terms, norms, sumFieldLength, documentCount);
        }

        return fields;
    }

    private static void WriteDocValues(
        BinaryWriter writer,
        IReadOnlyDictionary<string, DocValuesColumn> columns)
    {
        writer.Write7BitEncodedInt(columns.Count);

        foreach (var (name, column) in columns)
        {
            writer.Write(name);

            switch (column)
            {
                case NumericDocValues numeric:
                    writer.Write((byte)0);
                    writer.Write7BitEncodedInt(numeric.Count);

                    for (var i = 0; i < numeric.Count; i++)
                    {
                        writer.Write(numeric.HasValue(i));
                        writer.Write(numeric.GetDouble(i, 0));
                    }

                    break;

                case KeywordDocValues keyword:
                    writer.Write((byte)1);
                    writer.Write7BitEncodedInt(keyword.Count);

                    for (var i = 0; i < keyword.Count; i++)
                    {
                        var values = keyword.GetStrings(i);
                        writer.Write7BitEncodedInt(values.Count);

                        foreach (var value in values)
                        {
                            writer.Write(value);
                        }
                    }

                    break;

                default:
                    throw new InvalidDataException($"Cannot serialise doc values of type {column.GetType().Name}.");
            }
        }
    }

    private static Dictionary<string, DocValuesColumn> ReadDocValues(BinaryReader reader)
    {
        var count = reader.Read7BitEncodedInt();
        var columns = new Dictionary<string, DocValuesColumn>(count, StringComparer.Ordinal);

        for (var c = 0; c < count; c++)
        {
            var name = reader.ReadString();
            var kind = reader.ReadByte();
            var length = reader.Read7BitEncodedInt();

            if (kind == 0)
            {
                var values = new double[length];
                var present = new bool[length];

                for (var i = 0; i < length; i++)
                {
                    present[i] = reader.ReadBoolean();
                    values[i] = reader.ReadDouble();
                }

                columns[name] = new NumericDocValues(values, present);
            }
            else
            {
                var values = new string[length][];

                for (var i = 0; i < length; i++)
                {
                    var valueCount = reader.Read7BitEncodedInt();
                    var entry = new string[valueCount];

                    for (var v = 0; v < valueCount; v++)
                    {
                        entry[v] = reader.ReadString();
                    }

                    values[i] = entry;
                }

                columns[name] = new KeywordDocValues(values);
            }
        }

        return columns;
    }

    private static void WriteLiveDocs(BinaryWriter writer, LiveDocs liveDocs)
    {
        for (var i = 0; i < liveDocs.Capacity; i++)
        {
            writer.Write(liveDocs.IsLive(i));
        }
    }

    private static void ReadLiveDocs(BinaryReader reader, LiveDocs liveDocs)
    {
        for (var i = 0; i < liveDocs.Capacity; i++)
        {
            if (!reader.ReadBoolean())
            {
                liveDocs.Delete(i);
            }
        }
    }
}
