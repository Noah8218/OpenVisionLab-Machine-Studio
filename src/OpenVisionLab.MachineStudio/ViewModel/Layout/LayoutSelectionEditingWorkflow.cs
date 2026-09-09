using OpenVisionLab.MachineStudio.Model;

namespace OpenVisionLab.MachineStudio.ViewModel;

/// <summary>
/// Owns WPF-neutral selection editing state and geometry. The parent ViewModel
/// supplies the current selection and keeps responsibility for notifications,
/// project ownership, and edit-session presentation.
/// </summary>
internal sealed class LayoutSelectionEditingWorkflow
{
    private IReadOnlyDictionary<LayoutItem, (double X, double Y)>? _dragStartPositions;
    private LayoutTransformStart? _transformStart;

    internal bool BeginSelectionDrag(IEnumerable<LayoutItem> selectedItems, bool isEditable)
    {
        ArgumentNullException.ThrowIfNull(selectedItems);
        if (!isEditable || _dragStartPositions is not null || _transformStart is not null)
        {
            return false;
        }

        var selected = selectedItems.Where(item => item.Component is not null).ToArray();
        if (selected.Length == 0)
        {
            return false;
        }

        _dragStartPositions = selected.ToDictionary(item => item, item => (item.CurrentX, item.CurrentY));
        return true;
    }

    internal bool UpdateSelectionDrag(
        double deltaX,
        double deltaY,
        LayoutItem? primaryItem,
        bool snapToGrid,
        double gridSize)
    {
        if (_dragStartPositions is null || _dragStartPositions.Count == 0)
        {
            return false;
        }

        var primary = primaryItem is not null && _dragStartPositions.ContainsKey(primaryItem)
            ? primaryItem
            : _dragStartPositions.Keys.First();
        var primaryStart = _dragStartPositions[primary];
        var appliedX = snapToGrid
            ? SnapCoordinate(primaryStart.X + deltaX, gridSize) - primaryStart.X
            : deltaX;
        var appliedY = snapToGrid
            ? SnapCoordinate(primaryStart.Y + deltaY, gridSize) - primaryStart.Y
            : deltaY;

        foreach (var (item, start) in _dragStartPositions)
        {
            item.SetCurrentX(start.X + appliedX, snapToGrid: false);
            item.SetCurrentY(start.Y + appliedY, snapToGrid: false);
        }

        return true;
    }

    internal bool CompleteSelectionDrag()
    {
        if (_dragStartPositions is null)
        {
            return false;
        }

        var changed = _dragStartPositions.Any(entry =>
            entry.Key.CurrentX != entry.Value.X || entry.Key.CurrentY != entry.Value.Y);
        _dragStartPositions = null;
        return changed;
    }

    internal void CancelSelectionDrag()
    {
        if (_dragStartPositions is null)
        {
            return;
        }

        foreach (var (item, start) in _dragStartPositions)
        {
            item.SetCurrentX(start.X, snapToGrid: false);
            item.SetCurrentY(start.Y, snapToGrid: false);
        }

        _dragStartPositions = null;
    }

    internal bool BeginSelectionTransform(
        IEnumerable<LayoutItem> selectedItems,
        LayoutTransformHandle handle,
        bool isEditable)
    {
        ArgumentNullException.ThrowIfNull(selectedItems);
        if (!isEditable || _dragStartPositions is not null || _transformStart is not null)
        {
            return false;
        }

        var selected = selectedItems.Where(item => item.Component is not null).ToArray();
        if (selected.Length == 0)
        {
            return false;
        }

        var items = selected.Select(item => new LayoutTransformItemStart(
            item,
            item.CurrentX,
            item.CurrentY,
            item.CurrentWidth,
            item.CurrentHeight,
            item.CurrentRotationDegrees)).ToArray();
        var bounds = GetTransformBounds(items);
        _transformStart = new LayoutTransformStart(
            items,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            handle);
        return true;
    }

    internal bool UpdateSelectionTransform(
        double pointerX,
        double pointerY,
        bool snapToGrid,
        double gridSize,
        bool preserveAspectRatio = false)
    {
        if (_transformStart is not { } start ||
            !double.IsFinite(pointerX) ||
            !double.IsFinite(pointerY))
        {
            return false;
        }

        if (start.Items.Count == 1)
        {
            return UpdateSingleSelectionTransform(
                start,
                pointerX,
                pointerY,
                snapToGrid,
                gridSize,
                preserveAspectRatio);
        }

        return start.Handle == LayoutTransformHandle.Rotation
            ? UpdateGroupRotation(start, pointerX, pointerY)
            : UpdateGroupResize(start, pointerX, pointerY, snapToGrid, gridSize, preserveAspectRatio);
    }

    internal bool CompleteSelectionTransform()
    {
        if (_transformStart is not { } start)
        {
            return false;
        }

        var changed = start.Items.Any(item =>
            item.Item.CurrentX != item.X ||
            item.Item.CurrentY != item.Y ||
            item.Item.CurrentWidth != item.Width ||
            item.Item.CurrentHeight != item.Height ||
            item.Item.CurrentRotationDegrees != item.RotationDegrees);
        _transformStart = null;
        return changed;
    }

    internal void CancelSelectionTransform()
    {
        if (_transformStart is not { } start)
        {
            return;
        }

        SetTransformValues(start.Items.Select(item => new LayoutTransformValue(
            item.Item,
            item.X,
            item.Y,
            item.Width,
            item.Height,
            item.RotationDegrees)));
        _transformStart = null;
    }

    internal bool NudgeSelection(
        IEnumerable<LayoutItem> selectedItems,
        string direction,
        bool snapToGrid,
        double gridSize)
    {
        ArgumentNullException.ThrowIfNull(selectedItems);
        var step = snapToGrid ? gridSize : 1d;
        return direction switch
        {
            "Left" => MoveSelection(selectedItems, -step, 0),
            "Right" => MoveSelection(selectedItems, step, 0),
            "Up" => MoveSelection(selectedItems, 0, -step),
            "Down" => MoveSelection(selectedItems, 0, step),
            _ => false
        };
    }

    internal bool AlignSelection(
        IEnumerable<LayoutItem> selectedItems,
        LayoutItem? primary,
        LayoutSelectionAlignment alignment)
    {
        ArgumentNullException.ThrowIfNull(selectedItems);
        var selected = selectedItems.Where(item => item.Component is not null).ToArray();
        if (selected.Length < 2 || primary?.Component is null)
        {
            return false;
        }

        var (primaryHalfWidth, primaryHalfHeight) = GetRotatedHalfExtents(primary);
        var anchor = alignment switch
        {
            LayoutSelectionAlignment.Left => primary.CurrentX - primaryHalfWidth,
            LayoutSelectionAlignment.HorizontalCenter => primary.CurrentX,
            LayoutSelectionAlignment.Right => primary.CurrentX + primaryHalfWidth,
            LayoutSelectionAlignment.Top => primary.CurrentY - primaryHalfHeight,
            LayoutSelectionAlignment.VerticalCenter => primary.CurrentY,
            LayoutSelectionAlignment.Bottom => primary.CurrentY + primaryHalfHeight,
            _ => throw new ArgumentOutOfRangeException(nameof(alignment), alignment, null)
        };

        var changed = false;
        foreach (var item in selected)
        {
            var (halfWidth, halfHeight) = GetRotatedHalfExtents(item);
            switch (alignment)
            {
                case LayoutSelectionAlignment.Left:
                    changed |= SetX(item, anchor + halfWidth);
                    break;
                case LayoutSelectionAlignment.HorizontalCenter:
                    changed |= SetX(item, anchor);
                    break;
                case LayoutSelectionAlignment.Right:
                    changed |= SetX(item, anchor - halfWidth);
                    break;
                case LayoutSelectionAlignment.Top:
                    changed |= SetY(item, anchor + halfHeight);
                    break;
                case LayoutSelectionAlignment.VerticalCenter:
                    changed |= SetY(item, anchor);
                    break;
                case LayoutSelectionAlignment.Bottom:
                    changed |= SetY(item, anchor - halfHeight);
                    break;
            }
        }

        return changed;
    }

    internal bool CanChangeSelectionLayerOrder(
        IEnumerable<LayoutItem> allItems,
        IEnumerable<LayoutItem> selectedItems,
        LayoutLayerOrder order)
    {
        var (items, selected) = GetLayerOrderState(allItems, selectedItems);
        if (selected.Count == 0 || selected.Count == items.Count)
        {
            return false;
        }

        return order switch
        {
            LayoutLayerOrder.SendToBack or LayoutLayerOrder.SendBackward =>
                items.TakeWhile(selected.Contains).Count() != selected.Count,
            LayoutLayerOrder.BringForward or LayoutLayerOrder.BringToFront =>
                items.AsEnumerable().Reverse().TakeWhile(selected.Contains).Count() != selected.Count,
            _ => false
        };
    }

    internal bool ChangeSelectionLayerOrder(
        IEnumerable<LayoutItem> allItems,
        IEnumerable<LayoutItem> selectedItems,
        LayoutLayerOrder order)
    {
        if (!CanChangeSelectionLayerOrder(allItems, selectedItems, order))
        {
            return false;
        }

        var (items, selected) = GetLayerOrderState(allItems, selectedItems);
        switch (order)
        {
            case LayoutLayerOrder.SendToBack:
                items = items.Where(selected.Contains).Concat(items.Where(item => !selected.Contains(item))).ToList();
                break;
            case LayoutLayerOrder.BringToFront:
                items = items.Where(item => !selected.Contains(item)).Concat(items.Where(selected.Contains)).ToList();
                break;
            case LayoutLayerOrder.SendBackward:
                for (var index = 1; index < items.Count; index++)
                {
                    if (selected.Contains(items[index]) && !selected.Contains(items[index - 1]))
                    {
                        (items[index - 1], items[index]) = (items[index], items[index - 1]);
                    }
                }
                break;
            case LayoutLayerOrder.BringForward:
                for (var index = items.Count - 2; index >= 0; index--)
                {
                    if (selected.Contains(items[index]) && !selected.Contains(items[index + 1]))
                    {
                        (items[index], items[index + 1]) = (items[index + 1], items[index]);
                    }
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(order), order, null);
        }

        foreach (var (item, index) in items.Select((item, index) => (item, index)))
        {
            item.SetZIndex(index);
        }

        return true;
    }

    private bool UpdateSingleSelectionTransform(
        LayoutTransformStart start,
        double pointerX,
        double pointerY,
        bool snapToGrid,
        double gridSize,
        bool preserveAspectRatio)
    {
        var item = start.Items[0];
        if (start.Handle == LayoutTransformHandle.Rotation)
        {
            var deltaX = pointerX - item.X;
            var deltaY = pointerY - item.Y;
            if (Math.Abs(deltaX) < 0.000001d && Math.Abs(deltaY) < 0.000001d)
            {
                return false;
            }

            SetTransformValues(
                item.Item,
                item.X,
                item.Y,
                item.Width,
                item.Height,
                NormalizeRotation((Math.Atan2(deltaY, deltaX) * 180d / Math.PI) + 90d));
            return true;
        }

        var (signX, signY) = GetResizeSigns(start.Handle);
        var radians = item.RotationDegrees * Math.PI / 180d;
        var axisXX = Math.Cos(radians);
        var axisXY = Math.Sin(radians);
        var axisYX = -axisXY;
        var axisYY = axisXX;
        var fixedX = item.X - (signX * item.Width * axisXX / 2d) -
            (signY * item.Height * axisYX / 2d);
        var fixedY = item.Y - (signX * item.Width * axisXY / 2d) -
            (signY * item.Height * axisYY / 2d);
        var pointerDeltaX = pointerX - fixedX;
        var pointerDeltaY = pointerY - fixedY;
        var width = signX * ((pointerDeltaX * axisXX) + (pointerDeltaY * axisXY));
        var height = signY * ((pointerDeltaX * axisYX) + (pointerDeltaY * axisYY));
        var minimumSize = snapToGrid ? gridSize : 1d;
        if (preserveAspectRatio)
        {
            (width, height) = ConstrainAspectRatio(
                width,
                height,
                item.Width,
                item.Height,
                Math.Max(minimumSize / item.Width, minimumSize / item.Height),
                snapToGrid,
                gridSize);
        }
        else if (snapToGrid)
        {
            width = SnapCoordinate(width, gridSize);
            height = SnapCoordinate(height, gridSize);
        }
        width = Math.Max(minimumSize, width);
        height = Math.Max(minimumSize, height);

        SetTransformValues(
            item.Item,
            fixedX + (signX * width * axisXX / 2d) + (signY * height * axisYX / 2d),
            fixedY + (signX * width * axisXY / 2d) + (signY * height * axisYY / 2d),
            width,
            height,
            item.RotationDegrees);
        return true;
    }

    private bool UpdateGroupResize(
        LayoutTransformStart start,
        double pointerX,
        double pointerY,
        bool snapToGrid,
        double gridSize,
        bool preserveAspectRatio)
    {
        var (signX, signY) = GetResizeSigns(start.Handle);
        var fixedX = start.X - (signX * start.Width / 2d);
        var fixedY = start.Y - (signY * start.Height / 2d);
        var width = signX * (pointerX - fixedX);
        var height = signY * (pointerY - fixedY);
        var minimumSize = snapToGrid ? gridSize : 1d;
        var minimumWidth = start.Width * start.Items.Max(item => minimumSize / item.Width);
        var minimumHeight = start.Height * start.Items.Max(item => minimumSize / item.Height);
        if (preserveAspectRatio)
        {
            (width, height) = ConstrainAspectRatio(
                width,
                height,
                start.Width,
                start.Height,
                Math.Max(minimumWidth / start.Width, minimumHeight / start.Height),
                snapToGrid,
                gridSize);
        }
        else
        {
            if (snapToGrid)
            {
                width = SnapCoordinate(width, gridSize);
                height = SnapCoordinate(height, gridSize);
            }
            width = Math.Max(minimumWidth, width);
            height = Math.Max(minimumHeight, height);
        }
        var scaleX = width / start.Width;
        var scaleY = height / start.Height;

        SetTransformValues(start.Items.Select(item => new LayoutTransformValue(
            item.Item,
            fixedX + ((item.X - fixedX) * scaleX),
            fixedY + ((item.Y - fixedY) * scaleY),
            item.Width * scaleX,
            item.Height * scaleY,
            item.RotationDegrees)));
        return true;
    }

    private (double Width, double Height) ConstrainAspectRatio(
        double candidateWidth,
        double candidateHeight,
        double initialWidth,
        double initialHeight,
        double minimumScale,
        bool snapToGrid,
        double gridSize)
    {
        var scaleX = Math.Max(minimumScale, candidateWidth / initialWidth);
        var scaleY = Math.Max(minimumScale, candidateHeight / initialHeight);
        var useWidth = Math.Abs(scaleX - 1d) >= Math.Abs(scaleY - 1d);
        var scale = useWidth ? scaleX : scaleY;
        if (snapToGrid)
        {
            var initialPrimarySize = useWidth ? initialWidth : initialHeight;
            scale = Math.Max(
                minimumScale,
                SnapCoordinate(initialPrimarySize * scale, gridSize) / initialPrimarySize);
        }
        return (initialWidth * scale, initialHeight * scale);
    }

    private bool UpdateGroupRotation(
        LayoutTransformStart start,
        double pointerX,
        double pointerY)
    {
        var deltaX = pointerX - start.X;
        var deltaY = pointerY - start.Y;
        if (Math.Abs(deltaX) < 0.000001d && Math.Abs(deltaY) < 0.000001d)
        {
            return false;
        }

        var rotation = NormalizeRotation(
            (Math.Atan2(deltaY, deltaX) * 180d / Math.PI) + 90d);
        var radians = rotation * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        SetTransformValues(start.Items.Select(item =>
        {
            var x = item.X - start.X;
            var y = item.Y - start.Y;
            return new LayoutTransformValue(
                item.Item,
                start.X + (x * cosine) - (y * sine),
                start.Y + (x * sine) + (y * cosine),
                item.Width,
                item.Height,
                NormalizeRotation(item.RotationDegrees + rotation));
        }));
        return true;
    }

    private bool MoveSelection(IEnumerable<LayoutItem> selectedItems, double deltaX, double deltaY)
    {
        var selected = selectedItems.Where(item => item.Component is not null).ToArray();
        if (selected.Length == 0)
        {
            return false;
        }

        foreach (var item in selected)
        {
            item.SetCurrentX(item.CurrentX + deltaX, snapToGrid: false);
            item.SetCurrentY(item.CurrentY + deltaY, snapToGrid: false);
        }

        return true;
    }

    private static (List<LayoutItem> Items, HashSet<LayoutItem> Selected) GetLayerOrderState(
        IEnumerable<LayoutItem> allItems,
        IEnumerable<LayoutItem> selectedItems)
    {
        ArgumentNullException.ThrowIfNull(allItems);
        ArgumentNullException.ThrowIfNull(selectedItems);
        var items = allItems
            .Where(item => item.Component is not null)
            .OrderBy(item => item.ZIndex)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();
        return (items, selectedItems.Where(item => item.Component is not null).ToHashSet());
    }

    private static double SnapCoordinate(double value, double gridSize) =>
        Math.Round(value / gridSize, MidpointRounding.AwayFromZero) * gridSize;

    private static void SetTransformValues(
        LayoutItem item,
        double x,
        double y,
        double width,
        double height,
        double rotationDegrees)
    {
        item.SetCurrentX(x, snapToGrid: false);
        item.SetCurrentY(y, snapToGrid: false);
        item.CurrentWidth = width;
        item.CurrentHeight = height;
        item.CurrentRotationDegrees = rotationDegrees;
    }

    private static void SetTransformValues(IEnumerable<LayoutTransformValue> values)
    {
        foreach (var value in values)
        {
            value.Item.SetCurrentX(value.X, snapToGrid: false);
            value.Item.SetCurrentY(value.Y, snapToGrid: false);
            value.Item.CurrentWidth = value.Width;
            value.Item.CurrentHeight = value.Height;
            value.Item.CurrentRotationDegrees = value.RotationDegrees;
        }
    }

    private static (double SignX, double SignY) GetResizeSigns(LayoutTransformHandle handle) => handle switch
    {
        LayoutTransformHandle.TopLeft => (-1d, -1d),
        LayoutTransformHandle.TopRight => (1d, -1d),
        LayoutTransformHandle.BottomRight => (1d, 1d),
        LayoutTransformHandle.BottomLeft => (-1d, 1d),
        _ => throw new ArgumentOutOfRangeException(nameof(handle), handle, null)
    };

    private static double NormalizeRotation(double degrees)
    {
        var normalized = ((degrees % 360d) + 360d) % 360d;
        return normalized >= 180d ? normalized - 360d : normalized;
    }

    private static (double HalfWidth, double HalfHeight) GetRotatedHalfExtents(LayoutItem item) =>
        GetRotatedHalfExtents(item.Width, item.Height, item.RotationDegrees);

    private static (double HalfWidth, double HalfHeight) GetRotatedHalfExtents(
        double width,
        double height,
        double rotationDegrees)
    {
        var radians = rotationDegrees * Math.PI / 180d;
        var cosine = Math.Abs(Math.Cos(radians));
        var sine = Math.Abs(Math.Sin(radians));
        return (
            ((width * cosine) + (height * sine)) / 2d,
            ((width * sine) + (height * cosine)) / 2d);
    }

    private static (double X, double Y, double Width, double Height) GetTransformBounds(
        IReadOnlyList<LayoutTransformItemStart> items)
    {
        var minimumX = double.PositiveInfinity;
        var minimumY = double.PositiveInfinity;
        var maximumX = double.NegativeInfinity;
        var maximumY = double.NegativeInfinity;
        foreach (var item in items)
        {
            var (halfWidth, halfHeight) = GetRotatedHalfExtents(
                item.Width,
                item.Height,
                item.RotationDegrees);
            minimumX = Math.Min(minimumX, item.X - halfWidth);
            minimumY = Math.Min(minimumY, item.Y - halfHeight);
            maximumX = Math.Max(maximumX, item.X + halfWidth);
            maximumY = Math.Max(maximumY, item.Y + halfHeight);
        }
        return (
            (minimumX + maximumX) / 2d,
            (minimumY + maximumY) / 2d,
            maximumX - minimumX,
            maximumY - minimumY);
    }

    private static bool SetX(LayoutItem item, double value)
    {
        if (item.CurrentX == value)
        {
            return false;
        }
        item.SetCurrentX(value, snapToGrid: false);
        return true;
    }

    private static bool SetY(LayoutItem item, double value)
    {
        if (item.CurrentY == value)
        {
            return false;
        }
        item.SetCurrentY(value, snapToGrid: false);
        return true;
    }

    private sealed record LayoutTransformItemStart(
        LayoutItem Item,
        double X,
        double Y,
        double Width,
        double Height,
        double RotationDegrees);

    private sealed record LayoutTransformStart(
        IReadOnlyList<LayoutTransformItemStart> Items,
        double X,
        double Y,
        double Width,
        double Height,
        LayoutTransformHandle Handle);

    private sealed record LayoutTransformValue(
        LayoutItem Item,
        double X,
        double Y,
        double Width,
        double Height,
        double RotationDegrees);
}
