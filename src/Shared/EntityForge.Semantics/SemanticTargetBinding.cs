using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EntityForge.Semantics;

/// <summary>Classifies one stable target slot without introducing CLR or source-expression concerns.</summary>
public enum SemanticTargetMemberKind
{
    /// <summary>Binds a scalar-valued semantic property.</summary>
    Property,

    /// <summary>Binds a relationship to an entity identified by a concept and identity scheme.</summary>
    Relationship
}

/// <summary>Describes how one stable logical slot is represented by the Semantic profile.</summary>
public sealed record SemanticTargetMemberBinding
{
    /// <summary>Creates and validates one logical slot's property or relationship binding.</summary>
    /// <param name="slot">Non-blank, case-sensitive slot identifier.</param>
    /// <param name="term">A property or relationship term.</param>
    /// <param name="valueKind">Required non-null scalar kind for a property; null for a relationship.</param>
    /// <param name="targetConcept">Required concept term for a relationship; null for a property.</param>
    /// <param name="targetIdentityScheme">Required target identity scheme for a relationship; null for a property.</param>
    /// <exception cref="ArgumentException">The slot or combination of term role and metadata is invalid.</exception>
    /// <exception cref="ArgumentNullException">A required term or relationship identity scheme is null.</exception>
    public SemanticTargetMemberBinding(
        string slot,
        SemanticTerm term,
        SemanticValueKind? valueKind = null,
        SemanticTerm? targetConcept = null,
        SemanticIdentityScheme? targetIdentityScheme = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        Slot = slot;
        Term = term ?? throw new ArgumentNullException(nameof(term));
        Kind = term.Role switch
        {
            SemanticTermRole.Property => SemanticTargetMemberKind.Property,
            SemanticTermRole.Relationship => SemanticTargetMemberKind.Relationship,
            _ => throw new ArgumentException("A target member must bind a property or relationship term.", nameof(term))
        };

        if (Kind is SemanticTargetMemberKind.Property)
        {
            if (valueKind is null or SemanticValueKind.Null)
            {
                throw new ArgumentException("A property target requires a non-null Semantic value kind.", nameof(valueKind));
            }

            if (targetConcept is not null || targetIdentityScheme is not null)
            {
                throw new ArgumentException("A property target cannot declare a relationship identity.");
            }
        }
        else
        {
            if (valueKind is not null)
            {
                throw new ArgumentException("A relationship target does not declare a scalar value kind.", nameof(valueKind));
            }

            if (targetConcept?.Role is not SemanticTermRole.Concept)
            {
                throw new ArgumentException("A relationship target requires a concept term.", nameof(targetConcept));
            }

            ArgumentNullException.ThrowIfNull(targetIdentityScheme);
        }

        ValueKind = valueKind;
        TargetConcept = targetConcept;
        TargetIdentityScheme = targetIdentityScheme;
    }

    /// <summary>Gets the case-sensitive logical slot identifier.</summary>
    public string Slot
    {
        get;
    }

    /// <summary>Gets the semantic property or relationship term.</summary>
    public SemanticTerm Term
    {
        get;
    }

    /// <summary>Gets the member category derived from the term role.</summary>
    public SemanticTargetMemberKind Kind
    {
        get;
    }

    /// <summary>Gets the required scalar kind for a property, or null for a relationship.</summary>
    public SemanticValueKind? ValueKind
    {
        get;
    }

    /// <summary>Gets the related entity concept, or null for a property.</summary>
    public SemanticTerm? TargetConcept
    {
        get;
    }

    /// <summary>Gets the related entity identity scheme, or null for a property.</summary>
    public SemanticIdentityScheme? TargetIdentityScheme
    {
        get;
    }
}

/// <summary>
/// Immutable, source-neutral interpretation of stable logical slots for one Semantic target.
/// It is shared by POCO and integration front ends and does not require a running Semantic database.
/// </summary>
public sealed class SemanticTargetBinding
{
    private const int FingerprintFormatVersion = 1;
    private readonly IReadOnlyDictionary<string, SemanticTargetMemberBinding> _membersBySlot;

    /// <summary>Creates an immutable binding and computes its deterministic metadata fingerprint.</summary>
    /// <param name="identifier">Absolute identifier of the binding definition.</param>
    /// <param name="version">Non-blank binding version.</param>
    /// <param name="targetVocabulary">Reference whose model kind is vocabulary.</param>
    /// <param name="concept">Concept term represented by this binding.</param>
    /// <param name="identityScheme">Identity scheme for bound entities.</param>
    /// <param name="members">Members to copy; slots must be ordinally unique and a term cannot occupy multiple writable slots.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">An identifier, version, role or member uniqueness constraint is invalid.</exception>
    public SemanticTargetBinding(
        Uri identifier,
        string version,
        SemanticModelReference targetVocabulary,
        SemanticTerm concept,
        SemanticIdentityScheme identityScheme,
        IEnumerable<SemanticTargetMemberBinding> members)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (!identifier.IsAbsoluteUri)
        {
            throw new ArgumentException("A target-binding identifier must be absolute.", nameof(identifier));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        TargetVocabulary = targetVocabulary ?? throw new ArgumentNullException(nameof(targetVocabulary));
        if (targetVocabulary.Kind is not SemanticModelKind.Vocabulary)
        {
            throw new ArgumentException("A Semantic target binding requires a vocabulary reference.", nameof(targetVocabulary));
        }

        Concept = concept ?? throw new ArgumentNullException(nameof(concept));
        if (concept.Role is not SemanticTermRole.Concept)
        {
            throw new ArgumentException("A Semantic target binding requires a concept term.", nameof(concept));
        }

        IdentityScheme = identityScheme ?? throw new ArgumentNullException(nameof(identityScheme));

        SemanticTargetMemberBinding[] copy = members?.ToArray() ?? throw new ArgumentNullException(nameof(members));
        if (copy.Any(member => member is null))
        {
            throw new ArgumentException("A target binding cannot contain a null member.", nameof(members));
        }

        if (copy.Select(member => member.Slot).Distinct(StringComparer.Ordinal).Count() != copy.Length)
        {
            throw new ArgumentException("A target binding requires unique stable slots.", nameof(members));
        }

        if (copy.GroupBy(member => (member.Kind, member.Term)).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("A Semantic term cannot be bound to more than one independently writable slot.", nameof(members));
        }

        Identifier = identifier;
        Version = version;
        Members = Array.AsReadOnly(copy.OrderBy(member => member.Slot, StringComparer.Ordinal).ToArray());
        _membersBySlot = Members.ToDictionary(member => member.Slot, StringComparer.Ordinal);
        Fingerprint = ComputeFingerprint();
    }

    /// <summary>Gets the absolute identifier of this binding definition.</summary>
    public Uri Identifier
    {
        get;
    }

    /// <summary>Gets the caller-supplied binding version.</summary>
    public string Version
    {
        get;
    }

    /// <summary>Gets the vocabulary reference used to interpret the binding.</summary>
    public SemanticModelReference TargetVocabulary
    {
        get;
    }

    /// <summary>Gets the concept represented by entities using this binding.</summary>
    public SemanticTerm Concept
    {
        get;
    }

    /// <summary>Gets the identity scheme for entities using this binding.</summary>
    public SemanticIdentityScheme IdentityScheme
    {
        get;
    }

    /// <summary>Gets the copied member bindings sorted by ordinal slot name.</summary>
    public IReadOnlyList<SemanticTargetMemberBinding> Members
    {
        get;
    }

    /// <summary>Gets the lowercase SHA-256 fingerprint of versioned binding metadata; this is not a hash of entity data.</summary>
    public string Fingerprint
    {
        get;
    }

    /// <summary>Resolves a required target-member slot in this binding.</summary>
    /// <param name="slot">The non-blank literal slot identifier.</param>
    /// <returns>The member binding for the slot.</returns>
    /// <exception cref="KeyNotFoundException">The slot is not declared by this binding.</exception>
    public SemanticTargetMemberBinding RequireMember(string slot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        return _membersBySlot.TryGetValue(slot, out SemanticTargetMemberBinding? member)
            ? member
            : throw new KeyNotFoundException($"Semantic target slot '{slot}' is not defined by binding '{Identifier}'.");
    }

    private string ComputeFingerprint()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(FingerprintFormatVersion);
            Write(writer, Identifier.AbsoluteUri);
            Write(writer, Version);
            Write(writer, TargetVocabulary.Identifier.AbsoluteUri);
            Write(writer, TargetVocabulary.Version);
            Write(writer, Concept.Identifier.AbsoluteUri);
            writer.Write((int)Concept.Role);
            Write(writer, IdentityScheme.Identifier.AbsoluteUri);
            writer.Write((int)IdentityScheme.ValueKind);
            writer.Write(Members.Count);
            foreach (SemanticTargetMemberBinding member in Members)
            {
                Write(writer, member.Slot);
                writer.Write((int)member.Kind);
                Write(writer, member.Term.Identifier.AbsoluteUri);
                writer.Write((int)member.Term.Role);
                writer.Write(member.ValueKind is not null);
                if (member.ValueKind is not null)
                {
                    writer.Write((int)member.ValueKind.Value);
                }

                Write(writer, member.TargetConcept?.Identifier.AbsoluteUri);
                Write(writer, member.TargetIdentityScheme?.Identifier.AbsoluteUri);
                writer.Write((int?)member.TargetIdentityScheme?.ValueKind ?? -1);
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void Write(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }
}

/// <summary>Performs the one strict CLR-to-Semantic conversion used by every application front end.</summary>
public static class SemanticValueConverter
{
    /// <summary>Converts a supported CLR scalar or existing Semantic value using the strict shared mapping.</summary>
    /// <param name="value">Non-null scalar value to convert.</param>
    /// <param name="requiredKind">Optional expected semantic kind; no coercion to a different kind is performed.</param>
    /// <returns>The corresponding typed Semantic value.</returns>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="NotSupportedException">The CLR type has no supported Semantic mapping.</exception>
    /// <exception cref="InvalidOperationException">The converted kind differs from requiredKind.</exception>
    public static SemanticValue FromClr(object value, SemanticValueKind? requiredKind = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        SemanticValue converted = value switch
        {
            SemanticValue semantic => semantic,
            bool item => new BooleanSemanticValue(item),
            byte or sbyte or short or ushort or int or uint or long =>
                new IntegerSemanticValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            float or double => new FloatingPointSemanticValue(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            decimal item => new DecimalSemanticValue(item),
            string item => new StringSemanticValue(item),
            DateOnly item => new DateSemanticValue(item),
            TimeOnly item => new TimeSemanticValue(item),
            DateTimeOffset item => new DateTimeSemanticValue(item),
            DateTime item => new DateTimeSemanticValue(new DateTimeOffset(item)),
            TimeSpan item => new DurationSemanticValue(item),
            byte[] item => new BinarySemanticValue(item),
            Uri item => new AbsoluteUriSemanticValue(item),
            Guid item => new GuidSemanticValue(item),
            _ => throw new NotSupportedException(
                $"CLR value type '{value.GetType().Name}' has no Semantic value mapping.")
        };
        if (requiredKind is not null && converted.Kind != requiredKind)
        {
            throw new InvalidOperationException(
                $"The value produces {converted.Kind}, but the target binding requires {requiredKind}.");
        }

        return converted;
    }

    /// <summary>Formats a Semantic value using its invariant lexical representation.</summary>
    /// <param name="value">The non-null typed Semantic value.</param>
    /// <returns>The value text; binary is base64 and a Semantic null produces an empty string. Datatype and language metadata remain separate.</returns>
    public static string ToLexical(SemanticValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            NullSemanticValue => string.Empty,
            BooleanSemanticValue item => item.Value ? "true" : "false",
            IntegerSemanticValue item => item.Value.ToString(CultureInfo.InvariantCulture),
            FloatingPointSemanticValue item => item.Value.ToString("R", CultureInfo.InvariantCulture),
            DecimalSemanticValue item => item.Value.ToString(CultureInfo.InvariantCulture),
            StringSemanticValue item => item.Value,
            LanguageStringSemanticValue item => item.Value,
            DateSemanticValue item => item.Value.ToString("O", CultureInfo.InvariantCulture),
            TimeSemanticValue item => item.Value.ToString("O", CultureInfo.InvariantCulture),
            DateTimeSemanticValue item => item.Value.ToString("O", CultureInfo.InvariantCulture),
            DurationSemanticValue item => item.Value.ToString("c", CultureInfo.InvariantCulture),
            AbsoluteUriSemanticValue item => item.Value.AbsoluteUri,
            GuidSemanticValue item => item.Value.ToString("D"),
            TypedLiteralSemanticValue item => item.LexicalValue,
            JsonSemanticValue item => item.Utf8Json,
            BinarySemanticValue item => Convert.ToBase64String(item.Value.Span),
            _ => throw new NotSupportedException($"Semantic value '{value.Kind}' has no lexical form.")
        };
    }
}
