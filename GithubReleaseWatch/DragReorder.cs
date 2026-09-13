using System;
using System.Collections;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

// 消除 csproj 引入 UseWindowsForms 后的全局命名冲突
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Rect = System.Windows.Rect;
using Pen = System.Windows.Media.Pen;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ListBox = System.Windows.Controls.ListBox;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseButton = System.Windows.Input.MouseButton;

namespace GithubReleaseWatch
{
    /// <summary>
    /// WPF ListBox 拖拽排序（Trello/Notion 风格）：
    ///
    /// 拖动阶段：
    ///   - 源卡片 Visibility.Collapsed（**完全不占布局空间**，其余卡片自然紧凑排列，无洞）
    ///   - 鼠标位置显示半透明「幽灵卡片」+「横线 + 上下三角形」插入指示器
    ///   - 指示器 Y 坐标基于自然布局（Collapsed 后自动闭合）实时计算
    ///
    /// 松手阶段：
    ///   - 重新排列集合，源卡片落到目标位置
    ///   - 源卡片恢复可见，移除所有 Adorner
    /// </summary>
    internal sealed class ListBoxDragReorder
    {
        private readonly ListBox _listBox;
        private readonly IList _items;
        private readonly Action _persist;

        private ListBoxItem? _sourceItem;
        private int _sourceIndex = -1;
        private Point _startPoint;
        private double _itemHeight;
        private bool _dragging;
        private bool _suppressClick;

        private DragGhostAdorner? _ghost;
        private InsertIndicatorAdorner? _indicator;

        /// <summary>当前指示器所指的插入位（0..Count），基于不含源的自然布局。</summary>
        private int _currentSlot = -1;

        /// <summary>是否正在拖拽中。供外部在 SelectionChanged 等事件中判断，避免拖拽过程频繁刷新界面。</summary>
        public bool IsDragging => _dragging;

        public ListBoxDragReorder(ListBox listBox, IList items, Action persist)
        {
            _listBox = listBox;
            _items = items;
            _persist = persist;

            _listBox.PreviewMouseMove += OnListBoxMouseMove;
            _listBox.PreviewMouseLeftButtonUp += OnListBoxMouseUp;
        }

        /// <summary>挂在 ListBoxItem.PreviewMouseLeftButtonDown 上。</summary>
        public void OnItemMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBoxItem item) return;
            if (e.ChangedButton != MouseButton.Left) return;
            if (e.ClickCount >= 2) return;

            _sourceItem = item;
            _sourceIndex = _listBox.ItemContainerGenerator.IndexFromContainer(item);
            _startPoint = e.GetPosition(_listBox);
            _dragging = false;
            _suppressClick = false;
            _currentSlot = -1;
        }

        private void OnListBoxMouseMove(object sender, MouseEventArgs e)
        {
            if (_sourceItem is null) return;

            if (!_dragging)
            {
                var p0 = e.GetPosition(_listBox);
                if (Math.Abs(p0.Y - _startPoint.Y) < 5 && Math.Abs(p0.X - _startPoint.X) < 5) return;

                // 取源卡片高度作为指示器位移单位
                _itemHeight = _sourceItem.ActualHeight;
                if (_itemHeight <= 0)
                {
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (i == _sourceIndex) continue;
                        if (_listBox.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem other && other.ActualHeight > 0)
                        { _itemHeight = other.ActualHeight; break; }
                    }
                }
                if (_itemHeight <= 0) _itemHeight = 70;

                BeginDrag();
                _suppressClick = true;
            }

            var p = e.GetPosition(_listBox);
            _ghost?.Update(p);

            int slot = SlotForY(p.Y);
            if (slot != _currentSlot)
            {
                _currentSlot = slot;
                _indicator?.Update(slot);
            }
        }

        private void OnListBoxMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_sourceItem is null) return;
            if (_suppressClick) e.Handled = true;

            if (!_dragging)
            {
                _sourceItem = null; _sourceIndex = -1;
                return;
            }

            // 集合操作是 RemoveAt(src) + Insert(finalIndex, item)。
            // 不含源的自然布局里，slotNoSource 的取值范围恰好等于 finalIndex 的取值范围
            // （项数 - 1 相同，最大下标也相同），所以直接相等，无需换算。
            int src = _sourceIndex;
            int slotNoSource = SlotForY(e.GetPosition(_listBox).Y);
            int finalIndex = slotNoSource;

            EndDragVisuals();

            if (finalIndex != src)
            {
                var moved = _items[src]!;
                _items.RemoveAt(src);
                _items.Insert(finalIndex, moved);
                _persist?.Invoke();
            }

            _listBox.SelectedIndex = finalIndex;
            _listBox.ScrollIntoView(_listBox.SelectedItem);

            _sourceItem = null; _sourceIndex = -1; _suppressClick = false; _currentSlot = -1;
        }

        // ===================== 拖动阶段 =====================

        private void BeginDrag()
        {
            _dragging = true;
            var layer = AdornerLayer.GetAdornerLayer(_listBox);
            if (layer is null) return;

            // 关键顺序：先拍快照（源还可见），再 Collapsed，最后挂 Adorner
            _ghost = new DragGhostAdorner(_listBox, _sourceItem!, _startPoint);
            layer.Add(_ghost);

            // 源卡片 Collapsed：完全不占布局空间，其余卡片自动紧凑排列 —— 无洞
            _sourceItem!.Visibility = Visibility.Collapsed;

            _indicator = new InsertIndicatorAdorner(_listBox, _itemHeight);
            layer.Add(_indicator);
            _indicator.Update(SlotForY(_startPoint.Y));
        }

        private void EndDragVisuals()
        {
            var layer = AdornerLayer.GetAdornerLayer(_listBox);
            if (_ghost != null) { layer?.Remove(_ghost); _ghost = null; }
            if (_indicator != null) { layer?.Remove(_indicator); _indicator = null; }
            if (_sourceItem != null) _sourceItem.Visibility = Visibility.Visible;

            _dragging = false;
        }

        /// <summary>
        /// 把鼠标 Y 坐标映射为「不含源的自然布局下的插入位 slot」(0..Count-1)。
        /// 源卡片已 Collapsed，所以遍历时跳过源，其余卡片位置即视觉上的真实位置。
        /// </summary>
        private int SlotForY(double y)
        {
            int n = _listBox.Items.Count;
            int slot = n; // 默认：插入到末尾

            for (int i = 0; i < n; i++)
            {
                if (i == _sourceIndex) continue; // 源不参与判定
                if (_listBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem lbi) continue;
                if (lbi.Visibility != Visibility.Visible) continue;

                double top = lbi.TransformToAncestor(_listBox).Transform(new Point(0, 0)).Y;
                double mid = top + lbi.ActualHeight / 2;
                if (y < mid)
                {
                    slot = i; // 找到第一个 mid 在鼠标下方的可见卡片
                    break;
                }
            }

            return slot;
        }

        // ========================== Adorner ==========================

        /// <summary>跟随鼠标的半透明「幽灵卡片」—— 显示源卡片的真实内容快照。</summary>
        private sealed class DragGhostAdorner : Adorner
        {
            private readonly ListBoxItem _source;
            private readonly BitmapSource _snapshot; // 拖拽开始时拍的快照，源 Collapsed 后不变
            private readonly double _sourceWidth;
            private readonly double _sourceHeight;
            private Point _cursor;
            private const double GhostOpacity = 0.85;
            private static readonly Brush BorderBrush = Frozen(Color.FromArgb(220, 59, 130, 246));
            private static readonly Brush ShadowBrush = Frozen(Color.FromArgb(80, 0, 0, 0));

            public DragGhostAdorner(UIElement adorned, ListBoxItem source, Point cursor) : base(adorned)
            {
                _source = source;
                _cursor = cursor;
                IsHitTestVisible = false;

                // 拍快照必须在源 Collapsed 之前；宽度至少给个兜底（拖动前 ListBox 还在布局里）
                _sourceWidth = source.ActualWidth > 0 ? source.ActualWidth : 240;
                _sourceHeight = source.ActualHeight > 0 ? source.ActualHeight : 60;
                _snapshot = CaptureSnapshot(source, (int)Math.Ceiling(_sourceWidth), (int)Math.Ceiling(_sourceHeight));
            }

            public void Update(Point cursor)
            {
                _cursor = cursor;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                if (_snapshot == null) return;

                double w = _sourceWidth, h = _sourceHeight;
                // 画阴影
                var rect = new Rect(new Point(_cursor.X - 14, _cursor.Y - h / 2 - 6), new Size(w, h));
                dc.DrawRectangle(ShadowBrush, null,
                    new Rect(rect.X + 2, rect.Y + 6, rect.Width, rect.Height));

                // 画快照（半透明）
                dc.DrawImage(_snapshot, rect);

                // 画蓝色边框（凸显"正在拖"状态）
                dc.DrawRectangle(null, new Pen(BorderBrush, 2), rect);
            }

            /// <summary>把指定 Visual 渲染成 BitmapSource（用于源 Collapsed 后仍有内容可画）。</summary>
            private static BitmapSource CaptureSnapshot(Visual v, int pixelWidth, int pixelHeight)
            {
                try
                {
                    // VisualBrush 必须在 Visual 仍可见时采；这一步必须在源 Collapsed 前完成
                    var bmp = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(v);
                    bmp.Freeze();
                    return bmp;
                }
                catch
                {
                    return null!;
                }
            }
        }

        /// <summary>插入位置指示器：一条横线 + 上下两个三角形。</summary>
        private sealed class InsertIndicatorAdorner : Adorner
        {
            private double _itemHeight;
            private int _slot;
            private static readonly Brush LineBrush = Frozen(Color.FromRgb(59, 130, 246));

            public InsertIndicatorAdorner(UIElement adorned, double itemHeight) : base(adorned)
            {
                _itemHeight = itemHeight;
                IsHitTestVisible = false;
            }

            public void Update(int slot)
            {
                _slot = slot;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                var lb = (ListBox)AdornedElement;
                double y = YForSlot(lb, _slot);
                double w = lb.ActualWidth;

                // 横线：左右各留 6px 边距
                dc.DrawRectangle(LineBrush, null, new Rect(6, y - 1, Math.Max(0, w - 12), 2));

                // 三角形：横线居中
                double cx = w / 2;
                const double triW = 14, triH = 12;
                var up = new StreamGeometry();
                using (var ctx = up.Open())
                {
                    ctx.BeginFigure(new Point(cx, y - 2), true, true);
                    ctx.LineTo(new Point(cx - triW / 2, y - 2 - triH), true, false);
                    ctx.LineTo(new Point(cx + triW / 2, y - 2 - triH), true, false);
                }
                up.Freeze();
                dc.DrawGeometry(LineBrush, null, up);

                var down = new StreamGeometry();
                using (var ctx = down.Open())
                {
                    ctx.BeginFigure(new Point(cx, y + 2), true, true);
                    ctx.LineTo(new Point(cx - triW / 2, y + 2 + triH), true, false);
                    ctx.LineTo(new Point(cx + triW / 2, y + 2 + triH), true, false);
                }
                down.Freeze();
                dc.DrawGeometry(LineBrush, null, down);
            }

            /// <summary>计算 slot 对应的指示线 Y 坐标（基于不含源卡片的自然布局）。</summary>
            private double YForSlot(ListBox lb, int slot)
            {
                // 找 slot 位置「之后」的第一张可见卡片（指示线在其顶部）
                ListBoxItem? after = null;
                for (int i = slot; i < lb.Items.Count; i++)
                {
                    if (lb.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem lbi
                        && lbi.Visibility == Visibility.Visible && lbi.ActualHeight > 0)
                    { after = lbi; break; }
                }

                if (after != null)
                {
                    var p = after.TransformToAncestor(lb).Transform(new Point(0, 0));
                    // 指示线放在这张卡片的 top 边距处
                    return p.Y - 1;
                }

                // 没找到「之后」的卡片：放到最后一张可见卡片的底部
                for (int i = lb.Items.Count - 1; i >= 0; i--)
                {
                    if (lb.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem lbi
                        && lbi.Visibility == Visibility.Visible && lbi.ActualHeight > 0)
                    {
                        var p = lbi.TransformToAncestor(lb).Transform(new Point(0, 0));
                        return p.Y + lbi.ActualHeight + 1;
                    }
                }
                return 0;
            }
        }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}