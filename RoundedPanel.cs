// ============================================================================
//  RoundedPanel.cs — Panel with rounded corners and optional border.
//  Used for the lavender task cards in VideoConverter.
// ============================================================================

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VideoConverter
{
    public class RoundedPanel : Panel
    {
        public int CornerRadius { get; set; } = 12;
        public Color BorderColor { get; set; } = Color.FromArgb(197, 181, 232);
        public Color HoverBorderColor { get; set; } = Color.FromArgb(160, 140, 210);
        public Color ActiveBorderColor { get; set; } = Color.FromArgb(124, 77, 255);

        public Color FillColor { get; set; } = Color.FromArgb(240, 236, 249);
        public Color HoverFillColor { get; set; } = Color.FromArgb(232, 228, 245);

        public int BorderWidth { get; set; } = 1;
        public int ActiveBorderWidth { get; set; } = 2;

        public bool IsHovered { get; set; }
        public bool IsActive { get; set; }

        public RoundedPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = GetRoundedRect(rect, CornerRadius))
            {
                // Fill.
                Color fill = IsActive ? HoverFillColor : (IsHovered ? HoverFillColor : FillColor);
                using (var brush = new SolidBrush(fill))
                    e.Graphics.FillPath(brush, path);

                // Border.
                int bw = IsActive ? ActiveBorderWidth : BorderWidth;
                Color bc = IsActive ? ActiveBorderColor : (IsHovered ? HoverBorderColor : BorderColor);
                if (bw > 0)
                {
                    using (var pen = new Pen(bc, bw))
                    {
                        pen.Alignment = PenAlignment.Inset;
                        e.Graphics.DrawPath(pen, path);
                    }
                }
            }
            base.OnPaint(e);
        }

        private GraphicsPath GetRoundedRect(Rectangle rect, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.StartFigure();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
