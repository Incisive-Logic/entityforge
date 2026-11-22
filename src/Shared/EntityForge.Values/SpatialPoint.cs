using System.Globalization;
using System.Text.Json.Serialization;

namespace EntityForge.Values;

/// <summary>An immutable two- or three-dimensional point in a supported coordinate reference system.</summary>
public sealed record SpatialPoint : IComparable<SpatialPoint>
{
    /// <summary>Creates a point, validating its dimensions and normalizing geographic longitude.</summary>
    /// <param name="srid">7203 for Cartesian 2D, 9157 for Cartesian 3D, 4326 for WGS 84 2D, or 4979 for WGS 84 3D.</param>
    /// <param name="x">Cartesian X or geographic longitude in degrees.</param>
    /// <param name="y">Cartesian Y or geographic latitude in degrees.</param>
    /// <param name="z">Cartesian Z or geographic height in metres; required only for 3D systems.</param>
    /// <exception cref="ArgumentException">The coordinate system, dimensions or coordinates are invalid.</exception>
    [JsonConstructor]
    public SpatialPoint(int srid, double x, double y, double? z = null)
    {
        if (srid is not (7203 or 9157 or 4326 or 4979) || (srid is 9157 or 4979) != z.HasValue ||
            !Double.IsFinite(x) || !Double.IsFinite(y) || z.HasValue && !Double.IsFinite(z.Value))
        {
            throw new ArgumentException("A point requires a supported coordinate system and finite coordinates of the correct dimension.");
        }
        if (srid is 4326 or 4979)
        {
            if (y is < -90 or > 90)
            {
                throw new ArgumentException("Geographic latitude must be between -90 and 90 degrees.");
            }
            x %= 360;
            if (x > 180)
            {
                x -= 360;
            }

            if (x < -180)
            {
                x += 360;
            }
        }
        Srid = srid;
        X = x == 0 ? 0 : x;
        Y = y == 0 ? 0 : y;
        Z = z == 0 ? 0 : z;
    }

    /// <summary>The coordinate reference system identifier.</summary>
    public int Srid
    {
        get;
    }

    /// <summary>Cartesian X or geographic longitude in degrees.</summary>
    public double X
    {
        get;
    }

    /// <summary>Cartesian Y or geographic latitude in degrees.</summary>
    public double Y
    {
        get;
    }

    /// <summary>Cartesian Z or geographic height in metres, absent for 2D points.</summary>
    public double? Z
    {
        get;
    }

    /// <summary>The coordinate reference system name.</summary>
    [JsonIgnore]
    public string Crs => Srid switch { 7203 => "cartesian", 9157 => "cartesian-3d", 4326 => "wgs-84", _ => "wgs-84-3d" };

    /// <summary>Whether coordinates represent geographic longitude and latitude.</summary>
    [JsonIgnore]
    public bool IsGeographic => Srid is 4326 or 4979;

    /// <summary>Orders points by coordinate system, dimension and coordinate values.</summary>
    /// <param name="other">The point to compare; null sorts first.</param>
    /// <returns>A negative, zero or positive ordering result.</returns>
    /// <remarks>This total ordering is for sorting and does not define spatial containment.</remarks>
    public int CompareTo(SpatialPoint? other)
    {
        if (other is null)
        {
            return 1;
        }

        int order = Srid.CompareTo(other.Srid);
        if (order == 0)
        {
            order = X.CompareTo(other.X);
        }

        if (order == 0)
        {
            order = Y.CompareTo(other.Y);
        }

        return order == 0 ? Nullable.Compare(Z, other.Z) : order;
    }

    /// <summary>Formats the point as an SRID-prefixed WKT POINT with round-trip coordinate precision.</summary>
    /// <returns>The canonical point text.</returns>
    public override string ToString() => "SRID=" + Srid.ToString(CultureInfo.InvariantCulture) + ";POINT (" +
        X.ToString("R", CultureInfo.InvariantCulture) + " " + Y.ToString("R", CultureInfo.InvariantCulture) +
        (Z is { } z ? " " + z.ToString("R", CultureInfo.InvariantCulture) : "") + ")";

    /// <summary>Parses the SRID-prefixed point representation produced by ToString.</summary>
    /// <param name="text">Canonical point text, using invariant numeric syntax.</param>
    /// <returns>The validated point.</returns>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="FormatException">The text, coordinate system or coordinates are invalid.</exception>
    public static SpatialPoint Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int separator = text.IndexOf(";POINT (", StringComparison.Ordinal);
        if (!text.StartsWith("SRID=", StringComparison.Ordinal) || separator < 5 || !text.EndsWith(')'))
        {
            throw new FormatException("Expected an SRID-prefixed POINT.");
        }
        string[] coordinates = text[(separator + 8)..^1].Split(' ');
        try
        {
            if (coordinates.Length is not (2 or 3))
            {
                throw new FormatException("A point requires two or three coordinates.");
            }

            double Number(int index) => Double.Parse(coordinates[index], NumberStyles.Float, CultureInfo.InvariantCulture);
            return new(Int32.Parse(text[5..separator], NumberStyles.None, CultureInfo.InvariantCulture), Number(0), Number(1),
                coordinates.Length == 3 ? Number(2) : null);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new FormatException("Invalid point coordinates.", exception);
        }
    }
}
