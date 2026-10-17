using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using OpenVisionLab.MachineStudio.ViewModel;

namespace OpenVisionLab.MachineStudio.View.Sequence;

public partial class SequenceTaskOverviewView : UserControl
{
    public static readonly DependencyProperty LaneWidthProperty = DependencyProperty.Register(nameof(LaneWidth), typeof(double), typeof(SequenceTaskOverviewView), new PropertyMetadata(1000d));
    private readonly Dictionary<Grid, string> _laneSignatures = new();
    private readonly HashSet<Border> _selectedCards = new();

    public SequenceTaskOverviewView() => InitializeComponent();

    public double LaneWidth { get => (double)GetValue(LaneWidthProperty); private set => SetValue(LaneWidthProperty, value); }

    private void OnLaneLoaded(object sender, RoutedEventArgs args)
    {
        _laneSignatures.TryAdd((Grid)sender, string.Empty);
        UpdateLane((Grid)sender);
    }

    private void OnFlowLayoutUpdated(object? sender, EventArgs args)
    {
        foreach (var surface in _laneSignatures.Keys.ToArray()) UpdateLane(surface);
    }

    private void OnLaneUnloaded(object sender, RoutedEventArgs args)
    {
        _laneSignatures.Remove((Grid)sender);
        _selectedCards.RemoveWhere(card => !card.IsLoaded);
    }

    private void OnCardSelectionUpdated(object sender, DataTransferEventArgs args)
    {
        if (sender is not Border card || args.Property != TagProperty) return;
        if (card.Tag is not true) { _selectedCards.Remove(card); return; }
        if (!_selectedCards.Add(card)) return;
        RevealCard(card);
    }

    private void OnCardLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Border { Tag: true } card) RevealCard(card);
    }

    private void OnCardUnloaded(object sender, RoutedEventArgs args) => _selectedCards.Remove((Border)sender);

    private void RevealCard(Border card)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!card.IsLoaded || card.Tag is not true) return;
            FlowViewport.UpdateLayout();
            if (Descendants(FlowViewport).OfType<ScrollContentPresenter>().FirstOrDefault(part => ReferenceEquals(part.TemplatedParent, FlowViewport)) is not { } viewport) return;
            Rect bounds = card.TransformToAncestor(viewport).TransformBounds(new Rect(card.RenderSize));
            if (Tag is true)
            {
                for (DependencyObject? owner = VisualTreeHelper.GetParent(card); owner is not null && owner != FlowViewport; owner = VisualTreeHelper.GetParent(owner))
                {
                    if (owner is not Border { Name: "FlowLaneFrame" } frame) continue;
                    Rect laneBounds = frame.TransformToAncestor(viewport).TransformBounds(new Rect(frame.RenderSize));
                    if (laneBounds.Width <= viewport.ActualWidth) bounds = new Rect(laneBounds.Left, bounds.Top, laneBounds.Width, bounds.Height);
                    break;
                }
            }
            double x = FlowViewport.HorizontalOffset, y = FlowViewport.VerticalOffset;
            if (bounds.Left < 0) x += bounds.Left;
            else if (bounds.Right > viewport.ActualWidth) x += bounds.Right - viewport.ActualWidth;
            if (bounds.Top < 0 || bounds.Height > viewport.ActualHeight) y += bounds.Top;
            else if (bounds.Bottom > viewport.ActualHeight) y += bounds.Bottom - viewport.ActualHeight;
            FlowViewport.ScrollToHorizontalOffset(Math.Clamp(x, 0, FlowViewport.ScrollableWidth));
            FlowViewport.ScrollToVerticalOffset(Math.Clamp(y, 0, FlowViewport.ScrollableHeight));
        }, DispatcherPriority.ContextIdle);
    }

    private void UpdateLane(Grid surface)
    {
        if (!surface.IsLoaded || surface.DataContext is not SequenceTaskLaneViewModel lane || surface.ActualWidth <= 0) return;
        bool allUnits = Tag is true;
        double width = allUnits ? 390 : Math.Max(350, FlowViewport.ViewportWidth / FlowScale.ScaleX - 20);
        if (double.IsFinite(width) && Math.Abs(width - LaneWidth) > 0.5) { LaneWidth = width; return; }
        var cards = Descendants(surface).OfType<Border>().Where(card => card.Name == "FlowNodeCard").ToArray();
        if (cards.Length == 0 || cards.Length != lane.Groups.Count || cards.Any(card => card.ActualHeight <= 0)) return;
        var orderedGroups = lane.Groups.ToArray();
        var order = orderedGroups.Select((group, index) => (group, index)).ToDictionary(item => item.group, item => item.index);
        var routes = lane.Routes.ToArray();
        bool linear = orderedGroups.Select((group, index) => group.Routes.Count > 0 && group.Routes.All(route => route.IsValid
            && (index == orderedGroups.Length - 1 ? route.IsTerminal : ReferenceEquals(route.Target, orderedGroups[index + 1])))).All(value => value);
        bool horizontal = FlowLayoutState.Tag is true;
        var sizes = cards.ToDictionary(card => (SequenceTaskGroupViewModel)card.DataContext,
            card => new Size(card.ActualWidth + card.Margin.Left + card.Margin.Right, card.ActualHeight + card.Margin.Top + card.Margin.Bottom));
        var positions = new Dictionary<SequenceTaskGroupViewModel, Point>();
        var ranks = orderedGroups.ToDictionary(group => group, _ => 0);
        double columnWidth = sizes.Values.Max(size => size.Width);
        double extentWidth = width, extentHeight = 0;
        if (linear)
        {
            int columns = Math.Max(1, (int)(width / columnWidth));
            for (int startIndex = 0; startIndex < orderedGroups.Length; startIndex += columns)
            {
                var row = orderedGroups.Skip(startIndex).Take(columns).ToArray();
                for (int col = 0; col < row.Length; col++) positions[row[col]] = new Point(col * columnWidth, extentHeight);
                extentHeight += row.Max(group => sizes[group].Height);
            }
        }
        else
        {
            // Backward/self routes remain drawable; only forward display edges contribute to finite ranks.
            var source = routes.ToDictionary(route => route, route => orderedGroups.First(group => group.Steps.Contains(route.Source)));
            var forward = routes.Where(route => route.IsValid && route.Target is not null && order[route.Target] > order[source[route]]).ToArray();
            var incoming = orderedGroups.ToDictionary(group => group, group => forward.Where(route => ReferenceEquals(route.Target, group)).ToArray());
            foreach (var group in orderedGroups)
                if (incoming[group].Length > 0) ranks[group] = incoming[group].Max(route => ranks[source[route]] + 1);
            var tracks = orderedGroups.ToDictionary(group => group, _ => 0d);
            foreach (var level in orderedGroups.GroupBy(group => ranks[group]).OrderBy(level => level.Key))
            {
                var preferred = level.ToDictionary(group => group, group => incoming[group].Length == 0 ? 0 : incoming[group].Average(route => tracks[source[route]]
                    + (route.PropertyName == nameof(SequenceStepEditorItem.FailureStepId) ? 1 : route.PropertyName == nameof(SequenceStepEditorItem.NextStepId) && route.Source.HasFailureRoute ? -1 : 0)));
                double right = double.NegativeInfinity;
                foreach (var group in level.OrderBy(group => preferred[group]).ThenBy(group => order[group]))
                {
                    tracks[group] = Math.Max(preferred[group], right + 1);
                    right = tracks[group];
                }
                double shift = level.Average(group => tracks[group] - preferred[group]);
                foreach (var group in level) tracks[group] -= shift;
            }
            double minimum = tracks.Values.Min(), rowHeight = sizes.Values.Max(size => size.Height);
            double y = 0;
            foreach (var level in orderedGroups.GroupBy(group => ranks[group]).OrderBy(level => level.Key))
            {
                foreach (var group in level) positions[group] = horizontal
                    ? new Point(ranks[group] * columnWidth, (tracks[group] - minimum) * rowHeight)
                    : new Point((tracks[group] - minimum) * columnWidth, y);
                y += level.Max(group => sizes[group].Height);
            }
            double gutter = 28 + orderedGroups.Max(group => group.Routes.Count) * 7;
            extentWidth = Math.Max(width, positions.Max(pair => pair.Value.X + sizes[pair.Key].Width) + (horizontal ? 0 : gutter));
            extentHeight = positions.Max(pair => pair.Value.Y + sizes[pair.Key].Height) + (horizontal ? gutter : 0);
        }
        var items = surface.Children.OfType<ItemsControl>().Single();
        bool arranged = false;
        if (Math.Abs(items.Width - extentWidth) > 0.5 || double.IsNaN(items.Width)) { items.Width = extentWidth; arranged = true; }
        if (Math.Abs(items.Height - extentHeight) > 0.5 || double.IsNaN(items.Height)) { items.Height = extentHeight; arranged = true; }
        foreach (var group in orderedGroups)
        {
            if (items.ItemContainerGenerator.ContainerFromItem(group) is not FrameworkElement container) return;
            Point position = positions[group];
            if (Canvas.GetLeft(container) != position.X) { Canvas.SetLeft(container, position.X); arranged = true; }
            if (Canvas.GetTop(container) != position.Y) { Canvas.SetTop(container, position.Y); arranged = true; }
        }
        var startMarker = surface.Children.OfType<TextBlock>().First();
        var startMargin = new Thickness(positions[orderedGroups[0]].X + 24, 8, 0, 0);
        if (startMarker.Margin != startMargin) { startMarker.Margin = startMargin; arranged = true; }
        if (arranged) return;
        var boxes = cards.ToDictionary(card => (SequenceTaskGroupViewModel)card.DataContext, card => card.TransformToAncestor(surface).TransformBounds(new Rect(card.RenderSize)));
        var markers = surface.Children.OfType<TextBlock>().ToArray();
        if (boxes.Count == 0 || markers.Length != 2) return;
        Rect start = markers[0].TransformToAncestor(surface).TransformBounds(new Rect(markers[0].RenderSize));
        Rect end = markers[1].TransformToAncestor(surface).TransformBounds(new Rect(markers[1].RenderSize));
        string signature = string.Join("|", boxes.Select(pair => pair.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            + end.ToString(System.Globalization.CultureInfo.InvariantCulture) + string.Join("|", lane.Routes.Select(route => route.Caption))
            + string.Join(",", lane.Groups.Select(group => group.IsSelected));
        if (_laneSignatures.GetValueOrDefault(surface) == signature) return;
        _laneSignatures[surface] = signature;
        var canvas = surface.Children.OfType<Canvas>().Single();
        canvas.Children.Clear();
        var labelBounds = new List<Rect>();
        Rect first = boxes[lane.Groups[0]];
        Draw(new[] { new Point(start.Left + start.Width / 2, start.Bottom), new Point(first.Left + first.Width / 2, first.Top) }, string.Empty, false, false);
        foreach (var group in orderedGroups)
        {
            Rect from = boxes[group];
            for (int index = 0; index < group.Routes.Count; index++)
            {
                var route = group.Routes[index];
                Rect to = route.IsTerminal ? end : route.Target is { } target && boxes.TryGetValue(target, out Rect box) ? box : Rect.Empty;
                if (!route.IsValid || to.IsEmpty) continue;
                bool failure = route.PropertyName == nameof(SequenceStepEditorItem.FailureStepId);
                double offset = failure ? 22 : route.PropertyName == nameof(SequenceStepEditorItem.NextStepId) && route.Source.HasFailureRoute ? -22 : 0;
                Point[] points;
                bool sameRow = Math.Abs(from.Top - to.Top) < 2;
                bool adjacent = route.Target is { } next && ReferenceEquals(orderedGroups.ElementAtOrDefault(order[group] + 1), next);
                bool nextRank = route.Target is { } targetGroup && ranks[targetGroup] == ranks[group] + 1;
                if (!linear && horizontal && nextRank && to.Left > from.Right)
                {
                    double x = (from.Right + to.Left) / 2;
                    points = [new(from.Right, from.Top + 40 + offset), new(x, from.Top + 40 + offset), new(x, to.Top + 40 + offset), new(to.Left, to.Top + 40 + offset)];
                }
                else if (!linear && horizontal && route.IsTerminal)
                {
                    double bottom = boxes.Values.Max(box => box.Bottom) + 14 + index * 7;
                    points = [new(from.Right, from.Top + 40), new(from.Right + 12, from.Top + 40), new(from.Right + 12, bottom), new(to.Left + to.Width / 2, bottom), new(to.Left + to.Width / 2, to.Top)];
                }
                else if (!linear && horizontal)
                {
                    double bottom = boxes.Values.Max(box => box.Bottom) + 14 + index * 7;
                    points = [new(from.Right, from.Top + 40 + offset), new(from.Right + 12, from.Top + 40 + offset), new(from.Right + 12, bottom), new(to.Left - 12, bottom), new(to.Left - 12, to.Top + 40 + offset), new(to.Left, to.Top + 40 + offset)];
                }
                else if (!linear && !horizontal && nextRank && to.Top > from.Bottom)
                {
                    double y = boxes.Where(pair => ranks[pair.Key] == ranks[group]).Max(pair => pair.Value.Bottom) + 14 + index * 7;
                    points = [new(from.Left + from.Width / 2 + offset, from.Bottom), new(from.Left + from.Width / 2 + offset, y), new(to.Left + to.Width / 2 + offset, y), new(to.Left + to.Width / 2 + offset, to.Top)];
                }
                else if (sameRow && adjacent && to.Left > from.Right)
                {
                    double x = (from.Right + to.Left) / 2;
                    points = [new(from.Right, from.Top + 40 + offset), new(x, from.Top + 40 + offset), new(x, to.Top + 40 + offset), new(to.Left, to.Top + 40 + offset)];
                }
                else if (to.Top >= from.Bottom && (adjacent || route.IsTerminal && ReferenceEquals(group, lane.Groups[^1])))
                {
                    double y = (from.Bottom + to.Top) / 2;
                    points = [new(from.Left + from.Width / 2 + offset, from.Bottom), new(from.Left + from.Width / 2 + offset, y), new(to.Left + to.Width / 2 + offset, y), new(to.Left + to.Width / 2 + offset, to.Top)];
                }
                else
                {
                    double bottom = boxes.Values.Where(box => Math.Abs(box.Top - from.Top) < 2).Max(box => box.Bottom) + 14 + index * 7;
                    double gutter = surface.ActualWidth - 7 - index * 6;
                    double top = Math.Max(3, to.Top - 12 - index * 6);
                    points = [new(from.Left + from.Width / 2 + offset, from.Bottom), new(from.Left + from.Width / 2 + offset, bottom), new(gutter, bottom), new(gutter, top), new(to.Left + to.Width / 2 + offset, top), new(to.Left + to.Width / 2 + offset, to.Top)];
                }
                Draw(points, route.IsTerminal ? string.Empty : route.Label, failure, group.IsSelected, route);
            }
        }

        void Draw(Point[] points, string label, bool dashed, bool selected, SequenceTaskRoute? route = null)
        {
            string brush = selected ? "Accent.Primary" : "Text.Secondary";
            var line = new Polyline { Points = new PointCollection(points), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round, Tag = route };
            line.SetResourceReference(Shape.StrokeProperty, brush);
            if (dashed) line.StrokeDashArray = new DoubleCollection { 3, 2 };
            canvas.Children.Add(line);
            Point tip = points[^1], previous = points.Reverse().Skip(1).FirstOrDefault(point => (tip - point).Length > 0.5);
            Vector direction = tip - previous;
            if (direction.Length > 0)
            {
                direction.Normalize();
                Vector side = new(-direction.Y, direction.X);
                var arrow = new Polyline { Points = new PointCollection { tip - direction * 6 + side * 3, tip, tip - direction * 6 - side * 3 }, StrokeThickness = 1.5 };
                arrow.SetResourceReference(Shape.StrokeProperty, brush);
                canvas.Children.Add(arrow);
            }
            if (label.Length == 0) return;
            var text = new TextBlock { Text = label, FontSize = 10, Padding = new Thickness(2, 0, 2, 0) };
            text.SetResourceReference(TextBlock.ForegroundProperty, brush);
            text.SetResourceReference(TextBlock.BackgroundProperty, "Surface.Panel");
            canvas.Children.Add(text);
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var bounds = new Rect(new Point(points[0].X + 3, points[0].Y + 2), text.DesiredSize);
            while (labelBounds.Any(previous => previous.IntersectsWith(bounds))) bounds.Offset(0, text.DesiredSize.Height + 2);
            Canvas.SetLeft(text, bounds.Left);
            Canvas.SetTop(text, bounds.Top);
            labelBounds.Add(bounds);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
