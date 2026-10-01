using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace FlowDesk
{
    internal static class DialogUi
    {
        internal static readonly Brush Background = Brush("#111B2C");
        internal static readonly Brush Panel = Brush("#18263B");
        internal static readonly Brush Text = Brush("#EAF2FA");
        internal static readonly Brush Muted = Brush("#A5B6CA");
        internal static readonly Brush Teal = Brush("#71E6D2");
        internal static readonly Brush Violet = Brush("#B2A7FF");

        internal static SolidColorBrush Brush(string color)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }

        internal static void Configure(Window window, string title, double width, double height)
        {
            window.Title = title;
            window.Width = width;
            window.Height = Math.Min(height, Math.Max(420, SystemParameters.WorkArea.Height - 50));
            window.MinWidth = Math.Min(width, 460);
            window.MinHeight = 420;
            window.Background = Background;
            window.Foreground = Text;
            window.FontFamily = new FontFamily("Microsoft YaHei UI");
            window.FontSize = 13;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.ShowInTaskbar = false;
            window.ResizeMode = ResizeMode.CanResize;
        }

        internal static TextBlock Label(string value, double size, Brush foreground)
        {
            return new TextBlock { Text = value, FontSize = size, Foreground = foreground,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        }

        internal static Button Button(string text, bool primary)
        {
            Button result = new Button { Content = text, MinHeight = 36,
                Padding = new Thickness(18, 8, 18, 8), Cursor = System.Windows.Input.Cursors.Hand,
                Background = primary ? Teal : Brush("#25374F"), Foreground = primary ? Background : Text,
                BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold };
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.AppendChild(content);
            ControlTemplate template = new ControlTemplate(typeof(Button));
            template.VisualTree = border;
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, .85));
            template.Triggers.Add(hover);
            Trigger pressed = new Trigger { Property = System.Windows.Controls.Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty, .68));
            template.Triggers.Add(pressed);
            result.Template = template;
            return result;
        }

        internal static TextBox Input(string value)
        {
            return new TextBox { Text = value, MinHeight = 36, VerticalContentAlignment = VerticalAlignment.Center,
                Background = Panel, Foreground = Text, CaretBrush = Teal,
                BorderBrush = Brush("#34465F"), BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 6, 10, 6), SelectionBrush = Brush("#356B70") };
        }

        internal static ComboBox Choice()
        {
            // The native ComboBox popup retains system contrast and keyboard navigation.
            return new ComboBox { MinHeight = 36, Background = Brush("#E3EBF3"), Foreground = Brush("#142237"),
                Padding = new Thickness(9, 5, 9, 5), VerticalContentAlignment = VerticalAlignment.Center };
        }

        internal static string Bytes(long bytes)
        {
            double value = Math.Max(0, bytes);
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            int unit = 0;
            while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
            return value.ToString(unit == 0 ? "0" : "0.##", CultureInfo.CurrentCulture) + " " + units[unit];
        }

        internal static Border Card(UIElement content)
        {
            return new Border { Background = Panel, CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16), Child = content };
        }
    }

    public sealed class SettingsDialog : Window
    {
        private readonly AppSettings original;
        private readonly ComboBox adapter;
        private readonly TextBox quota;
        private readonly TextBox billing;
        private readonly Slider opacity;
        private readonly CheckBox topmost;
        private readonly CheckBox startup;
        private readonly TextBlock error;
        public AppSettings Result { get; private set; }

        public SettingsDialog(AppSettings current, List<AdapterChoice> adapters, bool startupEnabled)
        {
            if (current == null) throw new ArgumentNullException("current");
            original = current;
            DialogUi.Configure(this, "流量浮窗 · 设置", 530, 680);

            Grid root = new Grid { Margin = new Thickness(26, 23, 26, 22) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            StackPanel heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            heading.Children.Add(DialogUi.Label("按你的习惯显示", 23, DialogUi.Text));
            TextBlock subtitle = DialogUi.Label("选择网络、设置月度额度，让流量一目了然。", 12, DialogUi.Muted);
            subtitle.Margin = new Thickness(0, 7, 0, 0);
            heading.Children.Add(subtitle);
            root.Children.Add(heading);

            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 10, 0) };
            Grid.SetRow(scroll, 1);
            StackPanel form = new StackPanel();
            scroll.Content = form;
            root.Children.Add(scroll);

            adapter = DialogUi.Choice();
            adapter.Items.Add(new ComboBoxItem { Content = "自动选择联网网卡", Tag = "auto" });
            int selected = 0;
            if (adapters != null)
            {
                foreach (AdapterChoice choice in adapters)
                {
                    if (choice == null || String.IsNullOrWhiteSpace(choice.Id) || choice.Id == "auto") continue;
                    adapter.Items.Add(new ComboBoxItem { Content = choice.Name, Tag = choice.Id });
                    if (String.Equals(choice.Id, current.AdapterId, StringComparison.OrdinalIgnoreCase)) selected = adapter.Items.Count - 1;
                }
            }
            if (selected == 0 && !String.IsNullOrWhiteSpace(current.AdapterId) && current.AdapterId != "auto")
            {
                adapter.Items.Add(new ComboBoxItem { Content = "已选网卡（当前不可用）", Tag = current.AdapterId });
                selected = adapter.Items.Count - 1;
            }
            adapter.SelectedIndex = selected;
            AddField(form, "统计网卡", adapter, "使用 VPN 时建议明确选择物理网卡，以免重复统计。");

            Grid quotaGrid = new Grid();
            quotaGrid.ColumnDefinitions.Add(new ColumnDefinition());
            quotaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            quotaGrid.ColumnDefinitions.Add(new ColumnDefinition());
            quota = DialogUi.Input(current.QuotaGB.ToString("0.##", CultureInfo.CurrentCulture));
            billing = DialogUi.Input(current.BillingDay.ToString(CultureInfo.InvariantCulture));
            StackPanel quotaGroup = new StackPanel();
            AddField(quotaGroup, "每月额度（GB）", quota, "设为 0 关闭额度提醒");
            StackPanel billGroup = new StackPanel();
            AddField(billGroup, "每月结算日", billing, "1–31 日");
            quotaGrid.Children.Add(quotaGroup);
            Grid.SetColumn(billGroup, 2);
            quotaGrid.Children.Add(billGroup);
            form.Children.Add(quotaGrid);
            TextBlock billNote = DialogUi.Label("月份没有所选日期时，按当月最后一天结算。额度在 80% 和 100% 时提醒。", 11, DialogUi.Muted);
            billNote.Margin = new Thickness(0, -5, 0, 20);
            form.Children.Add(billNote);

            Grid opacityHeader = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            opacityHeader.Children.Add(DialogUi.Label("浮窗不透明度", 13, DialogUi.Text));
            TextBlock percentage = DialogUi.Label("", 12, DialogUi.Teal);
            percentage.HorizontalAlignment = HorizontalAlignment.Right;
            opacityHeader.Children.Add(percentage);
            form.Children.Add(opacityHeader);
            opacity = new Slider { Minimum = 70, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true,
                Value = Math.Max(70, Math.Min(100, current.Opacity * 100)), Margin = new Thickness(0, 0, 0, 18),
                Foreground = DialogUi.Teal, MinWidth = 180 };
            percentage.Text = opacity.Value.ToString("0") + "%";
            opacity.ValueChanged += delegate { percentage.Text = opacity.Value.ToString("0") + "%"; };
            System.Windows.Automation.AutomationProperties.SetName(opacity, "浮窗不透明度，百分比");
            form.Children.Add(opacity);

            topmost = new CheckBox { Content = "浮窗始终置顶", IsChecked = current.TopMost,
                Foreground = DialogUi.Text, Margin = new Thickness(0, 0, 0, 15) };
            startup = new CheckBox { Content = "登录 Windows 后自动启动", IsChecked = startupEnabled,
                Foreground = DialogUi.Text, Margin = new Thickness(0, 0, 0, 20) };
            form.Children.Add(topmost);
            form.Children.Add(startup);
            StackPanel note = new StackPanel();
            note.Children.Add(DialogUi.Label("统计说明", 12, DialogUi.Teal));
            TextBlock noteText = DialogUi.Label("从首次运行开始记录，历史数据保存在此电脑。应用关闭时的流量不会补记。局域网传输也计入统计；结果可能与运营商账单不同。", 11, DialogUi.Muted);
            noteText.Margin = new Thickness(0, 7, 0, 0);
            noteText.LineHeight = 19;
            note.Children.Add(noteText);
            form.Children.Add(DialogUi.Card(note));

            StackPanel footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            error = DialogUi.Label("", 12, DialogUi.Brush("#FFADA9"));
            error.Visibility = Visibility.Collapsed;
            error.Margin = new Thickness(0, 0, 0, 10);
            footer.Children.Add(error);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button cancel = DialogUi.Button("取消", false);
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(0, 0, 10, 0);
            Button save = DialogUi.Button("保存设置", true);
            save.IsDefault = true;
            save.Click += Save;
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            footer.Children.Add(buttons);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
            Content = root;
        }

        private static void AddField(Panel panel, string name, Control control, string hint)
        {
            TextBlock label = DialogUi.Label(name, 13, DialogUi.Text);
            label.Margin = new Thickness(0, 0, 0, 8);
            panel.Children.Add(label);
            System.Windows.Automation.AutomationProperties.SetName(control, name);
            panel.Children.Add(control);
            TextBlock help = DialogUi.Label(hint, 11, DialogUi.Muted);
            help.Margin = new Thickness(0, 6, 0, 20);
            panel.Children.Add(help);
        }

        private void Save(object sender, RoutedEventArgs e)
        {
            double cap;
            int day;
            if (!Double.TryParse(quota.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out cap)
                || Double.IsNaN(cap) || Double.IsInfinity(cap) || cap < 0 || cap > 100000)
            {
                ShowError("每月额度请输入 0–100000 之间的数字。", quota);
                return;
            }
            if (!Int32.TryParse(billing.Text.Trim(), out day) || day < 1 || day > 31)
            {
                ShowError("结算日请输入 1–31 之间的整数。", billing);
                return;
            }
            ComboBoxItem chosen = adapter.SelectedItem as ComboBoxItem;
            Result = new AppSettings {
                AdapterId = chosen == null ? "auto" : Convert.ToString(chosen.Tag, CultureInfo.InvariantCulture),
                QuotaGB = cap, BillingDay = day, Opacity = opacity.Value / 100.0,
                TopMost = topmost.IsChecked == true, StartWithWindows = startup.IsChecked == true,
                Left = original.Left, Top = original.Top, Width = original.Width, Height = original.Height
            };
            DialogResult = true;
        }

        private void ShowError(string message, TextBox field)
        {
            error.Text = message;
            error.Visibility = Visibility.Visible;
            field.Focus();
            field.SelectAll();
            field.BringIntoView();
        }
    }

    public sealed class PackageDialog : Window
    {
        private readonly AppSettings original;
        private readonly TextBox quota;
        private readonly TextBox billing;
        private readonly TextBlock error;
        public AppSettings Result { get; private set; }

        public PackageDialog(AppSettings current, MonitorSnapshot snapshot)
        {
            if (current == null) throw new ArgumentNullException("current");
            original = current.Clone();
            DialogUi.Configure(this, "流量浮窗 · 更新套餐", 470, 455);

            Grid root = new Grid { Margin = new Thickness(26, 23, 26, 22) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            StackPanel heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            heading.Children.Add(DialogUi.Label("更新你的流量套餐", 23, DialogUi.Text));
            TextBlock subtitle = DialogUi.Label("修改额度和结算日，历史用量会完整保留。", 12, DialogUi.Muted);
            subtitle.Margin = new Thickness(0, 7, 0, 0);
            heading.Children.Add(subtitle);
            root.Children.Add(heading);

            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 10, 0) };
            Grid.SetRow(scroll, 1);
            StackPanel form = new StackPanel();
            scroll.Content = form;
            root.Children.Add(scroll);

            long used = snapshot == null ? 0 : SaturatingAdd(snapshot.CycleDownload, snapshot.CycleUpload);
            string usedText = DialogUi.Bytes(used);
            string capText = current.QuotaGB > 0 ? DialogUi.Bytes((long)Math.Min(long.MaxValue, current.QuotaGB * 1000000000d)) : "未设置";
            StackPanel summary = new StackPanel();
            summary.Children.Add(DialogUi.Label("当前账期", 12, DialogUi.Teal));
            TextBlock summaryValue = DialogUi.Label(usedText + " / " + capText, 22, DialogUi.Text);
            summaryValue.Margin = new Thickness(0, 7, 0, 0);
            summaryValue.FontWeight = FontWeights.SemiBold;
            summary.Children.Add(summaryValue);
            TextBlock summaryNote = DialogUi.Label("已用流量不会因更新套餐而清零。", 11, DialogUi.Muted);
            summaryNote.Margin = new Thickness(0, 6, 0, 0);
            summary.Children.Add(summaryNote);
            Border summaryCard = DialogUi.Card(summary);
            summaryCard.Margin = new Thickness(0, 0, 0, 18);
            form.Children.Add(summaryCard);

            Grid fields = new Grid();
            fields.ColumnDefinitions.Add(new ColumnDefinition());
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            fields.ColumnDefinitions.Add(new ColumnDefinition());
            quota = DialogUi.Input(current.QuotaGB.ToString("0.##", CultureInfo.CurrentCulture));
            billing = DialogUi.Input(current.BillingDay.ToString(CultureInfo.InvariantCulture));
            StackPanel quotaGroup = new StackPanel();
            TextBlock quotaLabel = DialogUi.Label("每月额度（GB）", 13, DialogUi.Text);
            quotaLabel.Margin = new Thickness(0, 0, 0, 8);
            quotaGroup.Children.Add(quotaLabel);
            System.Windows.Automation.AutomationProperties.SetName(quota, "每月额度（GB）");
            quotaGroup.Children.Add(quota);
            TextBlock quotaHint = DialogUi.Label("0 表示关闭提醒", 11, DialogUi.Muted);
            quotaHint.Margin = new Thickness(0, 6, 0, 0);
            quotaGroup.Children.Add(quotaHint);
            fields.Children.Add(quotaGroup);

            StackPanel billingGroup = new StackPanel();
            TextBlock billingLabel = DialogUi.Label("每月结算日", 13, DialogUi.Text);
            billingLabel.Margin = new Thickness(0, 0, 0, 8);
            billingGroup.Children.Add(billingLabel);
            System.Windows.Automation.AutomationProperties.SetName(billing, "每月结算日");
            billingGroup.Children.Add(billing);
            TextBlock billingHint = DialogUi.Label("1–31 日", 11, DialogUi.Muted);
            billingHint.Margin = new Thickness(0, 6, 0, 0);
            billingGroup.Children.Add(billingHint);
            Grid.SetColumn(billingGroup, 2);
            fields.Children.Add(billingGroup);
            form.Children.Add(fields);

            TextBlock note = DialogUi.Label("月份没有所选日期时，按当月最后一天结算。额度达到 80% 和 100% 时提醒。", 11, DialogUi.Muted);
            note.Margin = new Thickness(0, 14, 0, 8);
            note.LineHeight = 18;
            form.Children.Add(note);

            error = DialogUi.Label("", 12, DialogUi.Brush("#FFADA9"));
            error.Visibility = Visibility.Collapsed;
            error.Margin = new Thickness(0, 0, 0, 8);
            form.Children.Add(error);

            StackPanel footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button cancel = DialogUi.Button("取消", false);
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(0, 0, 10, 0);
            Button save = DialogUi.Button("更新套餐", true);
            save.IsDefault = true;
            save.Click += Save;
            footer.Children.Add(cancel);
            footer.Children.Add(save);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
            Content = root;
        }

        private static long SaturatingAdd(long first, long second)
        {
            if (first < 0) first = 0;
            if (second < 0) second = 0;
            return first > long.MaxValue - second ? long.MaxValue : first + second;
        }

        private void Save(object sender, RoutedEventArgs e)
        {
            double cap;
            int day;
            if (!Double.TryParse(quota.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out cap)
                || Double.IsNaN(cap) || Double.IsInfinity(cap) || cap < 0 || cap > 100000)
            {
                ShowError("每月额度请输入 0–100000 之间的数字。", quota);
                return;
            }
            if (!Int32.TryParse(billing.Text.Trim(), out day) || day < 1 || day > 31)
            {
                ShowError("结算日请输入 1–31 之间的整数。", billing);
                return;
            }
            Result = original.Clone();
            Result.QuotaGB = cap;
            Result.BillingDay = day;
            DialogResult = true;
        }

        private void ShowError(string message, TextBox field)
        {
            error.Text = message;
            error.Visibility = Visibility.Visible;
            field.Focus();
            field.SelectAll();
            field.BringIntoView();
        }
    }

    public sealed class HistoryDialog : Window
    {
        private sealed class Entry
        {
            internal DateTime Date;
            internal long Download;
            internal long Upload;
            internal long Total { get { return Download + Upload; } }
        }

        private readonly List<Entry> entries;
        private readonly Canvas chart;
        private readonly ComboBox period;
        private readonly TextBlock downloadTotal;
        private readonly TextBlock uploadTotal;
        private readonly TextBlock overallTotal;
        private readonly TextBlock chartHint;

        public HistoryDialog(MonitorSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            DialogUi.Configure(this, "流量浮窗 · 历史记录", 750, 760);
            entries = new List<Entry>();
            if (snapshot.History != null)
            {
                Dictionary<DateTime, Entry> grouped = new Dictionary<DateTime, Entry>();
                foreach (UsageDay day in snapshot.History)
                {
                    DateTime date;
                    if (day == null || !DateTime.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out date)) continue;
                    Entry entry;
                    if (!grouped.TryGetValue(date, out entry))
                    {
                        entry = new Entry { Date = date };
                        grouped.Add(date, entry);
                    }
                    entry.Download += Math.Max(0, day.Download);
                    entry.Upload += Math.Max(0, day.Upload);
                }
                entries.AddRange(grouped.Values.OrderByDescending(x => x.Date));
            }

            Grid root = new Grid { Margin = new Thickness(26, 22, 26, 20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid header = new Grid { Margin = new Thickness(0, 0, 0, 20) };
            StackPanel titles = new StackPanel();
            titles.Children.Add(DialogUi.Label("看看流量都去哪天了", 23, DialogUi.Text));
            TextBlock subtitle = DialogUi.Label("每天的上传与下载，保存在这台电脑。", 12, DialogUi.Muted);
            subtitle.Margin = new Thickness(0, 7, 0, 0);
            titles.Children.Add(subtitle);
            header.Children.Add(titles);
            period = DialogUi.Choice();
            period.Items.Add("最近 14 天");
            period.Items.Add("最近 30 天");
            period.SelectedIndex = 0;
            period.Width = 116;
            period.HorizontalAlignment = HorizontalAlignment.Right;
            period.VerticalAlignment = VerticalAlignment.Center;
            System.Windows.Automation.AutomationProperties.SetName(period, "趋势时间范围");
            header.Children.Add(period);
            root.Children.Add(header);

            Grid body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            Grid metrics = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            metrics.ColumnDefinitions.Add(new ColumnDefinition());
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            metrics.ColumnDefinitions.Add(new ColumnDefinition());
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            metrics.ColumnDefinitions.Add(new ColumnDefinition());
            downloadTotal = Metric(metrics, 0, "↓ 下载", DialogUi.Teal);
            uploadTotal = Metric(metrics, 2, "↑ 上传", DialogUi.Violet);
            overallTotal = Metric(metrics, 4, "期间合计", DialogUi.Text);
            body.Children.Add(metrics);

            StackPanel chartContainer = new StackPanel();
            chartHint = DialogUi.Label("", 11, DialogUi.Muted);
            chartHint.Margin = new Thickness(0, 0, 0, 8);
            chartContainer.Children.Add(chartHint);
            chart = new Canvas { Height = 170, ClipToBounds = true };
            chart.SizeChanged += delegate { DrawChart(); };
            chartContainer.Children.Add(chart);
            Border chartCard = DialogUi.Card(chartContainer);
            chartCard.Margin = new Thickness(0, 0, 0, 20);
            Grid.SetRow(chartCard, 1);
            body.Children.Add(chartCard);

            Grid tableHeader = TableLine("日期", "下载", "上传", "合计", true, false);
            tableHeader.Margin = new Thickness(0, 0, 0, 5);
            Grid.SetRow(tableHeader, 2);
            body.Children.Add(tableHeader);
            ScrollViewer rowsScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MinHeight = 40 };
            StackPanel rows = new StackPanel();
            if (entries.Count == 0)
            {
                TextBlock empty = DialogUi.Label("还没有历史记录。保持浮窗运行，新的用量会自动记录在这里。", 13, DialogUi.Muted);
                empty.Margin = new Thickness(12, 20, 12, 20);
                rows.Children.Add(empty);
            }
            else
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    Entry entry = entries[i];
                    string date = entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (entry.Date == DateTime.Today) date += " · 今天";
                    rows.Children.Add(TableLine(date, DialogUi.Bytes(entry.Download), DialogUi.Bytes(entry.Upload), DialogUi.Bytes(entry.Total), false, i % 2 == 0));
                }
            }
            rowsScroll.Content = rows;
            Grid.SetRow(rowsScroll, 3);
            body.Children.Add(rowsScroll);

            Grid footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            TextBlock footnote = DialogUi.Label("仅统计应用运行期间的流量。1 GB = 1,000,000,000 字节。", 11, DialogUi.Muted);
            footnote.MaxWidth = 290;
            footnote.HorizontalAlignment = HorizontalAlignment.Left;
            footer.Children.Add(footnote);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button export = DialogUi.Button("导出 CSV", false);
            export.Margin = new Thickness(0, 0, 10, 0);
            export.Click += Export;
            Button close = DialogUi.Button("完成", true);
            close.IsCancel = true;
            close.Click += delegate { Close(); };
            buttons.Children.Add(export);
            buttons.Children.Add(close);
            footer.Children.Add(buttons);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
            period.SelectionChanged += delegate { DrawChart(); };
            Content = root;
            Loaded += delegate { DrawChart(); };
        }

        private static TextBlock Metric(Grid parent, int column, string label, Brush accent)
        {
            StackPanel panel = new StackPanel();
            panel.Children.Add(DialogUi.Label(label, 12, accent));
            TextBlock value = DialogUi.Label("0 B", 24, DialogUi.Text);
            value.Margin = new Thickness(0, 8, 0, 0);
            value.FontWeight = FontWeights.SemiBold;
            panel.Children.Add(value);
            Border card = DialogUi.Card(panel);
            Grid.SetColumn(card, column);
            parent.Children.Add(card);
            return value;
        }

        private static Grid TableLine(string date, string download, string upload, string total, bool header, bool alternate)
        {
            Grid line = new Grid { Background = alternate ? DialogUi.Panel : Brushes.Transparent,
                MinHeight = header ? 30 : 41, Margin = new Thickness(0, 0, 4, 1) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition());
            string[] values = { date, download, upload, total };
            for (int i = 0; i < values.Length; i++)
            {
                TextBlock text = DialogUi.Label(values[i], header ? 11 : 12,
                    header ? DialogUi.Muted : (i == 1 ? DialogUi.Teal : (i == 2 ? DialogUi.Violet : DialogUi.Text)));
                text.Margin = new Thickness(10, 7, 10, 7);
                text.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                if (i == 3 && !header) text.FontWeight = FontWeights.SemiBold;
                Grid.SetColumn(text, i);
                line.Children.Add(text);
            }
            return line;
        }

        private void DrawChart()
        {
            if (chart == null) return;
            chart.Children.Clear();
            int count = period.SelectedIndex == 1 ? 30 : 14;
            DateTime first = DateTime.Today.AddDays(1 - count);
            List<Entry> span = entries.Where(x => x.Date >= first && x.Date <= DateTime.Today).ToList();
            long downloaded = span.Sum(x => x.Download);
            long uploaded = span.Sum(x => x.Upload);
            downloadTotal.Text = DialogUi.Bytes(downloaded);
            uploadTotal.Text = DialogUi.Bytes(uploaded);
            overallTotal.Text = DialogUi.Bytes(downloaded + uploaded);
            long maximum = span.Count == 0 ? 0 : span.Max(x => x.Total);
            chartHint.Text = maximum == 0 ? "暂无流量 · 新记录会出现在这里" : "每日用量 · 峰值 " + DialogUi.Bytes(maximum) + " · 悬停查看明细";
            double width = chart.ActualWidth;
            if (width < 50) return;
            const double baseY = 143;
            const double plotHeight = 122;
            for (int line = 0; line < 3; line++)
            {
                double y = baseY - plotHeight * line / 2;
                Line guide = new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y,
                    Stroke = DialogUi.Brush("#2B3A50"), StrokeThickness = 1 };
                chart.Children.Add(guide);
            }
            double step = width / count;
            double barWidth = Math.Max(3, Math.Min(26, step - 7));
            for (int i = 0; i < count; i++)
            {
                DateTime date = first.AddDays(i);
                Entry entry = span.FirstOrDefault(x => x.Date == date);
                long down = entry == null ? 0 : entry.Download;
                long up = entry == null ? 0 : entry.Upload;
                double downHeight = maximum == 0 ? 0 : plotHeight * down / maximum;
                double upHeight = maximum == 0 ? 0 : plotHeight * up / maximum;
                string tooltip = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\n下载  " + DialogUi.Bytes(down)
                    + "\n上传  " + DialogUi.Bytes(up) + "\n合计  " + DialogUi.Bytes(down + up);
                double barX = i * step + (step - barWidth) / 2;
                Rectangle downBar = new Rectangle { Width = barWidth, Height = downHeight,
                    Fill = DialogUi.Teal, ToolTip = tooltip, RadiusX = 2, RadiusY = 2 };
                Canvas.SetLeft(downBar, barX);
                Canvas.SetTop(downBar, baseY - downHeight);
                chart.Children.Add(downBar);
                Rectangle upBar = new Rectangle { Width = barWidth, Height = upHeight,
                    Fill = DialogUi.Violet, ToolTip = tooltip, RadiusX = 2, RadiusY = 2 };
                Canvas.SetLeft(upBar, barX);
                Canvas.SetTop(upBar, baseY - downHeight - upHeight);
                chart.Children.Add(upBar);
                Rectangle hover = new Rectangle { Width = step, Height = baseY, Fill = Brushes.Transparent, ToolTip = tooltip };
                Canvas.SetLeft(hover, i * step);
                chart.Children.Add(hover);
                if (i == 0 || i == count - 1 || i % (count == 14 ? 3 : 7) == 0)
                {
                    TextBlock label = DialogUi.Label(i == count - 1 ? "今天" : date.ToString("M/d", CultureInfo.InvariantCulture), 10, DialogUi.Muted);
                    label.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
                    Canvas.SetLeft(label, Math.Max(0, Math.Min(width - label.DesiredSize.Width, i * step + step / 2 - label.DesiredSize.Width / 2)));
                    Canvas.SetTop(label, 151);
                    chart.Children.Add(label);
                }
            }
        }

        private void Export(object sender, RoutedEventArgs e)
        {
            SaveFileDialog dialog = new SaveFileDialog { Title = "导出流量历史", Filter = "CSV 表格 (*.csv)|*.csv",
                FileName = "FlowDesk-流量历史-" + DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv",
                DefaultExt = ".csv", AddExtension = true, OverwritePrompt = true };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                StringBuilder csv = new StringBuilder("日期,下载（字节）,上传（字节）,合计（字节）\r\n");
                foreach (Entry row in entries.OrderBy(x => x.Date))
                {
                    csv.Append(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                        .Append(row.Download.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(row.Upload.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(row.Total.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                }
                File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
                MessageBox.Show(this, "历史记录已导出。", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法保存文件：" + ex.Message, "导出未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
