using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TokenFloat.Services;

public sealed class InkContextMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color PaperWhite = Color.FromArgb(248, 245, 240);
    private static readonly Color IvoryWhite = Color.FromArgb(255, 255, 240);
    private static readonly Color InkDark = Color.FromArgb(26, 26, 26);
    private static readonly Color InkStrong = Color.FromArgb(51, 51, 51);
    private static readonly Color InkClear = Color.FromArgb(153, 153, 153);
    private static readonly Color SealRed = Color.FromArgb(196, 30, 58);
    private static readonly Color LandscapeGreen = Color.FromArgb(46, 139, 87);

    public InkContextMenuRenderer() : base(new InkColorTable())
    {
        RoundedEdges = false;
    }

    /// <summary>
    /// 按菜单当前尺寸裁出圆角区域，使宣纸背景和自绘边框保持一致。
    /// </summary>
    public static void ApplyRoundedRegion(ContextMenuStrip menu)
    {
        if (menu.Width <= 1 || menu.Height <= 1)
        {
            return;
        }

        using var path = CreateRoundedRectangle(
            new Rectangle(0, 0, menu.Width, menu.Height),
            8);
        var previous = menu.Region;
        menu.Region = new Region(path);
        previous?.Dispose();
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new LinearGradientBrush(
            e.AffectedBounds,
            IvoryWhite,
            PaperWhite,
            LinearGradientMode.Vertical);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(PaperWhite);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(5, 2, e.Item.Width - 10, e.Item.Height - 4);
        using var path = CreateRoundedRectangle(bounds, 6);
        using var brush = new SolidBrush(Color.FromArgb(28, LandscapeGreen));
        using var border = new Pen(Color.FromArgb(42, LandscapeGreen));
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(border, path);

        using var accent = new SolidBrush(Color.FromArgb(190, SealRed));
        e.Graphics.FillEllipse(accent, bounds.Left + 5, bounds.Top + bounds.Height / 2 - 2, 4, 4);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled
            ? InkClear
            : e.Item.Selected
                ? InkDark
                : InkStrong;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var size = 16;
        var bounds = new Rectangle(
            e.ImageRectangle.Left + (e.ImageRectangle.Width - size) / 2,
            e.ImageRectangle.Top + (e.ImageRectangle.Height - size) / 2,
            size,
            size);
        using var path = CreateRoundedRectangle(bounds, 4);
        using var background = new SolidBrush(SealRed);
        e.Graphics.FillPath(background, path);

        using var pen = new Pen(IvoryWhite, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        e.Graphics.DrawLines(pen,
        new Point[]
        {
            new Point(bounds.Left + 4, bounds.Top + 8),
            new Point(bounds.Left + 7, bounds.Top + 11),
            new Point(bounds.Left + 12, bounds.Top + 5)
        });
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(Color.FromArgb(42, InkStrong));
        e.Graphics.DrawLine(pen, 14, y, e.Item.Width - 14, y);
        using var seal = new SolidBrush(Color.FromArgb(150, SealRed));
        e.Graphics.FillEllipse(seal, 8, y - 1.5f, 3, 3);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedRectangle(
            new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1),
            8);
        using var pen = new Pen(Color.FromArgb(74, InkStrong));
        e.Graphics.DrawPath(pen, path);
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class InkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => PaperWhite;
        public override Color ImageMarginGradientBegin => PaperWhite;
        public override Color ImageMarginGradientMiddle => PaperWhite;
        public override Color ImageMarginGradientEnd => PaperWhite;
        public override Color MenuBorder => Color.Transparent;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Color.Transparent;
        public override Color SeparatorDark => Color.FromArgb(42, InkStrong);
        public override Color SeparatorLight => Color.Transparent;
    }
}
