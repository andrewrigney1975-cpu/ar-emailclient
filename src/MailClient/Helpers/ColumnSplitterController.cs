using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MailClient.Helpers;

/// Pointer-drag on a thin splitter element resizes an adjacent Grid ColumnDefinition
/// (ported from the file explorer project).
public sealed class ColumnSplitterController
{
    private readonly FrameworkElement _splitter;
    private readonly ColumnDefinition _column;
    private readonly bool _invert;
    private readonly double _min;
    private readonly double _max;
    private readonly Action<double>? _onResized;
    private bool _dragging;
    private double _startWidth;
    private Windows.Foundation.Point _startPoint;

    public ColumnSplitterController(FrameworkElement splitter, ColumnDefinition column, bool invert, double min, double max,
        Action<double>? onResized = null)
    {
        _splitter = splitter;
        _column = column;
        _invert = invert;
        _min = min;
        _max = max;
        _onResized = onResized;

        splitter.PointerPressed += OnPressed;
        splitter.PointerMoved += OnMoved;
        splitter.PointerReleased += OnReleased;
        splitter.PointerCaptureLost += (_, _) =>
        {
            if (_dragging)
            {
                _dragging = false;
                _onResized?.Invoke(_column.ActualWidth);
            }
        };
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _startWidth = _column.ActualWidth;
        _startPoint = e.GetCurrentPoint(null).Position;
        _splitter.CapturePointer(e.Pointer);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var delta = e.GetCurrentPoint(null).Position.X - _startPoint.X;
        if (_invert)
        {
            delta = -delta;
        }

        _column.Width = new GridLength(Math.Clamp(_startWidth + delta, _min, _max));
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            _onResized?.Invoke(_column.ActualWidth);
        }

        _splitter.ReleasePointerCapture(e.Pointer);
    }
}
