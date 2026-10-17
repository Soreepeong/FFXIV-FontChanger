using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FontChanger.DalamudPlugin.Fonts.Faces.Sources.Nominal;

internal sealed partial class MergingFaceSource
{
    /// <summary>Points in shape units with FreeType's outline tags; each contour starts on the curve and closes by itself.</summary>
    private sealed class ShapePath
    {
        private const byte TagOn = 1;
        private const byte TagConic = 0;
        private const byte TagCubic = 2;

        public List<(float X, float Y)> Points { get; } = [];

        public List<byte> Tags { get; } = [];

        public List<short> Ends { get; } = [];

        public static ShapePath Parse(string d)
        {
            var p = new ShapePath();
            var i = 0;
            (float X, float Y) cur = (0, 0), start = (0, 0), lastControl = (0, 0);
            char lastCommand = '\0', command = '\0';
            while (true)
            {
                SkipSeparators();
                if (i >= d.Length)
                    break;
                if (char.IsAsciiLetter(d[i]))
                    command = d[i++];
                else if (command == '\0')
                    throw new FormatException("Path data must begin with a command.");

                var relative = char.IsLower(command);
                var b = relative ? cur : (0f, 0f);
                var upper = char.ToUpperInvariant(command);
                switch (upper)
                {
                    case 'M':
                        cur = start = Add(b, ReadPoint());
                        p.MoveTo(cur);

                        // SVG makes coordinate pairs after a moveto's first implicit linetos.
                        command = relative ? 'l' : 'L';
                        break;
                    case 'L':
                        cur = Add(b, ReadPoint());
                        p.LineTo(cur);
                        break;
                    case 'H':
                        cur = ((relative ? cur.X : 0) + ReadNumber(), cur.Y);
                        p.LineTo(cur);
                        break;
                    case 'V':
                        cur = (cur.X, (relative ? cur.Y : 0) + ReadNumber());
                        p.LineTo(cur);
                        break;
                    case 'C' or 'S':
                    {
                        var c1 = upper == 'C' ? Add(b, ReadPoint())
                                 : lastCommand is 'C' or 'S' ? Reflect(cur, lastControl)
                                 : cur;
                        var c2 = Add(b, ReadPoint());
                        cur = Add(b, ReadPoint());
                        p.CubicTo(c1, c2, cur);
                        lastControl = c2;
                        break;
                    }

                    case 'Q' or 'T':
                    {
                        var c = upper == 'Q' ? Add(b, ReadPoint())
                                : lastCommand is 'Q' or 'T' ? Reflect(cur, lastControl)
                                : cur;
                        cur = Add(b, ReadPoint());
                        p.QuadTo(c, cur);
                        lastControl = c;
                        break;
                    }

                    case 'A':
                    {
                        var rx = MathF.Abs(ReadNumber());
                        var ry = MathF.Abs(ReadNumber());
                        var angle = ReadNumber() * MathF.PI / 180;
                        var largeArc = ReadFlag();
                        var sweep = ReadFlag();
                        var end = Add(b, ReadPoint());
                        p.ArcTo(cur, end, rx, ry, angle, largeArc, sweep);
                        cur = end;
                        break;
                    }

                    case 'Z':
                        p.Close();
                        cur = start;
                        break;
                    default:
                        throw new FormatException("Unsupported command in path data.");
                }

                lastCommand = char.ToUpperInvariant(command);
                if (upper == 'Z' && !NextIsNumber())
                    command = '\0';
            }

            p.Close();
            return p;

            void SkipSeparators()
            {
                while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ','))
                    i++;
            }

            bool NextIsNumber()
            {
                SkipSeparators();
                return i < d.Length && (char.IsAsciiDigit(d[i]) || d[i] is '-' or '+' or '.');
            }

            float ReadNumber()
            {
                SkipSeparators();
                var begin = i;
                if (i < d.Length && d[i] is '-' or '+')
                    i++;
                bool dot = false, exponent = false;
                while (i < d.Length)
                {
                    var c = d[i];
                    if (char.IsAsciiDigit(c))
                    {
                        i++;
                    }
                    else if (c == '.' && !dot && !exponent)
                    {
                        dot = true;
                        i++;
                    }
                    else if (c is 'e' or 'E' && !exponent)
                    {
                        exponent = true;
                        i++;
                        if (i < d.Length && d[i] is '-' or '+')
                            i++;
                    }
                    else
                    {
                        break;
                    }
                }

                if (begin == i)
                    throw new FormatException("Invalid number in path data.");
                return float.Parse(d.AsSpan(begin, i - begin), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            (float, float) ReadPoint() => (ReadNumber(), ReadNumber());

            bool ReadFlag()
            {
                SkipSeparators();
                if (i >= d.Length || d[i] is not ('0' or '1'))
                    throw new FormatException("Invalid flag in path data.");
                return d[i++] == '1';
            }

            static (float, float) Add((float X, float Y) a, (float X, float Y) b) => (a.X + b.X, a.Y + b.Y);

            static (float, float) Reflect((float X, float Y) p, (float X, float Y) c) => ((2 * p.X) - c.X, (2 * p.Y) - c.Y);
        }

        public void MoveTo((float X, float Y) p)
        {
            this.Close();
            this.Add(p, TagOn);
        }

        public void LineTo((float X, float Y) p) => this.Add(p, TagOn);

        public void QuadTo((float X, float Y) c, (float X, float Y) p)
        {
            this.Add(c, TagConic);
            this.Add(p, TagOn);
        }

        public void CubicTo((float X, float Y) c1, (float X, float Y) c2, (float X, float Y) p)
        {
            this.Add(c1, TagCubic);
            this.Add(c2, TagCubic);
            this.Add(p, TagOn);
        }

        public void Close()
        {
            var begin = this.Ends.Count == 0 ? 0 : this.Ends[^1] + 1;
            if (this.Points.Count <= begin)
                return;

            // FreeType closes contours implicitly, so an explicit closing point would be a duplicate.
            var first = this.Points[begin];
            var last = this.Points[^1];
            if (this.Points.Count - begin > 1 && this.Tags[^1] == TagOn && MathF.Abs(last.X - first.X) < 0.001f && MathF.Abs(last.Y - first.Y) < 0.001f)
            {
                this.Points.RemoveAt(this.Points.Count - 1);
                this.Tags.RemoveAt(this.Tags.Count - 1);
            }

            this.Ends.Add((short)(this.Points.Count - 1));
        }

        public void AddRoundedPolygon((float X, float Y)[] vertices, float radius)
        {
            const float Kappa = 0.5522847f;
            var n = vertices.Length;
            var starts = new (float X, float Y)[n];
            var ends = new (float X, float Y)[n];
            for (var i = 0; i < n; i++)
            {
                var (prev, cur, next) = (vertices[(i + n - 1) % n], vertices[i], vertices[(i + 1) % n]);
                var (px, py) = (prev.X - cur.X, prev.Y - cur.Y);
                var (nx, ny) = (next.X - cur.X, next.Y - cur.Y);
                var (lp, ln) = (MathF.Sqrt((px * px) + (py * py)), MathF.Sqrt((nx * nx) + (ny * ny)));
                var r = Math.Min(radius, Math.Min(lp / 2, ln / 2));
                starts[i] = (cur.X + (px * r / lp), cur.Y + (py * r / lp));
                ends[i] = (cur.X + (nx * r / ln), cur.Y + (ny * r / ln));
            }

            this.MoveTo(ends[0]);
            for (var j = 1; j <= n; j++)
            {
                var i = j % n;
                var v = vertices[i];
                this.LineTo(starts[i]);
                this.CubicTo(
                    (starts[i].X + ((v.X - starts[i].X) * Kappa), starts[i].Y + ((v.Y - starts[i].Y) * Kappa)),
                    (ends[i].X + ((v.X - ends[i].X) * Kappa), ends[i].Y + ((v.Y - ends[i].Y) * Kappa)),
                    ends[i]);
            }

            this.Close();
        }

        // In pixels from the line's top, with the baseline at baselineY.
        public IEnumerable<(float X, float Y)> ToPixels(float scale, float baselineY) =>
            this.Points.Select(p => (p.X * scale, baselineY - ((ShapeBaselineY - p.Y) * scale)));

        public (float X1, float Y1, float X2, float Y2) GetBounds(float scale, float baselineY)
        {
            float x1 = float.MaxValue, y1 = float.MaxValue, x2 = float.MinValue, y2 = float.MinValue;
            foreach (var (x, y) in this.ToPixels(scale, baselineY))
                (x1, x2, y1, y2) = (Math.Min(x1, x), Math.Max(x2, x), Math.Min(y1, y), Math.Max(y2, y));
            return (x1, y1, x2, y2);
        }

        // SVG 1.1 implementation notes F.6, as cubic curves of at most 90 degrees.
        private void ArcTo((float X, float Y) from, (float X, float Y) to, float rx, float ry, float angle, bool largeArc, bool sweep)
        {
            if (rx == 0 || ry == 0)
            {
                this.LineTo(to);
                return;
            }

            var (cosA, sinA) = (MathF.Cos(angle), MathF.Sin(angle));
            var (dx, dy) = ((from.X - to.X) / 2, (from.Y - to.Y) / 2);
            var (x1P, y1P) = ((cosA * dx) + (sinA * dy), (-sinA * dx) + (cosA * dy));
            var lambda = (x1P * x1P / (rx * rx)) + (y1P * y1P / (ry * ry));
            if (lambda > 1)
                (rx, ry) = (rx * MathF.Sqrt(lambda), ry * MathF.Sqrt(lambda));

            var num = (rx * rx * ry * ry) - (rx * rx * y1P * y1P) - (ry * ry * x1P * x1P);
            var den = (rx * rx * y1P * y1P) + (ry * ry * x1P * x1P);
            var coef = MathF.Sqrt(Math.Max(0, num / den));
            if (largeArc == sweep)
                coef = -coef;
            var (cxp, cyp) = (coef * rx * y1P / ry, -coef * ry * x1P / rx);
            var cx = (cosA * cxp) - (sinA * cyp) + ((from.X + to.X) / 2);
            var cy = (sinA * cxp) + (cosA * cyp) + ((from.Y + to.Y) / 2);
            var theta1 = VectorAngle(1, 0, (x1P - cxp) / rx, (y1P - cyp) / ry);
            var delta = VectorAngle((x1P - cxp) / rx, (y1P - cyp) / ry, (-x1P - cxp) / rx, (-y1P - cyp) / ry);
            if (!sweep && delta > 0)
                delta -= 2 * MathF.PI;
            else if (sweep && delta < 0)
                delta += 2 * MathF.PI;

            var pieces = Math.Max(1, (int)MathF.Ceiling((MathF.Abs(delta) / (MathF.PI / 2)) - 0.001f));
            var step = delta / pieces;
            var k = 4f / 3 * MathF.Tan(step / 4);
            for (var j = 0; j < pieces; j++)
            {
                var t1 = theta1 + (step * j);
                var t2 = t1 + step;
                var p1 = At(t1);
                var p2 = j + 1 == pieces ? to : At(t2);
                var d1 = Derivative(t1);
                var d2 = Derivative(t2);
                this.CubicTo((p1.X + (d1.X * k), p1.Y + (d1.Y * k)), (p2.X - (d2.X * k), p2.Y - (d2.Y * k)), p2);
            }

            (float X, float Y) At(float t) =>
                (cx + (rx * MathF.Cos(t) * cosA) - (ry * MathF.Sin(t) * sinA), cy + (rx * MathF.Cos(t) * sinA) + (ry * MathF.Sin(t) * cosA));

            (float X, float Y) Derivative(float t) =>
                ((-rx * MathF.Sin(t) * cosA) - (ry * MathF.Cos(t) * sinA), (-rx * MathF.Sin(t) * sinA) + (ry * MathF.Cos(t) * cosA));

            static float VectorAngle(float ux, float uy, float vx, float vy) => MathF.Atan2((ux * vy) - (uy * vx), (ux * vx) + (uy * vy));
        }

        private void Add((float X, float Y) p, byte tag)
        {
            this.Points.Add(p);
            this.Tags.Add(tag);
        }
    }
}
