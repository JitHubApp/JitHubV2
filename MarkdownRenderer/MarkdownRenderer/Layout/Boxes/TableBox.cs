using System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Document;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Renders a GFM pipe table with header and body rows using Win2D CanvasTextLayout.
/// </summary>
public sealed class TableBox : BlockBox
{
    private readonly MarkdownLayoutContext _context;
    private readonly string[][] _headers;   // [row][col]
    private readonly string[][] _rows;      // [row][col]
    private int _colCount;

    private CanvasTextLayout[][]? _headerLayouts;
    private CanvasTextLayout[][]? _rowLayouts;
    private float[]? _colWidths;
    private float[]? _rowHeights;   // combined: header rows first, then body rows

    public TableBox(MarkdownLayoutContext context, string[][] headers, string[][] rows)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _headers = headers ?? Array.Empty<string[]>();
        _rows = rows ?? Array.Empty<string[]>();
        Margin = new Thickness(0, 6, 0, 6);

        _colCount = 0;
        if (_headers.Length > 0 && _headers[0].Length > 0)
            _colCount = _headers[0].Length;
        else if (_rows.Length > 0 && _rows[0].Length > 0)
            _colCount = _rows[0].Length;
    }

    public override float Measure(float availableWidth)
    {
        if (_colCount == 0)
        {
            Bounds = new Rect(0, 0, availableWidth, 0);
            return 0;
        }

        DisposeLayouts();

        float innerWidth = availableWidth - (float)(Margin.Left + Margin.Right);
        float colWidth = Math.Max(1f, innerWidth / _colCount);
        _colWidths = new float[_colCount];
        for (int c = 0; c < _colCount; c++)
            _colWidths[c] = colWidth;

        var headerStyle = _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.TableHeader);
        var bodyStyle = _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.TableCell);

        const float cellPadH = 16f;
        const float cellPadV = 8f;

        _headerLayouts = new CanvasTextLayout[_headers.Length][];
        float[] headerHeights = new float[_headers.Length];
        for (int r = 0; r < _headers.Length; r++)
        {
            _headerLayouts[r] = new CanvasTextLayout[_colCount];
            float maxH = 0;
            for (int c = 0; c < _colCount; c++)
            {
                string text = c < _headers[r].Length ? (_headers[r][c] ?? string.Empty) : string.Empty;
                using var fmt = CreateFormat(headerStyle);
                var layout = new CanvasTextLayout(
                    _context.ResourceCreator, text, fmt,
                    Math.Max(1f, colWidth - cellPadH), float.MaxValue);
                _headerLayouts[r][c] = layout;
                maxH = Math.Max(maxH, (float)layout.LayoutBounds.Height);
            }
            headerHeights[r] = maxH + cellPadV * 2f;
        }

        _rowLayouts = new CanvasTextLayout[_rows.Length][];
        float[] rowHeights = new float[_rows.Length];
        for (int r = 0; r < _rows.Length; r++)
        {
            _rowLayouts[r] = new CanvasTextLayout[_colCount];
            float maxH = 0;
            for (int c = 0; c < _colCount; c++)
            {
                string text = c < _rows[r].Length ? (_rows[r][c] ?? string.Empty) : string.Empty;
                using var fmt = CreateFormat(bodyStyle);
                var layout = new CanvasTextLayout(
                    _context.ResourceCreator, text, fmt,
                    Math.Max(1f, colWidth - cellPadH), float.MaxValue);
                _rowLayouts[r][c] = layout;
                maxH = Math.Max(maxH, (float)layout.LayoutBounds.Height);
            }
            rowHeights[r] = maxH + cellPadV * 2f;
        }

        _rowHeights = new float[_headers.Length + _rows.Length];
        for (int i = 0; i < _headers.Length; i++) _rowHeights[i] = headerHeights[i];
        for (int i = 0; i < _rows.Length; i++) _rowHeights[_headers.Length + i] = rowHeights[i];

        float totalHeight = (float)(Margin.Top + Margin.Bottom);
        foreach (var h in _rowHeights) totalHeight += h;

        Bounds = new Rect(0, 0, availableWidth, totalHeight);
        return totalHeight;
    }

    public override void Paint(CanvasDrawingSession ds, Rect viewport)
    {
        if (_colWidths is null || _rowHeights is null) return;

        var headerStyle = _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.TableHeader);
        var bodyStyle = _context.ThemeSnapshot.GetStyle(MarkdownElementKeys.TableCell);

        float startX = (float)(Bounds.X + Margin.Left);
        float rowY = (float)(Bounds.Y + Margin.Top);
        float innerWidth = (float)(Bounds.Width - Margin.Left - Margin.Right);

        const float cellPadH = 8f;
        const float cellPadV = 8f;

        // Draw header background
        float headerTotalH = 0;
        for (int i = 0; i < _headers.Length; i++) headerTotalH += _rowHeights[i];
        if (headerStyle.Background is { } headerBg && _headers.Length > 0)
            ds.FillRectangle(new Rect(startX, rowY, innerWidth, headerTotalH), headerBg);

        // Draw header rows
        for (int r = 0; r < _headers.Length; r++)
        {
            float rh = _rowHeights[r];
            float colX = startX;
            for (int c = 0; c < _colCount; c++)
            {
                if (_headerLayouts?[r][c] is { } layout)
                    ds.DrawTextLayout(layout, colX + cellPadH, rowY + cellPadV, headerStyle.Foreground);
                colX += _colWidths[c];
            }
            rowY += rh;
        }

        // Separator line after headers
        if (_headers.Length > 0)
        {
            var sep = Color.FromArgb(80, bodyStyle.Foreground.R, bodyStyle.Foreground.G, bodyStyle.Foreground.B);
            ds.DrawLine(startX, rowY, startX + innerWidth, rowY, sep, 1f);
        }

        // Draw body rows
        for (int r = 0; r < _rows.Length; r++)
        {
            float rh = _rowHeights[_headers.Length + r];
            if (rowY > viewport.Bottom) break;
            if (rowY + rh >= viewport.Top)
            {
                float colX = startX;
                for (int c = 0; c < _colCount; c++)
                {
                    if (_rowLayouts?[r][c] is { } layout)
                        ds.DrawTextLayout(layout, colX + cellPadH, rowY + cellPadV, bodyStyle.Foreground);
                    colX += _colWidths[c];
                }
                var rowSep = Color.FromArgb(30, bodyStyle.Foreground.R, bodyStyle.Foreground.G, bodyStyle.Foreground.B);
                ds.DrawLine(startX, rowY + rh, startX + innerWidth, rowY + rh, rowSep, 0.5f);
            }
            rowY += rh;
        }

        // Column separator lines
        float tableH = (float)(Bounds.Height - Margin.Top - Margin.Bottom);
        float colSepX = startX;
        var colSepColor = Color.FromArgb(30, bodyStyle.Foreground.R, bodyStyle.Foreground.G, bodyStyle.Foreground.B);
        for (int c = 1; c < _colCount; c++)
        {
            colSepX += _colWidths[c - 1];
            ds.DrawLine(colSepX, (float)(Bounds.Y + Margin.Top), colSepX,
                (float)(Bounds.Y + Margin.Top + tableH), colSepColor, 0.5f);
        }
    }

    public override bool HitTest(Point point, out DocumentPosition position)
    {
        position = new DocumentPosition(BlockIndex, 0, 0);
        return Bounds.Contains(point);
    }

    private CanvasTextFormat CreateFormat(ElementStyle style) => new CanvasTextFormat
    {
        FontFamily = style.FontFamily,
        FontSize = style.FontSize,
        FontWeight = style.FontWeight,
        FontStyle = style.FontStyle,
        WordWrapping = CanvasWordWrapping.Wrap,
        LineSpacingMode = CanvasLineSpacingMode.Proportional,
        LineSpacing = style.LineHeightMultiplier,
    };

    private void DisposeLayouts()
    {
        if (_headerLayouts is not null)
        {
            foreach (var row in _headerLayouts)
                foreach (var l in row)
                    l?.Dispose();
            _headerLayouts = null;
        }
        if (_rowLayouts is not null)
        {
            foreach (var row in _rowLayouts)
                foreach (var l in row)
                    l?.Dispose();
            _rowLayouts = null;
        }
    }
}
