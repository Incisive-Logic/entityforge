using System.Globalization;
using EntityForge.Query.Values;
using EntityForge.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static SpatialPoint PointValue(LiteralValue value) => SpatialPoint.Parse(value.LexicalValue);

    private static QueryValue ConstructPoint(QueryValue input)
    {
        if (input is NullValue or UnboundValue)
        {
            return UnboundValue.Instance;
        }
        if (input is NodeValue or EdgeValue)
        {
            _ = TryEvaluateMap("PROPERTIES", [input], out input);
        }
        if (input is not MapValue map)
        {
            throw new ArgumentException("POINT requires a coordinate map.");
        }
        if (map.Values.Values.Any(value => value is NullValue or UnboundValue))
        {
            return UnboundValue.Instance;
        }
        double Coordinate(string key)
        {
            if (!map.Values.TryGetValue(key, out var value) || value is not LiteralValue
                { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint } literal ||
                !Double.TryParse(literal.LexicalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !Double.IsFinite(number))
            {
                throw new ArgumentException("POINT requires finite numeric coordinates.");
            }
            return number;
        }
        bool cartesianKeys = map.Values.ContainsKey("x") && map.Values.ContainsKey("y");
        string x = cartesianKeys ? "x" : "longitude", y = cartesianKeys ? "y" : "latitude";
        string z = cartesianKeys || map.Values.ContainsKey("z") ? "z" : "height";
        bool three = map.Values.ContainsKey(z);
        int srid = cartesianKeys ? three ? 9157 : 7203 : three ? 4979 : 4326;
        if (map.Values.TryGetValue("crs", out var crs))
        {
            if (map.Values.ContainsKey("srid") || crs is not LiteralValue { ScalarKind: QueryScalarKind.String } name)
            {
                throw new ArgumentException("POINT accepts one CRS name or SRID identifier.");
            }
            srid = name.LexicalValue.ToLowerInvariant() switch
            {
                "cartesian" => 7203,
                "cartesian-3d" => 9157,
                "wgs-84" => 4326,
                "wgs-84-3d" => 4979,
                _ => throw new ArgumentException("Unsupported point coordinate reference system.")
            };
        }
        else if (map.Values.TryGetValue("srid", out var identifier))
        {
            if (identifier is not LiteralValue { ScalarKind: QueryScalarKind.Integer } integer ||
                !Int32.TryParse(integer.LexicalValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid))
            {
                throw new ArgumentException("POINT SRID must be a supported integer identifier.");
            }
        }
        if (!cartesianKeys && srid is not (4326 or 4979))
        {
            throw new ArgumentException("Longitude and latitude require a geographic coordinate reference system.");
        }
        return new LiteralValue(new SpatialPoint(srid, Coordinate(x), Coordinate(y), three ? Coordinate(z) : null).ToString(), QueryScalarKind.Point);
    }

    private static QueryValue PointProperty(LiteralValue literal, QueryValue key)
    {
        if (key is NullValue or UnboundValue)
        {
            return UnboundValue.Instance;
        }
        if (key is not LiteralValue { ScalarKind: QueryScalarKind.String } name)
        {
            throw new ArgumentException("A point property requires a string key.");
        }
        SpatialPoint point = PointValue(literal);
        if (name.LexicalValue == "crs")
        {
            return new LiteralValue(point.Crs);
        }
        if (name.LexicalValue == "srid")
        {
            return new LiteralValue(point.Srid.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
        }
        double value = name.LexicalValue switch
        {
            "x" => point.X,
            "y" => point.Y,
            "z" when point.Z.HasValue => point.Z.Value,
            "longitude" when point.IsGeographic => point.X,
            "latitude" when point.IsGeographic => point.Y,
            "height" when point.IsGeographic && point.Z.HasValue => point.Z.Value,
            _ => throw new ArgumentException("The requested property is unavailable on this point.")
        };
        return new LiteralValue(value.ToString("R", CultureInfo.InvariantCulture), QueryScalarKind.FloatingPoint);
    }

    private static QueryValue SpatialFunction(string function, QueryValue[] values)
    {
        if (values.Any(value => value is NullValue or UnboundValue))
        {
            return UnboundValue.Instance;
        }
        if (values.Any(value => value is not LiteralValue { ScalarKind: QueryScalarKind.Point }))
        {
            return UnboundValue.Instance;
        }
        SpatialPoint[] points = values.Cast<LiteralValue>().Select(PointValue).ToArray();
        SpatialPoint first = points[0], second = points[1];
        if (points.Any(point => point.Srid != first.Srid))
        {
            return UnboundValue.Instance;
        }
        if (function == "POINT.WITHINBBOX")
        {
            SpatialPoint upper = points[2];
            bool x = first.IsGeographic && second.X > upper.X ? first.X >= second.X || first.X <= upper.X : first.X >= second.X && first.X <= upper.X;
            bool inside = x && first.Y >= second.Y && first.Y <= upper.Y &&
                (!first.Z.HasValue || first.Z >= second.Z && first.Z <= upper.Z);
            return new LiteralValue(inside ? "true" : "false", QueryScalarKind.Boolean);
        }
        double distance;
        if (!first.IsGeographic)
        {
            distance = Double.Hypot(Double.Hypot(first.X - second.X, first.Y - second.Y), first.Z.GetValueOrDefault() - second.Z.GetValueOrDefault());
        }
        else
        {
            const double radians = Math.PI / 180;
            double latitude = Math.Sin((first.Y - second.Y) * radians / 2);
            double longitude = Math.Sin((first.X - second.X) * radians / 2);
            double haversine = latitude * latitude + Math.Cos(first.Y * radians) * Math.Cos(second.Y * radians) * longitude * longitude;
            double radius = 6_378_140 + first.Z.GetValueOrDefault() / 2 + second.Z.GetValueOrDefault() / 2;
            double surface = 2 * radius * Math.Asin(Math.Sqrt(Math.Clamp(haversine, 0, 1)));
            distance = Double.Hypot(surface, first.Z.GetValueOrDefault() - second.Z.GetValueOrDefault());
        }
        return new LiteralValue(distance.ToString("R", CultureInfo.InvariantCulture), QueryScalarKind.FloatingPoint);
    }

    private static string PointText(SpatialPoint point) => "point({x: " + FloatingText(point.X) + ", y: " + FloatingText(point.Y) +
        (point.Z is { } z ? ", z: " + FloatingText(z) : "") + ", crs: '" + point.Crs + "'})";
}
