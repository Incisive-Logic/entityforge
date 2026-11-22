using System.Globalization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using EntityForge.Semantics;

namespace EntityForge.Semantics.Protobuf;

/// <summary>Converts semantic domain contracts to and from their generated Protocol Buffers transport messages.</summary>
public static class SemanticChangeProtobufConverter
{
    /// <summary>Converts a semantic domain value into its transport message representation.</summary>
    public static SemanticChangeSessionStartMessage ToMessage(SemanticChangeSessionStart value)
    {
        var message = new SemanticChangeSessionStartMessage
        {
            SessionId = value.SessionId.ToString("D"),
            Kind = (SemanticChangeSessionKindMessage)(int)value.Kind,
            Source = new SemanticChangeSessionSourceMessage
            {
                SourceSystem = value.Source.SourceSystem.AbsoluteUri,
                SourcePartition = value.Source.SourcePartition.AbsoluteUri,
                SourceVersion = value.Source.SourceVersion,
                GenerationId = value.Source.GenerationId.ToString("D"),
                SourcePosition = value.Source.SourcePosition
            },
            ProducerActivityId = value.ProducerActivityId.ToString("D"),
            StartedAt = Timestamp.FromDateTimeOffset(value.StartedAt)
        };
        if (value.CorrelationId is not null)
        {
            message.CorrelationId = value.CorrelationId;
        }

        if (value.ProgressPrecondition is not null)
        {
            message.ProgressPrecondition = ToMessage(value.ProgressPrecondition);
        }

        return message;
    }

    /// <summary>Converts a transport message into the semantic domain contract, validating its representation.</summary>
    public static SemanticChangeSessionStart ToDomain(this SemanticChangeSessionStartMessage value) =>
        new(
            Guid.Parse(value.SessionId),
            (SemanticChangeSessionKind)(int)value.Kind,
            new SemanticChangeSessionSource(
                new Uri(value.Source.SourceSystem),
                new Uri(value.Source.SourcePartition),
                value.Source.SourceVersion,
                Guid.Parse(value.Source.GenerationId),
                value.Source.SourcePosition),
            Guid.Parse(value.ProducerActivityId),
            value.StartedAt.ToDateTimeOffset(),
            value.HasCorrelationId ? value.CorrelationId : null,
            value.ProgressPrecondition?.ToDomain());

    private static SemanticSourceProgressPreconditionMessage ToMessage(
        SemanticSourceProgressPrecondition value)
    {
        var message = new SemanticSourceProgressPreconditionMessage
        {
            Expectation = (SemanticSourceProgressExpectationMessage)(int)value.Expectation
        };
        if (value.Predecessor is not null)
        {
            message.Predecessor = new SemanticSourcePositionMessage
            {
                GenerationId = value.Predecessor.GenerationId.ToString("D"),
                SourceVersion = value.Predecessor.SourceVersion,
                SourcePosition = value.Predecessor.SourcePosition
            };
        }

        return message;
    }

    private static SemanticSourceProgressPrecondition ToDomain(
        this SemanticSourceProgressPreconditionMessage value) => new(
            (SemanticSourceProgressExpectation)(int)value.Expectation,
            value.Predecessor is null ? null : new SemanticSourcePosition(
                Guid.Parse(value.Predecessor.GenerationId), value.Predecessor.SourceVersion,
                value.Predecessor.SourcePosition));

    /// <summary>Converts a semantic domain value into its transport message representation.</summary>
    public static SemanticChangeSessionPageMessage ToMessage(SemanticChangeSessionPage value)
    {
        var message = new SemanticChangeSessionPageMessage
        {
            SessionId = value.SessionId.ToString("D"),
            PageNumber = value.PageNumber
        };
        message.Changes.AddRange(value.Changes.Select(ToMessage));
        return message;
    }

    /// <summary>Converts a transport message into the semantic domain contract, validating its representation.</summary>
    public static SemanticChangeSessionPage ToDomain(this SemanticChangeSessionPageMessage value) =>
        new(Guid.Parse(value.SessionId), value.PageNumber, value.Changes.Select(ToDomain));

    /// <summary>Converts a semantic domain value into its transport message representation.</summary>
    public static SemanticChangeSessionCloseMessage ToMessage(SemanticChangeSessionClose value)
    {
        var message = new SemanticChangeSessionCloseMessage
        {
            SessionId = value.SessionId.ToString("D"),
            PageCount = value.PageCount,
            ChangeCount = value.ChangeCount,
            ContentHash = value.ContentHash,
            ClosedAt = Timestamp.FromDateTimeOffset(value.ClosedAt)
        };
        if (value.OperationHash is not null)
        {
            message.OperationHash = value.OperationHash;
        }

        return message;
    }

    /// <summary>Converts a transport message into the semantic domain contract, validating its representation.</summary>
    public static SemanticChangeSessionClose ToDomain(this SemanticChangeSessionCloseMessage value) =>
        new(
            Guid.Parse(value.SessionId),
            value.PageCount,
            value.ChangeCount,
            value.ContentHash,
            value.ClosedAt.ToDateTimeOffset(),
            value.HasOperationHash ? value.OperationHash : null);

    /// <summary>Converts a semantic domain value into its transport message representation.</summary>
    public static SemanticChangeSessionAbortMessage ToMessage(SemanticChangeSessionAbort value) => new()
    {
        SessionId = value.SessionId.ToString("D"),
        ErrorCode = value.ErrorCode,
        Error = value.Error,
        AbortedAt = Timestamp.FromDateTimeOffset(value.AbortedAt),
        FailedPageNumber = value.FailedPageNumber ?? 0
    };

    /// <summary>Converts a transport message into the semantic domain contract, validating its representation.</summary>
    public static SemanticChangeSessionAbort ToDomain(this SemanticChangeSessionAbortMessage value) =>
        new(
            Guid.Parse(value.SessionId),
            value.ErrorCode,
            value.Error,
            value.AbortedAt.ToDateTimeOffset(),
            value.HasFailedPageNumber ? value.FailedPageNumber : null);

    private static SemanticEntityChangeMessage ToMessage(SemanticEntityChange value)
    {
        var message = new SemanticEntityChangeMessage
        {
            ChangeId = value.ChangeId.ToString("D"),
            Kind = (SemanticEntityChangeKindMessage)(int)value.Kind,
            Subject = ToMessage(value.Subject),
            Provenance = ToMessage(value.Provenance)
        };
        message.Fields.AddRange(value.Fields.Select(ToMessage));
        message.Relationships.AddRange(value.Relationships.Select(ToMessage));
        return message;
    }

    private static SemanticEntityChange ToDomain(SemanticEntityChangeMessage value) =>
        new(
            Guid.Parse(value.ChangeId),
            (SemanticEntityChangeKind)(int)value.Kind,
            ToDomain(value.Subject),
            ToDomain(value.Provenance),
            value.Fields.Select(ToDomain),
            value.Relationships.Select(ToDomain));

    internal static SemanticEntityIdentityMessage ToMessage(SemanticEntityIdentity value) => new()
    {
        Concept = ToMessage(value.Concept),
        Scheme = ToMessage(value.Scheme),
        Value = ToMessage(value.Value)
    };

    private static SemanticIdentitySchemeMessage ToMessage(SemanticIdentityScheme value)
    {
        var message = new SemanticIdentitySchemeMessage
        {
            Identifier = value.Identifier.AbsoluteUri,
            Name = value.Name,
            ValueKind = (SemanticValueKindMessage)(int)value.ValueKind
        };
        if (value.Description is not null)
        {
            message.Description = value.Description;
        }

        return message;
    }

    internal static SemanticEntityIdentity ToDomain(SemanticEntityIdentityMessage value) =>
        new(
            ToDomain(value.Concept),
            new SemanticIdentityScheme(
                new Uri(value.Scheme.Identifier),
                value.Scheme.Name,
                (SemanticValueKind)(int)value.Scheme.ValueKind,
                value.Scheme.HasDescription ? value.Scheme.Description : null),
            ToDomain(value.Value));

    private static SemanticTermMessage ToMessage(SemanticTerm value) => new()
    {
        Identifier = value.Identifier.AbsoluteUri,
        Role = (SemanticTermRoleMessage)(int)value.Role
    };

    private static SemanticTerm ToDomain(SemanticTermMessage value) =>
        new(new Uri(value.Identifier), (SemanticTermRole)(int)value.Role);

    private static SemanticFieldChangeMessage ToMessage(SemanticFieldChange value)
    {
        var message = new SemanticFieldChangeMessage
        {
            Field = ToMessage(value.Field),
            Kind = (SemanticFieldChangeKindMessage)(int)value.Kind
        };
        if (value.Value is not null)
        {
            message.Value = ToMessage(value.Value);
        }
        return message;
    }

    private static SemanticFieldChange ToDomain(SemanticFieldChangeMessage value) =>
        new(
            ToDomain(value.Field),
            (SemanticFieldChangeKind)(int)value.Kind,
            value.Value is not null ? ToDomain(value.Value) : null);

    private static SemanticRelationshipChangeMessage ToMessage(SemanticRelationshipChange value)
    {
        var message = new SemanticRelationshipChangeMessage
        {
            Relationship = ToMessage(value.Relationship),
            Kind = (SemanticRelationshipChangeKindMessage)(int)value.Kind
        };
        if (value.Target is not null)
        {
            message.Target = ToMessage(value.Target);
        }

        return message;
    }

    private static SemanticRelationshipChange ToDomain(SemanticRelationshipChangeMessage value) =>
        new(
            ToDomain(value.Relationship),
            (SemanticRelationshipChangeKind)(int)value.Kind,
            value.Target is not null ? ToDomain(value.Target) : null);

    internal static SemanticProvenanceMessage ToMessage(SemanticProvenance value)
    {
        var message = new SemanticProvenanceMessage
        {
            SourceSystem = value.SourceSystem.AbsoluteUri,
            SourceRecord = value.SourceRecord,
            ObservedAt = Timestamp.FromDateTimeOffset(value.ObservedAt),
            TargetVocabulary = ToMessage(value.TargetVocabulary),
            GeneratedAt = Timestamp.FromDateTimeOffset(value.GeneratedAt)
        };
        if (value.CorrelationId is not null)
        {
            message.CorrelationId = value.CorrelationId;
        }

        if (value.SourceVocabulary is not null)
        {
            message.SourceVocabulary = ToMessage(value.SourceVocabulary);
        }

        message.Mappings.AddRange(value.Mappings.Select(ToMessage));
        if (value.ValidFrom is not null)
        {
            message.ValidFrom = Timestamp.FromDateTimeOffset(value.ValidFrom.Value);
        }

        if (value.ValidTo is not null)
        {
            message.ValidTo = Timestamp.FromDateTimeOffset(value.ValidTo.Value);
        }

        if (value.SourceEntity is not null)
        {
            message.SourceEntity = new SemanticProvenanceEntityMessage
            {
                Identifier = value.SourceEntity.Identifier.AbsoluteUri
            };
            if (value.SourceEntity.Description is not null)
            {
                message.SourceEntity.Description = value.SourceEntity.Description;
            }
        }
        if (value.TransformActivity is not null)
        {
            message.TransformActivity = new SemanticProvenanceActivityMessage
            {
                Identifier = value.TransformActivity.Identifier.AbsoluteUri
            };
            if (value.TransformActivity.StartedAt is not null)
            {
                message.TransformActivity.StartedAt = Timestamp.FromDateTimeOffset(value.TransformActivity.StartedAt.Value);
            }

            if (value.TransformActivity.EndedAt is not null)
            {
                message.TransformActivity.EndedAt = Timestamp.FromDateTimeOffset(value.TransformActivity.EndedAt.Value);
            }
        }
        if (value.ResponsibleAgent is not null)
        {
            message.ResponsibleAgent = new SemanticProvenanceAgentMessage
            {
                Identifier = value.ResponsibleAgent.Identifier.AbsoluteUri
            };
            if (value.ResponsibleAgent.Name is not null)
            {
                message.ResponsibleAgent.Name = value.ResponsibleAgent.Name;
            }
        }
        if (value.TargetBinding is not null)
        {
            message.TargetBinding = new SemanticArtifactReferenceMessage
            {
                Identifier = value.TargetBinding.Identifier.AbsoluteUri,
                Version = value.TargetBinding.Version,
                ContentHash = value.TargetBinding.ContentHash!
            };
        }

        if (value.TransformTemplate is not null)
        {
            message.TransformTemplate = new SemanticArtifactReferenceMessage
            {
                Identifier = value.TransformTemplate.Identifier.AbsoluteUri,
                Version = value.TransformTemplate.Version
            };
            if (value.TransformTemplate.ContentHash is not null)
            {
                message.TransformTemplate.ContentHash = value.TransformTemplate.ContentHash;
            }
        }
        return message;
    }

    private static SemanticProvenance ToDomain(SemanticProvenanceMessage value) =>
        new(
            new Uri(value.SourceSystem),
            value.SourceRecord,
            value.ObservedAt.ToDateTimeOffset(),
            ToDomain(value.TargetVocabulary),
            value.Mappings.Select(ToDomain),
            value.ValidFrom?.ToDateTimeOffset(),
            value.ValidTo?.ToDateTimeOffset(),
            value.HasCorrelationId ? value.CorrelationId : null,
            value.SourceEntity is not null
                ? new SemanticProvenanceEntity(
                    new Uri(value.SourceEntity.Identifier),
                    value.SourceEntity.HasDescription ? value.SourceEntity.Description : null)
                : null,
            value.TransformActivity is not null
                ? new SemanticProvenanceActivity(
                    new Uri(value.TransformActivity.Identifier),
                    value.TransformActivity.StartedAt?.ToDateTimeOffset(),
                    value.TransformActivity.EndedAt?.ToDateTimeOffset())
                : null,
            value.ResponsibleAgent is not null
                ? new SemanticProvenanceAgent(
                    new Uri(value.ResponsibleAgent.Identifier),
                    value.ResponsibleAgent.HasName ? value.ResponsibleAgent.Name : null)
                : null,
            value.TransformTemplate is not null
                ? new SemanticArtifactReference(
                    new Uri(value.TransformTemplate.Identifier),
                    value.TransformTemplate.Version,
                    value.TransformTemplate.HasContentHash ? value.TransformTemplate.ContentHash : null)
                : null,
            value.GeneratedAt.ToDateTimeOffset(),
            value.SourceVocabulary is not null ? ToDomain(value.SourceVocabulary) : null,
            value.TargetBinding is not null ? new SemanticArtifactReference(
                new Uri(value.TargetBinding.Identifier), value.TargetBinding.Version,
                value.TargetBinding.HasContentHash ? value.TargetBinding.ContentHash : null) : null);

    private static SemanticModelReferenceMessage ToMessage(SemanticModelReference value) => new()
    {
        Identifier = value.Identifier.AbsoluteUri,
        Version = value.Version,
        Kind = (SemanticModelKindMessage)(int)value.Kind
    };

    private static SemanticModelReference ToDomain(SemanticModelReferenceMessage value) =>
        new(new Uri(value.Identifier), value.Version, (SemanticModelKind)(int)value.Kind);

    private static SemanticValueMessage ToMessage(SemanticValue value)
    {
        var message = new SemanticValueMessage { Kind = (SemanticValueKindMessage)(int)value.Kind };
        switch (value)
        {
            case NullSemanticValue:
                message.NullValue = true;
                break;
            case BooleanSemanticValue item:
                message.BooleanValue = item.Value;
                break;
            case IntegerSemanticValue item:
                message.IntegerValue = item.Value;
                break;
            case FloatingPointSemanticValue item:
                message.FloatingPointValue = item.Value;
                break;
            case DecimalSemanticValue item:
                message.DecimalValue = item.Value.ToString(CultureInfo.InvariantCulture);
                break;
            case StringSemanticValue item:
                message.StringValue = item.Value;
                break;
            case LanguageStringSemanticValue item:
                message.LanguageStringValue = new LanguageStringValueMessage { Value = item.Value, Language = item.Language };
                break;
            case DateSemanticValue item:
                message.DateValue = item.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            case TimeSemanticValue item:
                message.TimeValue = item.Value.ToString("O", CultureInfo.InvariantCulture);
                break;
            case DateTimeSemanticValue item:
                message.DateTimeValue = Timestamp.FromDateTimeOffset(item.Value);
                break;
            case DurationSemanticValue item:
                message.DurationValue = item.Value.ToString("c", CultureInfo.InvariantCulture);
                break;
            case BinarySemanticValue item:
                message.BinaryValue = ByteString.CopyFrom(item.Value.Span);
                break;
            case AbsoluteUriSemanticValue item:
                message.AbsoluteUriValue = item.Value.AbsoluteUri;
                break;
            case GuidSemanticValue item:
                message.GuidValue = item.Value.ToString("D");
                break;
            case JsonSemanticValue item:
                message.JsonValue = item.Utf8Json;
                break;
            case TypedLiteralSemanticValue item:
                message.TypedLiteralValue = new TypedLiteralValueMessage
                {
                    LexicalValue = item.LexicalValue,
                    Datatype = item.Datatype.AbsoluteUri
                };
                break;
            default:
                throw new NotSupportedException($"Semantic value '{value.GetType().Name}' is not supported.");
        }
        return message;
    }

    private static SemanticValue ToDomain(SemanticValueMessage value)
    {
        if (!HasMatchingPayload(value))
        {
            throw new InvalidDataException(
                $"Semantic value kind '{value.Kind}' does not match payload '{value.ValueCase}'.");
        }

        return value.Kind switch
        {
            SemanticValueKindMessage.SemanticValueKindNull => new NullSemanticValue(),
            SemanticValueKindMessage.SemanticValueKindBoolean => new BooleanSemanticValue(value.BooleanValue),
            SemanticValueKindMessage.SemanticValueKindInteger => new IntegerSemanticValue(value.IntegerValue),
            SemanticValueKindMessage.SemanticValueKindFloatingPoint => new FloatingPointSemanticValue(value.FloatingPointValue),
            SemanticValueKindMessage.SemanticValueKindDecimal => new DecimalSemanticValue(decimal.Parse(value.DecimalValue, CultureInfo.InvariantCulture)),
            SemanticValueKindMessage.SemanticValueKindString => new StringSemanticValue(value.StringValue),
            SemanticValueKindMessage.SemanticValueKindLanguageString => new LanguageStringSemanticValue(value.LanguageStringValue.Value, value.LanguageStringValue.Language),
            SemanticValueKindMessage.SemanticValueKindDate => new DateSemanticValue(DateOnly.ParseExact(value.DateValue, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
            SemanticValueKindMessage.SemanticValueKindTime => new TimeSemanticValue(TimeOnly.Parse(value.TimeValue, CultureInfo.InvariantCulture)),
            SemanticValueKindMessage.SemanticValueKindDateTime => new DateTimeSemanticValue(value.DateTimeValue.ToDateTimeOffset()),
            SemanticValueKindMessage.SemanticValueKindDuration => new DurationSemanticValue(TimeSpan.ParseExact(value.DurationValue, "c", CultureInfo.InvariantCulture)),
            SemanticValueKindMessage.SemanticValueKindBinary => new BinarySemanticValue(value.BinaryValue.Memory),
            SemanticValueKindMessage.SemanticValueKindAbsoluteUri => new AbsoluteUriSemanticValue(new Uri(value.AbsoluteUriValue)),
            SemanticValueKindMessage.SemanticValueKindGuid => new GuidSemanticValue(Guid.Parse(value.GuidValue)),
            SemanticValueKindMessage.SemanticValueKindJson => new JsonSemanticValue(value.JsonValue),
            SemanticValueKindMessage.SemanticValueKindTypedLiteral => new TypedLiteralSemanticValue(value.TypedLiteralValue.LexicalValue, new Uri(value.TypedLiteralValue.Datatype)),
            _ => throw new InvalidDataException($"Unknown semantic value kind '{value.Kind}'.")
        };
    }

    private static bool HasMatchingPayload(SemanticValueMessage value) => value.Kind switch
    {
        SemanticValueKindMessage.SemanticValueKindNull => value.ValueCase is SemanticValueMessage.ValueOneofCase.NullValue,
        SemanticValueKindMessage.SemanticValueKindBoolean => value.ValueCase is SemanticValueMessage.ValueOneofCase.BooleanValue,
        SemanticValueKindMessage.SemanticValueKindInteger => value.ValueCase is SemanticValueMessage.ValueOneofCase.IntegerValue,
        SemanticValueKindMessage.SemanticValueKindFloatingPoint => value.ValueCase is SemanticValueMessage.ValueOneofCase.FloatingPointValue,
        SemanticValueKindMessage.SemanticValueKindDecimal => value.ValueCase is SemanticValueMessage.ValueOneofCase.DecimalValue,
        SemanticValueKindMessage.SemanticValueKindString => value.ValueCase is SemanticValueMessage.ValueOneofCase.StringValue,
        SemanticValueKindMessage.SemanticValueKindLanguageString => value.ValueCase is SemanticValueMessage.ValueOneofCase.LanguageStringValue,
        SemanticValueKindMessage.SemanticValueKindDate => value.ValueCase is SemanticValueMessage.ValueOneofCase.DateValue,
        SemanticValueKindMessage.SemanticValueKindTime => value.ValueCase is SemanticValueMessage.ValueOneofCase.TimeValue,
        SemanticValueKindMessage.SemanticValueKindDateTime => value.ValueCase is SemanticValueMessage.ValueOneofCase.DateTimeValue,
        SemanticValueKindMessage.SemanticValueKindDuration => value.ValueCase is SemanticValueMessage.ValueOneofCase.DurationValue,
        SemanticValueKindMessage.SemanticValueKindBinary => value.ValueCase is SemanticValueMessage.ValueOneofCase.BinaryValue,
        SemanticValueKindMessage.SemanticValueKindAbsoluteUri => value.ValueCase is SemanticValueMessage.ValueOneofCase.AbsoluteUriValue,
        SemanticValueKindMessage.SemanticValueKindGuid => value.ValueCase is SemanticValueMessage.ValueOneofCase.GuidValue,
        SemanticValueKindMessage.SemanticValueKindJson => value.ValueCase is SemanticValueMessage.ValueOneofCase.JsonValue,
        SemanticValueKindMessage.SemanticValueKindTypedLiteral => value.ValueCase is SemanticValueMessage.ValueOneofCase.TypedLiteralValue,
        _ => false
    };
}
