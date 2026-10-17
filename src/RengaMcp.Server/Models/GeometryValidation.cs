namespace RengaMcp.Models;

public static class GeometryValidation
{
    private const double Tolerance = 1e-7;

    public static void Placement(Placement3DValue placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        Point(placement.Origin); Point(placement.AxisX); Point(placement.AxisZ);
        var x = placement.AxisX; var z = placement.AxisZ;
        if (Math.Abs(x.X * x.X + x.Y * x.Y + x.Z * x.Z - 1) > Tolerance ||
            Math.Abs(z.X * z.X + z.Y * z.Y + z.Z * z.Z - 1) > Tolerance ||
            Math.Abs(x.X * z.X + x.Y * z.Y + x.Z * z.Z) > Tolerance)
            throw new ArgumentException("Placement axes must be perpendicular unit vectors.", nameof(placement));
    }

    public static void Point(Point3DValue point)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
            throw new ArgumentException("Coordinates must be finite.", nameof(point));
    }

    public static void Baseline(IReadOnlyList<Point2DValue> points, bool closed)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < (closed ? 3 : 2) || points.Count > 256)
            throw new ArgumentException("Use 2..256 vertices, or 3..256 for a closed contour. Do not repeat the first vertex.", nameof(points));
        foreach (var point in points)
        {
            ArgumentNullException.ThrowIfNull(point);
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                throw new ArgumentException("Coordinates must be finite.", nameof(points));
        }
        var edges = closed ? points.Count : points.Count - 1;
        for (var i = closed ? 0 : 1; i < (closed ? points.Count : points.Count - 1); i++)
        {
            var a = points[(i + points.Count - 1) % points.Count];
            var b = points[i]; var c = points[(i + 1) % points.Count];
            var cross = (b.X-a.X)*(c.Y-b.Y) - (b.Y-a.Y)*(c.X-b.X);
            var dot = (b.X-a.X)*(c.X-b.X) + (b.Y-a.Y)*(c.Y-b.Y);
            if (Math.Abs(cross) < Tolerance && dot < -Tolerance)
                throw new ArgumentException("Adjacent edges cannot overlap by reversing direction.", nameof(points));
        }
        for (var i = 0; i < edges; i++)
        {
            var a = points[i]; var b = points[(i + 1) % points.Count];
            if (Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) < Tolerance)
                throw new ArgumentException("Zero-length edges are not allowed.", nameof(points));
            for (var j = i + 1; j < edges; j++)
            {
                if (j == i + 1 || (closed && i == 0 && j == edges - 1)) continue;
                if (Intersects(a, b, points[j], points[(j + 1) % points.Count]))
                    throw new ArgumentException("Baseline edges cannot intersect or touch except at adjacent endpoints.", nameof(points));
            }
        }
        if (closed)
        {
            var area = Enumerable.Range(0, points.Count).Sum(i =>
                points[i].X * points[(i + 1) % points.Count].Y - points[(i + 1) % points.Count].X * points[i].Y);
            if (!double.IsFinite(area) || Math.Abs(area) < Tolerance) throw new ArgumentException("Contour must enclose a finite, nonzero area.", nameof(points));
        }
    }

    public static void Assignments(IReadOnlyList<ParameterAssignment> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Count > 100) throw new ArgumentException("At most 100 parameter assignments are allowed.", nameof(parameters));
        var ids = new HashSet<Guid>();
        foreach (var parameter in parameters)
        {
            ArgumentNullException.ThrowIfNull(parameter);
            if (!Guid.TryParse(parameter.ParameterId, out var id) || !ids.Add(id))
                throw new ArgumentException("Parameter IDs must be valid, distinct GUIDs.", nameof(parameters));
            ArgumentNullException.ThrowIfNull(parameter.Value);
        }
    }

    private static bool Intersects(Point2DValue a, Point2DValue b, Point2DValue c, Point2DValue d)
    {
        static double Cross(Point2DValue p, Point2DValue q, Point2DValue r) => (q.X-p.X)*(r.Y-p.Y)-(q.Y-p.Y)*(r.X-p.X);
        static bool On(Point2DValue p, Point2DValue q, Point2DValue r) =>
            r.X >= Math.Min(p.X,q.X)-Tolerance && r.X <= Math.Max(p.X,q.X)+Tolerance &&
            r.Y >= Math.Min(p.Y,q.Y)-Tolerance && r.Y <= Math.Max(p.Y,q.Y)+Tolerance;
        var c1 = Cross(a,b,c); var c2 = Cross(a,b,d); var c3 = Cross(c,d,a); var c4 = Cross(c,d,b);
        return ((c1 > Tolerance && c2 < -Tolerance || c1 < -Tolerance && c2 > Tolerance) &&
                (c3 > Tolerance && c4 < -Tolerance || c3 < -Tolerance && c4 > Tolerance)) ||
            Math.Abs(c1)<Tolerance && On(a,b,c) || Math.Abs(c2)<Tolerance && On(a,b,d) ||
            Math.Abs(c3)<Tolerance && On(c,d,a) || Math.Abs(c4)<Tolerance && On(c,d,b);
    }
}
