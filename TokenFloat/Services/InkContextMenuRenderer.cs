using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TokenFloat.Services;

public sealed class InkContextMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color Background = Color.FromArgb(255, 255, 255);
    private static readonly Color Border = Color.FromArgb(221, 225, 218);
    private static readonly Color Text = Color.FromArgb(32, 34, 31);
    private static readonly Color Muted = Color.FromArgb(123, 129, 120);
    private static readonly Color Green = Color.FromArgb(95, 127, 79);
    private static readonly Color GreenSurface = Color.FromArgb(238, 244, 234);

    public InkContextMenuRenderer() : base(new DashboardColorTable())
    {
        RoundedEdges = false;
    }

    /// <summary>
    /// 为托盘菜单设置圆角区域，使系统菜单外观与主界面保持一致。
    /// </summary>
    public static void ApplyRoundedRegion(ContextMenuStrip menu)
    {
        if (menu.Width <= 1 || menu.Height <= 1)
        {
            return;
        }

        using var path = CreateRoundedRectangle(new Rectangle(0, 0, menu.Width, menu.Height), 9);
        var previous = menu.Region;
        menu.Region = new Region(path);
        previous?.Dispose();
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(6, 2, Math.Max(1, e.Item.Width - 12), Math.Max(1, e.Item.Height - 4));
        using var path = CreateRoundedRectangle(bounds, 6);
        using var brush = new SolidBrush(GreenSurface);
        using var borderPen = new Pen(Color.FromArgb(191, 208, 182));
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(borderPen, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? Muted : Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        const int size = 16;
        var bounds = new Rectangle(
            e.ImageRectangle.Left + (e.ImageRectangle.Width - size) / 2,
            e.ImageRectangle.Top + (e.ImageRectangle.Height - size) / 2,
            size,
            size);
        using var path = CreateRoundedRectangle(bounds, 5);
        using var background = new SolidBrush(Green);
        e.Graphics.FillPath(background, path);

        using var pen = new Pen(Color.White, 1.8f)
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
        using var pen = new Pen(Border);
        e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedRectangle(new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 9);
        using var pen = new Pen(Border);
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

    private sealed class DashboardColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuBorder => Color.Transparent;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Color.Transparent;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Color.Transparent;
    }
}
