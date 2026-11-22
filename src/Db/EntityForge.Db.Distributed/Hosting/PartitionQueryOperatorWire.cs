using System.Globalization;
using System.Text.Json;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Graph;
using Google.Protobuf;
using Wire = EntityForge.Db.Distributed.Hosting.QueryWire;

namespace EntityForge.Db.Distributed.Hosting;

// Profile interpretation stays with the existing domain records and validators.
// Protobuf owns the transport representation; generated code is never edited.
internal static class PartitionQueryOperatorWire
{
    private const uint Format = 4;
    private const uint Vertices = 1, Edges = 2, Adjacency = 4, Bindings = 8, Bags = 16;
    private const uint ScalarRows = 32;
    private const uint Supported = Vertices | Edges | Adjacency | Bindings | Bags | ScalarRows;

    internal sealed record Control(DatabasePartitionIdentity Target, string Authority, Guid Operation,
        PartitionReadCut Cut, bool Valid, bool Completed, PartitionQueryPendingCompletion? Pending);

    internal static byte[] Encode(Control control, PartitionQueryJobResult result)
    {
        var reply = new Wire.Reply
        {
            Format = Format,
            Control = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(control, PartitionParticipantWire.Json))
        };
        if (result.Value is { } value)
        {
            if (value.Native is not PartitionQueryExchangePage page)
            {
                throw new InvalidDataException("Typed operator returned a non-page value.");
            }
            // Existing logical limits remain independent of the binary wire size.
            _ = value.QueryPageBytes ?? PartitionProjectionValue.Measure(page, PartitionQueryLimits.ExchangePageBytes, PartitionQueryJson.Options);
            reply.Page = value.OperatorWirePage ?? EncodePage(page);
        }
        if (reply.CalculateSize() > PartitionParticipantWire.MaximumResponseBytes)
        {
            throw new InvalidDataException("Typed operator response exceeds its bound.");
        }

        return reply.ToByteArray();
    }

    internal static (Control Control, PartitionQueryJobResult Result) Decode(byte[] bytes)
    {
        if (bytes.Length > PartitionParticipantWire.MaximumResponseBytes)
        {
            throw new InvalidDataException("Operator response exceeds its bound.");
        }

        var reply = Wire.Reply.Parser.ParseFrom(bytes);
        if (reply.Format != Format || reply.Control.IsEmpty)
        {
            throw new InvalidDataException("Unsupported operator framing.");
        }

        var control = JsonSerializer.Deserialize<Control>(reply.Control.Span, PartitionParticipantWire.Json)
            ?? throw new InvalidDataException("Missing operator control.");
        PartitionProjectionValue? value = null;
        if (reply.Page is { } page)
        {
            var decoded = DecodePage(page);
            long pageBytes = PartitionProjectionValue.Measure(decoded, PartitionQueryLimits.ExchangePageBytes, PartitionQueryJson.Options);
            value = new(decoded, PartitionQueryJson.Options)
            {
                QueryPageBytes = pageBytes
            };
        }
        return (control, new(control.Valid, value, control.Completed, control.Pending));
    }

    internal static Wire.Page EncodePage(PartitionQueryExchangePage page)
    {
        var relation = page.Relation;
        var result = new Wire.Page();
        if (page.NextOffset is { } next)
        {
            result.NextOffset = next;
        }

        if (page.Result is { } complete)
        {
            result.CompleteResult = ByteString.CopyFrom(complete.Encode(PartitionQueryLimits.ExchangePageBytes));
        }

        if (relation.Vertices is { } vertices)
        {
            result.GraphPresence |= Vertices;
            result.Vertices.Add(vertices.Select(v =>
        {
            var item = new Wire.Vertex { Id = Id(v.Id.Value) };
            item.Labels.Add(v.Labels);
            item.Properties.Add(Properties(v.Properties));
            return item;
        }));
        }
        if (relation.ScalarRows is { } rows)
        {
            if (relation.Vertices is not null || relation.GraphBindings is not null)
            {
                throw new InvalidDataException("Scalar and entity row shapes cannot mix.");
            }

            result.GraphPresence |= Vertices | ScalarRows;
            result.Vertices.Add(rows.Select(row =>
            {
                var item = new Wire.Vertex { Id = Id(row.Id.Value) };
                item.Properties.Add(Properties(row.Values));
                return item;
            }));
        }
        if (relation.Edges is { } edges)
        {
            result.GraphPresence |= Edges;
            result.Edges.Add(edges.Select(e =>
        {
            var item = Edge(e.Id.Value, e.From.Value, e.To.Value, e.Label);
            item.Properties.Add(Properties(e.Properties));
            return item;
        }));
        }
        if (relation.Adjacency is { } adjacency)
        {
            result.GraphPresence |= Adjacency;
            result.Adjacency.Add(adjacency.Select(e => Edge(e.Id.Value, e.From.Value, e.To.Value, e.Label)));
        }
        if (relation.GraphBindings is { } bindings)
        {
            result.GraphPresence |= Bindings;
            result.Bindings.Add(bindings.Select(Id));
        }
        if (relation.PropertyBags is { } bags)
        {
            result.GraphPresence |= Bags;
            result.Bags.Add(bags.Select(b =>
        {
            var item = new Wire.Bag { Id = Id(b.Identity), Edge = b.Edge };
            item.Properties.Add(Properties(b.Properties));
            return item;
        }));
        }
        if (relation.Quads is not null || relation.Fields is not null || relation.Relationships is not null || relation.Catalog is not null ||
            relation.RdfBindings is not null || relation.SemanticRows is not null || relation.SemanticEvidence is not null || relation.ProfileFallback)
        {
            result.ProfileRelation = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(relation with
            {
                Vertices = null,
                Edges = null,
                Adjacency = null,
                GraphBindings = null,
                PropertyBags = null,
                ScalarRows = null,
                Continuations = null
            }, PartitionQueryJson.Options));
        }

        if (relation.Continuations is { } continuations)
        {
            foreach (var continuation in continuations)
            {
                if (continuation.Relation.Continuations is not null)
                {
                    throw new InvalidDataException("Nested continuation.");
                }

                var item = new Wire.Continuation { Stage = continuation.Stage, Page = EncodePage(new(continuation.Relation, null)) };
                item.Identities.Add(continuation.Identities.Select(Id));
                item.Dependencies.Add(continuation.Dependencies.Select(Id));
                result.Continuations.Add(item);
            }
        }

        return result;
    }

    internal static PartitionQueryExchangePage DecodePage(Wire.Page page)
    {
        if ((page.GraphPresence & ~Supported) != 0 || page.HasNextOffset && (page.NextOffset < 1 || page.NextOffset > PartitionQueryLimits.MaximumFacts))
        {
            throw new InvalidDataException("Invalid operator page flags or continuation.");
        }

        int facts = checked(page.Vertices.Count + page.Edges.Count + page.Adjacency.Count + page.Bindings.Count + page.Bags.Count);
        if (facts > PartitionQueryLimits.ExchangePageFacts)
        {
            throw new InvalidDataException("Operator page exceeds its fact bound.");
        }

        RequirePresence(page, Vertices, page.Vertices.Count);
        RequirePresence(page, Edges, page.Edges.Count);
        RequirePresence(page, Adjacency, page.Adjacency.Count);
        RequirePresence(page, Bindings, page.Bindings.Count);
        RequirePresence(page, Bags, page.Bags.Count);
        var relation = page.ProfileRelation.IsEmpty ? new PartitionQueryRelation() :
            JsonSerializer.Deserialize<PartitionQueryRelation>(page.ProfileRelation.Span, PartitionQueryJson.Options)
                ?? throw new InvalidDataException("Missing profile relation.");
        if (Has(page, ScalarRows) && (!Has(page, Vertices) || Has(page, Bindings)))
        {
            throw new InvalidDataException("Invalid scalar row shape.");
        }

        if (relation.Vertices is not null || relation.Edges is not null || relation.Adjacency is not null || relation.GraphBindings is not null || relation.PropertyBags is not null || relation.ScalarRows is not null || relation.Continuations is not null)
        {
            throw new InvalidDataException("Profile section carries Graph data.");
        }

        relation = relation with
        {
            Vertices = Has(page, Vertices) && !Has(page, ScalarRows) ? page.Vertices.Select(v =>
            {
                var labels = v.Labels.ToHashSet(StringComparer.Ordinal);
                if (labels.Count != v.Labels.Count)
                {
                    throw new InvalidDataException("Duplicate vertex label.");
                }

                return new GraphVertex(new(ReadId(v.Id)), labels, ReadProperties(v.Properties));
            }).ToArray() : null,
            ScalarRows = Has(page, ScalarRows) ? page.Vertices.Select(v =>
            {
                if (v.Labels.Count != 0)
                {
                    throw new InvalidDataException("Scalar row carries entity labels.");
                }

                return new GraphFragmentRow(new(ReadId(v.Id)), ReadProperties(v.Properties));
            }).ToArray() : null,
            Edges = Has(page, Edges) ? page.Edges.Select(e => new GraphEdge(new(ReadId(e.Id)), new(ReadId(e.From)), new(ReadId(e.To)), e.Label, ReadProperties(e.Properties))).ToArray() : null,
            Adjacency = Has(page, Adjacency) ? page.Adjacency.Select(e =>
            {
                if (e.Properties.Count != 0 || string.IsNullOrWhiteSpace(e.Label))
                {
                    throw new InvalidDataException("Invalid adjacency.");
                }

                return new GraphEdgeReference(new(ReadId(e.Id)), new(ReadId(e.From)), new(ReadId(e.To)), e.Label);
            }).ToArray() : null,
            GraphBindings = Has(page, Bindings) ? page.Bindings.Select(ReadId).ToArray() : null,
            PropertyBags = Has(page, Bags) ? page.Bags.Select(b => new PartitionTopologyProperties.Bag(b.Edge, ReadId(b.Id), ReadProperties(b.Properties))).ToArray() : null
        };
        if (page.Continuations.Count > PartitionQueryLimits.ExchangePageFacts)
        {
            throw new InvalidDataException("Too many continuations.");
        }

        relation = relation with
        {
            Continuations = page.Continuations.Count == 0 ? null : page.Continuations.Select(c =>
            {
                if (c.Stage < 1 || c.Identities.Count == 0 || c.Page is null || c.Page.Continuations.Count != 0 ||
                    c.Page.HasNextOffset || !c.Page.CompleteResult.IsEmpty)
                {
                    throw new InvalidDataException("Invalid continuation page.");
                }

                return new PartitionQueryContinuation(c.Stage, c.Identities.Select(ReadId).ToArray(), c.Dependencies.Select(ReadId).ToArray(), DecodePage(c.Page).Relation);
            }).ToArray()
        };
        if (PartitionQueryData.FactCount(relation) > PartitionQueryLimits.ExchangePageFacts)
        {
            throw new InvalidDataException("Profile page exceeds its fact bound.");
        }

        PartitionProjectionValue? complete = page.CompleteResult.IsEmpty ? null : new PartitionProjectionValue(JsonSerializer.Deserialize<JsonElement>(page.CompleteResult.Span));
        return new(relation, page.HasNextOffset ? page.NextOffset : null, complete);
    }

    private static bool Has(Wire.Page page, uint mask) => (page.GraphPresence & mask) != 0;

    private static void RequirePresence(Wire.Page page, uint mask, int count)
    {
        if (count != 0 && !Has(page, mask))
        {
            throw new InvalidDataException("Unselected operator data.");
        }
    }

    internal static ByteString Id(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        return ByteString.CopyFrom(bytes);
    }

    private static Guid ReadId(ByteString bytes) => bytes.Length == 16 && new Guid(bytes.Span) is var id && id != Guid.Empty ? id :
        throw new InvalidDataException("Invalid Graph identity.");

    private static Wire.Edge Edge(Guid id, Guid from, Guid to, string label) => new() { Id = Id(id), From = Id(from), To = Id(to), Label = label };

    private static IEnumerable<Wire.Property> Properties(IReadOnlyDictionary<string, DatabaseValue> properties) => properties.Select(p =>
        new Wire.Property { Name = p.Key, Value = EncodeValue(p.Value) });

    private static readonly IReadOnlyDictionary<string, DatabaseValue> EmptyProperties = System.Collections.Frozen.FrozenDictionary<string, DatabaseValue>.Empty;

    private static IReadOnlyDictionary<string, DatabaseValue> ReadProperties(IEnumerable<Wire.Property> properties)
    {
        if (properties is ICollection<Wire.Property> { Count: 0 })
        {
            return EmptyProperties;
        }

        var result = new Dictionary<string, DatabaseValue>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            if (string.IsNullOrWhiteSpace(property.Name) || property.Value is null || !result.TryAdd(property.Name, DecodeValue(property.Value)))
            {
                throw new InvalidDataException("Invalid or duplicate Graph property.");
            }
        }

        return result;
    }

    private static Wire.Value EncodeValue(DatabaseValue value) => value switch
    {
        DatabaseValue.String v => new() { Text = v.Value },
        DatabaseValue.Int64 v => new() { Integer = v.Value },
        DatabaseValue.Double v => new() { Real = v.Value },
        DatabaseValue.Boolean v => new() { Boolean = v.Value },
        DatabaseValue.DateTimeOffset v => new() { Date = v.Value.ToString("O", CultureInfo.InvariantCulture) },
        DatabaseValue.Date v => new() { CalendarDate = v.Value.EpochDay },
        DatabaseValue.Point v => EncodePoint(v.Value),
        DatabaseValue.Uuid v => new() { Uuid = Id(v.Value) },
        DatabaseValue.Decimal v => new() { DecimalValue = v.Value.ToString(CultureInfo.InvariantCulture) },
        DatabaseValue.ZonedDateTime v => new() { ZonedDatetime = new Wire.ZonedDateTime { EpochDay = v.Value.DateTime.Date.EpochDay, NanosecondsOfDay = v.Value.DateTime.Time.NanosecondsOfDay, OffsetSeconds = v.Value.OffsetSeconds, ZoneId = v.Value.ZoneId ?? String.Empty } },
        DatabaseValue.ZonedTime v => new() { ZonedTime = new Wire.ZonedTime { NanosecondsOfDay = v.Value.Time.NanosecondsOfDay, OffsetSeconds = v.Value.OffsetSeconds } },
        DatabaseValue.LocalTime v => new() { LocalTimeNanoseconds = v.Value.NanosecondsOfDay },
        DatabaseValue.LocalDateTime v => new() { LocalDatetime = new Wire.LocalDateTime { EpochDay = v.Value.Date.EpochDay, NanosecondsOfDay = v.Value.Time.NanosecondsOfDay } },
        DatabaseValue.Duration v => new() { Duration = new Wire.CalendarDuration { Months = v.Value.Months, Days = v.Value.Days, Seconds = v.Value.Seconds, Nanoseconds = v.Value.Nanoseconds, Qualifier = (uint)v.Qualifier } },
        DatabaseValue.Uri v => new() { Uri = v.Value.AbsoluteUri },
        DatabaseValue.Binary v => new() { Binary = ByteString.CopyFrom(v.Value.Span) },
        DatabaseValue.Json v => new() { Json = v.Utf8Json },
        DatabaseValue.Array v => new() { Array = new Wire.ScalarArray { Values = { v.Value.Select(EncodeValue) } } },
        _ => throw new InvalidDataException("Unsupported Graph value.")
    };

    private static DatabaseValue DecodeValue(Wire.Value value) => value.ValueCase switch
    {
        Wire.Value.ValueOneofCase.Text => new DatabaseValue.String(value.Text),
        Wire.Value.ValueOneofCase.Integer => new DatabaseValue.Int64(value.Integer),
        Wire.Value.ValueOneofCase.Real => new DatabaseValue.Double(value.Real),
        Wire.Value.ValueOneofCase.Boolean => new DatabaseValue.Boolean(value.Boolean),
        Wire.Value.ValueOneofCase.Date => new DatabaseValue.DateTimeOffset(DateTimeOffset.ParseExact(value.Date, "O", CultureInfo.InvariantCulture)),
        Wire.Value.ValueOneofCase.CalendarDate => new DatabaseValue.Date(EntityForge.Values.CalendarDate.FromEpochDay(value.CalendarDate)),
        Wire.Value.ValueOneofCase.Point => new DatabaseValue.Point(new(value.Point.Srid, value.Point.X, value.Point.Y, value.Point.HasZ ? value.Point.Z : null)),
        Wire.Value.ValueOneofCase.Uuid => value.Uuid.Length == 16 ? new DatabaseValue.Uuid(new Guid(value.Uuid.Span)) : throw new InvalidDataException("A UUID requires sixteen bytes."),
        Wire.Value.ValueOneofCase.DecimalValue => new DatabaseValue.Decimal(Decimal.Parse(value.DecimalValue, CultureInfo.InvariantCulture)),
        Wire.Value.ValueOneofCase.ZonedDatetime => new DatabaseValue.ZonedDateTime(new(new(EntityForge.Values.CalendarDate.FromEpochDay(value.ZonedDatetime.EpochDay), new(value.ZonedDatetime.NanosecondsOfDay)), value.ZonedDatetime.OffsetSeconds, value.ZonedDatetime.ZoneId)),
        Wire.Value.ValueOneofCase.ZonedTime => new DatabaseValue.ZonedTime(new(new(value.ZonedTime.NanosecondsOfDay), value.ZonedTime.OffsetSeconds)),
        Wire.Value.ValueOneofCase.LocalTimeNanoseconds => new DatabaseValue.LocalTime(new(value.LocalTimeNanoseconds)),
        Wire.Value.ValueOneofCase.LocalDatetime => new DatabaseValue.LocalDateTime(new(EntityForge.Values.CalendarDate.FromEpochDay(value.LocalDatetime.EpochDay), new(value.LocalDatetime.NanosecondsOfDay))),
        Wire.Value.ValueOneofCase.Duration => DecodeDuration(value.Duration),
        Wire.Value.ValueOneofCase.Uri => new DatabaseValue.Uri(new Uri(value.Uri, UriKind.Absolute)),
        Wire.Value.ValueOneofCase.Binary => new DatabaseValue.Binary(value.Binary.Memory),
        Wire.Value.ValueOneofCase.Json => new DatabaseValue.Json(value.Json),
        Wire.Value.ValueOneofCase.Array => DecodeArray(value.Array),
        _ => throw new InvalidDataException("Missing Graph value.")
    };

    private static DatabaseValue.Duration DecodeDuration(Wire.CalendarDuration value)
    {
        if (value.Qualifier > (uint)DatabaseDurationQualifier.DayToSecond)
        {
            throw new InvalidDataException("Invalid temporal duration qualifier.");
        }
        return new(new(value.Months, value.Days, value.Seconds, value.Nanoseconds), (DatabaseDurationQualifier)value.Qualifier);
    }

    private static Wire.Value EncodePoint(EntityForge.Values.SpatialPoint value)
    {
        var point = new Wire.SpatialPoint { Srid = value.Srid, X = value.X, Y = value.Y };
        if (value.Z is { } z)
        {
            point.Z = z;
        }
        return new()
        {
            Point = point
        };
    }

    private static DatabaseValue.Array DecodeArray(Wire.ScalarArray value)
    {
        if (value.Values.Count > 10_000 || value.Values.Any(item => item.ValueCase is not
            (Wire.Value.ValueOneofCase.Text or Wire.Value.ValueOneofCase.Integer or Wire.Value.ValueOneofCase.Real or Wire.Value.ValueOneofCase.Boolean or Wire.Value.ValueOneofCase.CalendarDate or Wire.Value.ValueOneofCase.Duration or Wire.Value.ValueOneofCase.Point or Wire.Value.ValueOneofCase.LocalTimeNanoseconds or Wire.Value.ValueOneofCase.LocalDatetime or Wire.Value.ValueOneofCase.ZonedTime or Wire.Value.ValueOneofCase.ZonedDatetime or Wire.Value.ValueOneofCase.Uuid or Wire.Value.ValueOneofCase.DecimalValue)))
        {
            throw new InvalidDataException("Invalid stored array shape or elements.");
        }
        return new(value.Values.Select(DecodeValue).ToArray());
    }
}
