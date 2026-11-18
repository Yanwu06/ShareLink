using System.Windows.Markup;
using System.Windows.Media;

namespace ShareLink.BrowserSharing;

public sealed class LucideIconExtension : MarkupExtension
{
    private static readonly Pen Stroke = CreateStroke();

    // Lucide 1.17.0: info and qr-code. See Lucide.LICENSE.txt.
    public static Geometry Info { get; } = Outline(
        "M12 2A10 10 0 1 1 12 22A10 10 0 1 1 12 2Z " +
        "M12 16v-4 " +
        "M12 8h.01");

    internal static Geometry QrCode { get; } = Outline(
        "M4 3H7A1 1 0 0 1 8 4V7A1 1 0 0 1 7 8H4A1 1 0 0 1 3 7V4A1 1 0 0 1 4 3Z " +
        "M17 3H20A1 1 0 0 1 21 4V7A1 1 0 0 1 20 8H17A1 1 0 0 1 16 7V4A1 1 0 0 1 17 3Z " +
        "M4 16H7A1 1 0 0 1 8 17V20A1 1 0 0 1 7 21H4A1 1 0 0 1 3 20V17A1 1 0 0 1 4 16Z " +
        "M21 16h-3a2 2 0 0 0-2 2v3 " +
        "M21 21v.01 " +
        "M12 7v3a2 2 0 0 1-2 2H7 " +
        "M3 12h.01 " +
        "M12 3h.01 " +
        "M12 16v.01 " +
        "M16 12h1 " +
        "M21 12v.01 " +
        "M12 21v-1");

    public string Data { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Outline(Data);

    private static Geometry Outline(string data)
    {
        // Keep existing WPF fill bindings and opacity masks while preserving the SVG stroke.
        var outline = Geometry.Parse(data).GetWidenedPathGeometry(Stroke, 0.01, ToleranceType.Absolute);
        outline.Freeze();
        return outline;
    }

    private static Pen CreateStroke()
    {
        var pen = new Pen(Brushes.Black, 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }
}
