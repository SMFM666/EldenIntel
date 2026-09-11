using System;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;
using DrawingCopyPixelOperation = System.Drawing.CopyPixelOperation;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;
using FormsScreen = System.Windows.Forms.Screen;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfButton = System.Windows.Controls.Button;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfPoint = System.Windows.Point;

namespace EnemyIntelReader;

internal sealed class ScreenshotRegionWindow : Window
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmFlush();

    // Keep the original portrait aspect while reducing the capture footprint
    // by roughly 25 percent for a less intrusive on-screen selector.
    public const int CaptureWidthPixels = 488;
    public const int CaptureHeightPixels = 938;
    // Store portraits at EldenIntel's original/default asset resolution. The
    // on-screen selector remains 25% smaller so it does not dominate gameplay.
    public const int PortraitWidthPixels = 650;
    public const int PortraitHeightPixels = 1250;

    private readonly Canvas _canvas = new();
    private readonly Border _selection;
    private readonly Rect _virtualScreenPixels;
    private double _deviceToDipX = 1.0;
    private double _deviceToDipY = 1.0;
    private bool _isDragging;
    private WpfPoint _dragStart;
    private WpfPoint _selectionStart;

    private readonly string _captureDirectory;

    private ScreenshotRegionWindow(string captureDirectory)
    {
        _captureDirectory = captureDirectory;
        _virtualScreenPixels = GetVirtualScreenBounds();
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        // Keep the game at its true brightness while positioning the portrait
        // frame. The selector border and toolbar provide all necessary contrast.
        Background = MediaBrushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Content = _canvas;

        _selection = BuildSelection();
        _canvas.Children.Add(_selection);

        SourceInitialized += (_, _) => InitializePlacement();
        KeyDown += HandleKeyDown;
    }

    public string? CapturedPath { get; private set; }
    public string? CaptureError { get; private set; }

    public static string? Capture(Window owner, string captureDirectory, out string? error)
    {
        var window = new ScreenshotRegionWindow(captureDirectory)
        {
            Owner = owner
        };
        window.ShowDialog();
        error = window.CaptureError;
        return window.CapturedPath;
    }

    public static string? Capture(Window owner, out string? error) =>
        Capture(owner, Path.Combine(AppContext.BaseDirectory, "captures"), out error);

    private void InitializePlacement()
    {
        HwndSource? source = PresentationSource.FromVisual(this) as HwndSource;
        if (source?.CompositionTarget != null)
        {
            Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
            _deviceToDipX = fromDevice.M11;
            _deviceToDipY = fromDevice.M22;
        }

        Left = _virtualScreenPixels.Left * _deviceToDipX;
        Top = _virtualScreenPixels.Top * _deviceToDipY;
        Width = _virtualScreenPixels.Width * _deviceToDipX;
        Height = _virtualScreenPixels.Height * _deviceToDipY;

        _selection.Width = CaptureWidthPixels * _deviceToDipX;
        _selection.Height = CaptureHeightPixels * _deviceToDipY;

        double left = Math.Max(16, (Width - _selection.Width) / 2.0);
        double top = Math.Max(16, (Height - _selection.Height) / 2.0);
        Canvas.SetLeft(_selection, ClampLeft(left));
        Canvas.SetTop(_selection, ClampTop(top));
        Activate();
        Focus();
    }

    private Border BuildSelection()
    {
        var selection = new Border
        {
            CornerRadius = new CornerRadius(2),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(245, 197, 83)),
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(MediaColor.FromArgb(1, 255, 255, 255)),
            Cursor = WpfCursors.SizeAll
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        selection.Child = grid;

        var toolbar = new Border
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(MediaColor.FromArgb(220, 18, 18, 22)),
            Padding = new Thickness(9, 6, 9, 6)
        };

        var toolbarStack = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal
        };
        toolbar.Child = toolbarStack;

        toolbarStack.Children.Add(new TextBlock
        {
            Text = $"{CaptureWidthPixels} x {CaptureHeightPixels}",
            Foreground = MediaBrushes.White,
            FontFamily = new WpfFontFamily("Consolas"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        toolbarStack.Children.Add(ToolbarButton("SAVE", TryCaptureAndClose));
        toolbarStack.Children.Add(ToolbarButton("CANCEL", () => Close()));
        grid.Children.Add(toolbar);

        selection.MouseLeftButtonDown += (_, e) =>
        {
            _isDragging = true;
            _dragStart = e.GetPosition(_canvas);
            _selectionStart = new WpfPoint(Canvas.GetLeft(_selection), Canvas.GetTop(_selection));
            selection.CaptureMouse();
            e.Handled = true;
        };
        selection.MouseMove += (_, e) =>
        {
            if (!_isDragging)
            {
                return;
            }

            WpfPoint current = e.GetPosition(_canvas);
            MoveSelection(
                _selectionStart.X + current.X - _dragStart.X,
                _selectionStart.Y + current.Y - _dragStart.Y);
        };
        selection.MouseLeftButtonUp += (_, _) =>
        {
            _isDragging = false;
            selection.ReleaseMouseCapture();
        };

        return selection;
    }

    private static WpfButton ToolbarButton(string text, Action action)
    {
        var button = new WpfButton
        {
            Content = text,
            Foreground = MediaBrushes.White,
            Background = new SolidColorBrush(MediaColor.FromRgb(48, 48, 54)),
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(130, 255, 255, 255)),
            Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Cursor = WpfCursors.Hand
        };
        button.Click += (_, e) =>
        {
            e.Handled = true;
            action();
        };
        return button;
    }

    private void HandleKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            return;
        }

        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            TryCaptureAndClose();
            return;
        }

        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 10;
        double left = Canvas.GetLeft(_selection);
        double top = Canvas.GetTop(_selection);
        switch (e.Key)
        {
            case Key.Left:
                MoveSelection(left - step, top);
                break;
            case Key.Right:
                MoveSelection(left + step, top);
                break;
            case Key.Up:
                MoveSelection(left, top - step);
                break;
            case Key.Down:
                MoveSelection(left, top + step);
                break;
        }
    }

    private void MoveSelection(double left, double top)
    {
        Canvas.SetLeft(_selection, ClampLeft(left));
        Canvas.SetTop(_selection, ClampTop(top));
    }

    private double ClampLeft(double value)
    {
        return Math.Clamp(value, 0, Math.Max(0, ActualWidth - _selection.Width));
    }

    private double ClampTop(double value)
    {
        return Math.Clamp(value, 0, Math.Max(0, ActualHeight - _selection.Height));
    }

    private async Task CaptureAndCloseAsync()
    {
        Rect capture = GetCaptureBoundsPixels();
        // Keep the modal HWND alive, but remove every visible selector layer.
        // Awaiting between compositor barriers lets WPF's render thread publish
        // a genuinely clean desktop frame without hiding/closing the modal
        // window that owns the capture operation.
        Background = MediaBrushes.Transparent;
        _selection.Opacity = 0;
        IsHitTestVisible = false;
        UpdateLayout();
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        _ = DwmFlush();
        await Task.Delay(200);
        await Dispatcher.InvokeAsync(
            () => { },
            System.Windows.Threading.DispatcherPriority.Render);
        _ = DwmFlush();

        string folder = _captureDirectory;
        Directory.CreateDirectory(folder);
        string path = Path.Combine(
            folder,
            $"enemy_capture_{DateTime.Now:yyyyMMdd_HHmmss}_{PortraitWidthPixels}x{PortraitHeightPixels}.png");

        CaptureRegionToFile(capture, path);
        CapturedPath = path;
        Close();
    }

    private async void TryCaptureAndClose()
    {
        try
        {
            await CaptureAndCloseAsync();
        }
        catch (Exception exception)
        {
            CapturedPath = null;
            CaptureError = exception.Message;
            Close();
        }
    }

    private void CaptureRegionToFile(Rect capture, string path)
    {
        Exception? captureException = null;
        var captureThread = new Thread(() =>
        {
            try
            {
                using var sourceBitmap = new DrawingBitmap(CaptureWidthPixels, CaptureHeightPixels);
                using (DrawingGraphics graphics = DrawingGraphics.FromImage(sourceBitmap))
                {
                    graphics.Clear(DrawingColor.Black);
                    CopyVisibleScreenRegion(graphics, capture);
                }

                using var portrait = new DrawingBitmap(PortraitWidthPixels, PortraitHeightPixels);
                using (DrawingGraphics graphics = DrawingGraphics.FromImage(portrait))
                {
                    graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    graphics.DrawImage(
                        sourceBitmap,
                        new DrawingRectangle(0, 0, PortraitWidthPixels, PortraitHeightPixels),
                        new DrawingRectangle(0, 0, CaptureWidthPixels, CaptureHeightPixels),
                        System.Drawing.GraphicsUnit.Pixel);
                }

                portrait.Save(path, ImageFormat.Png);
            }
            catch (Exception exception)
            {
                captureException = exception;
            }
        })
        {
            IsBackground = true,
            Name = "EnemyIntelScreenshotCapture"
        };

        captureThread.Start();
        if (!captureThread.Join(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Screenshot capture timed out.");
        }

        if (captureException != null)
        {
            throw captureException;
        }
    }

    private void CopyVisibleScreenRegion(DrawingGraphics graphics, Rect capture)
    {
        DrawingRectangle source = new(
            (int)Math.Round(capture.Left),
            (int)Math.Round(capture.Top),
            CaptureWidthPixels,
            CaptureHeightPixels);
        DrawingRectangle desktop = new(
            (int)Math.Round(_virtualScreenPixels.Left),
            (int)Math.Round(_virtualScreenPixels.Top),
            (int)Math.Round(_virtualScreenPixels.Width),
            (int)Math.Round(_virtualScreenPixels.Height));
        DrawingRectangle visible = DrawingRectangle.Intersect(source, desktop);
        if (visible.Width <= 0 || visible.Height <= 0)
        {
            throw new InvalidOperationException("Capture area is outside the visible desktop.");
        }

        graphics.CopyFromScreen(
            visible.Left,
            visible.Top,
            visible.Left - source.Left,
            visible.Top - source.Top,
            new DrawingSize(visible.Width, visible.Height),
            DrawingCopyPixelOperation.SourceCopy);
    }

    private Rect GetCaptureBoundsPixels()
    {
        double leftDip = Canvas.GetLeft(_selection);
        double topDip = Canvas.GetTop(_selection);
        double left = _virtualScreenPixels.Left + leftDip / _deviceToDipX;
        double top = _virtualScreenPixels.Top + topDip / _deviceToDipY;
        return new Rect(left, top, CaptureWidthPixels, CaptureHeightPixels);
    }

    private static Rect GetVirtualScreenBounds()
    {
        int left = FormsScreen.AllScreens.Min(screen => screen.Bounds.Left);
        int top = FormsScreen.AllScreens.Min(screen => screen.Bounds.Top);
        int right = FormsScreen.AllScreens.Max(screen => screen.Bounds.Right);
        int bottom = FormsScreen.AllScreens.Max(screen => screen.Bounds.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }
}
