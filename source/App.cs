using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace LightNote {
 public class App : Application {
  Store store; State data; Note current; Window main;
  TextBox title, body; TextBlock status, count, reminder;
  Button pin; System.Windows.Controls.Border strip; ListBox notesList; bool syncingList;
  Forms.NotifyIcon tray; System.Drawing.Icon trayIcon;
  DispatcherTimer debounce, clock; bool loading, exiting;
  Dictionary<string, Window> alerts = new Dictionary<string, Window>();
  static Mutex mutex; static EventWaitHandle showEvent;
  const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
  const string StartupName = "LightNote";
  static readonly string[] Colors = { "MemoYellow", "MemoBlue", "MemoGreen" };

  [STAThread] public static void Main(string[] args) {
   // This small text utility needs no GPU effects; software rendering also avoids
   // blank WPF surfaces with some display drivers and remote desktop sessions.
   RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
   string folder = Arg(args, "--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightNote");
   if (args.Contains("--self-test")) { SelfTest.Run(folder); return; }
   string key;
   using (var hash = SHA256.Create()) { key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).ToUpperInvariant()))).Replace("-", "").Substring(0, 20); }
   bool created;
   mutex = new Mutex(true, "Local\\LightNote-" + key, out created);
   showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\LightNote-show-" + key);
   if (!created) { showEvent.Set(); mutex.Dispose(); showEvent.Dispose(); return; }
   try {
    var app = new App();
    app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
    app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e) { MessageBox.Show("操作未完成：" + e.Exception.Message, "轻笺", MessageBoxButton.OK, MessageBoxImage.Error); e.Handled = true; };
    app.Initialize(folder, args.Contains("--demo-reminder"));
    app.SessionEnding += delegate(object s, SessionEndingCancelEventArgs e) { app.PullGeometry(); try { app.store.Save(app.data); } catch { e.Cancel = true; } };
    app.Run();
   } catch (Exception ex) { MessageBox.Show(ex.Message, "轻笺无法启动", MessageBoxButton.OK, MessageBoxImage.Error); }
   finally { showEvent.Dispose(); mutex.ReleaseMutex(); mutex.Dispose(); }
  }
  static string Arg(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
  T Find<T>(string name) where T : class { return main.FindName(name) as T; }
  void Initialize(string folder, bool demo) {
   store = new Store(folder); data = store.Load();
   if (!data.Notes.Any(n => n.DeletedUtc == 0)) data.Notes.Add(new Note());
   current = data.Notes.FirstOrDefault(n => n.Id == data.CurrentId && n.DeletedUtc == 0) ?? data.Notes.First(n => n.DeletedUtc == 0);
   using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) main = (Window)XamlReader.Load(stream);
   MainWindow = main;
   using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LightNote.png")) main.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
   title = Find<TextBox>("TitleBox"); body = Find<TextBox>("BodyBox"); status = Find<TextBlock>("SaveText"); count = Find<TextBlock>("CountText");
   reminder = Find<TextBlock>("ReminderText"); pin = Find<Button>("PinButton"); strip = Find<System.Windows.Controls.Border>("ColorStrip");
   notesList = Find<ListBox>("NotesList");
   notesList.SelectionChanged += delegate {
    if (syncingList || notesList.SelectedItem == null) return;
    var n = (Note)((ListBoxItem)notesList.SelectedItem).Tag;
    if (n != current) { SelectNote(n); if (current != n) RebuildSidebar(); }
   };
   if (data.LayoutVersion < 2) { data.Width = 580; data.Height = 430; data.LayoutVersion = 2; }
   main.Width = SafeSize(data.Width, 500, 1300, 580); main.Height = SafeSize(data.Height, 340, 1200, 430);
   if (Forms.Screen.AllScreens.Any(s => s.WorkingArea.Contains((int)data.Left, (int)data.Top) && s.WorkingArea.Contains((int)(data.Left + 100), (int)(data.Top + 60)))) { main.WindowStartupLocation = WindowStartupLocation.Manual; main.Left = data.Left; main.Top = data.Top; }
   debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
   debounce.Tick += delegate { debounce.Stop(); Save(); };
   title.TextChanged += Edited; body.TextChanged += Edited;
   Find<Button>("NewButton").Click += delegate { NewNote(); };
   Find<Button>("MoreButton").Click += delegate { ShowMore(); };
   pin.Click += delegate { current.Pinned = !current.Pinned; RefreshPin(); Changed(); };
   Find<Button>("ReminderButton").Click += delegate { EditReminder(); };
   main.PreviewKeyDown += delegate(object s, KeyEventArgs e) {
    if (Keyboard.Modifiers != ModifierKeys.Control) return;
    if (e.Key == Key.N) NewNote(); else if (e.Key == Key.L) ShowList(); else if (e.Key == Key.T) { current.Pinned = !current.Pinned; RefreshPin(); Changed(); } else if (e.Key == Key.S) Save(); else return;
    e.Handled = true;
   };
   main.Closing += delegate(object s, System.ComponentModel.CancelEventArgs e) { if (exiting) return; e.Cancel = true; PullGeometry(); if (Save()) main.Hide(); };
   main.StateChanged += delegate { if (main.WindowState == WindowState.Minimized) { PullGeometry(); if (Save()) main.Hide(); } };
   main.LocationChanged += delegate { if (main.IsVisible && main.WindowState == WindowState.Normal) Changed(); };
   main.SizeChanged += delegate { if (main.IsVisible && main.WindowState == WindowState.Normal) Changed(); };
   SetupTray(); Bind(); main.Show();
   if (demo) { current.Title = "提醒测试"; current.Body = "这是一条隔离测试便笺。\n提醒会在启动约 20 秒后出现。"; current.DueUtc = DateTime.UtcNow.AddSeconds(20).Ticks; Bind(); Changed(); }
   if (store.Recovered) MessageBox.Show(main, store.LoadNotice, "已恢复便笺", MessageBoxButton.OK, MessageBoxImage.Information);
   clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
   clock.Tick += delegate { if (showEvent.WaitOne(0)) ShowMain(); CheckReminders(); };
   clock.Start(); Save();
  }
  static double SafeSize(double v, double min, double max, double fallback) { return Double.IsNaN(v) || Double.IsInfinity(v) ? fallback : Math.Max(min, Math.Min(max, v)); }
  void Edited(object sender, TextChangedEventArgs e) { if (loading) return; current.Title = title.Text; current.Body = body.Text; count.Text = body.Text.Length + " 字"; UpdateSidebarText(); Changed(); }
  void Changed() { if (loading) return; status.Text = "保存中…"; status.Foreground = Theme("Muted"); debounce.Stop(); debounce.Start(); }
  void PullGeometry() { if (main != null && main.WindowState == WindowState.Normal) { data.Left = main.Left; data.Top = main.Top; data.Width = main.Width; data.Height = main.Height; } }
  bool Save() {
   debounce.Stop(); PullGeometry(); data.CurrentId = current.Id;
   try { store.Save(data); status.Text = "已保存"; status.Foreground = Theme("Muted"); status.ToolTip = "便笺自动保存在本机。关闭窗口后仍在托盘运行。"; return true; }
   catch (Exception ex) { status.Text = "保存失败 · Ctrl+S 重试"; status.Foreground = Theme("ErrorInk"); status.ToolTip = ex.Message; return false; }
  }
  void Bind() { loading = true; data.CurrentId = current.Id; title.Text = current.Title ?? ""; body.Text = current.Body ?? ""; count.Text = body.Text.Length + " 字"; strip.Background = Theme(Colors[Math.Abs(current.Color % Colors.Length)]); RefreshPin(); RefreshReminder(); RebuildSidebar(); loading = false; }
  static string Summary(Note n) { return String.IsNullOrWhiteSpace(n.Body) ? "还没有内容" : n.Body.Replace("\r", " ").Replace("\n", " ").Replace("\t", " "); }
  void UpdateSidebarText() {
   foreach (ListBoxItem item in notesList.Items) {
    var n = (Note)item.Tag;
    var panel = (StackPanel)item.Content; ((TextBlock)panel.Children[0]).Text = n.Label; ((TextBlock)panel.Children[1]).Text = Summary(n);
    System.Windows.Automation.AutomationProperties.SetName(item, n.Label); item.ToolTip = n.Label;
   }
  }
  void RebuildSidebar() {
   syncingList = true;
   try {
    var visible = data.Notes.Where(n => n.DeletedUtc == 0).ToArray();
    if (notesList.Items.Count == visible.Length && notesList.Items.Cast<ListBoxItem>().Select(i => ((Note)i.Tag).Id).SequenceEqual(visible.Select(n => n.Id))) {
     UpdateSidebarText(); notesList.SelectedItem = notesList.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.Tag == current); return;
    }
    notesList.Items.Clear();
    foreach (var n in visible) {
     var selected = n; var panel = new StackPanel();
     panel.Children.Add(new TextBlock { Text = n.Label, FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = Theme("Ink"), TextTrimming = TextTrimming.CharacterEllipsis });
     panel.Children.Add(new TextBlock { Text = Summary(n), FontSize = 11, Foreground = Theme("Muted"), Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
     var item = new ListBoxItem { Content = panel, Tag = n, ToolTip = n.Label };
     System.Windows.Automation.AutomationProperties.SetName(item, n.Label);
     var menu = new System.Windows.Controls.ContextMenu(); menu.Items.Add(Item("删除便笺", delegate { DeleteNote(selected); })); item.ContextMenu = menu;
     notesList.Items.Add(item); if (n == current) notesList.SelectedItem = item;
    }
    Find<TextBlock>("NoteTotal").Text = notesList.Items.Count.ToString();
   } finally { syncingList = false; }
  }
  void RefreshPin() { main.Topmost = current.Pinned; Find<TextBlock>("PinText").Text = current.Pinned ? "已置顶" : "置顶"; pin.Foreground = Theme(current.Pinned ? "Accent" : "SecondaryInk"); pin.Background = current.Pinned ? Theme("SelectedSurface") : Brushes.Transparent; System.Windows.Automation.AutomationProperties.SetName(pin, current.Pinned ? "取消置顶" : "设为置顶"); }
  void RefreshReminder() {
   var button = Find<Button>("ReminderButton");
   if (current.DueUtc <= 0) { reminder.Text = "设置提醒"; button.Foreground = Theme("SecondaryInk"); button.ToolTip = "选择提醒日期与时间"; }
   else { var date = new DateTime(current.DueUtc, DateTimeKind.Utc).ToLocalTime(); reminder.Text = (current.Alerting ? "待处理 · " : "") + date.ToString("MM/dd HH:mm"); button.Foreground = Theme("Accent"); button.ToolTip = date.ToString("yyyy年MM月dd日 HH:mm") + " · 点击修改或取消"; }
  }
  void NewNote() { if (!Save()) { ShowSaveError(); return; } current = new Note(); data.Notes.Add(current); Bind(); Changed(); ShowMain(); title.Focus(); title.SelectAll(); }
  void SelectNote(Note n) { if (n.DeletedUtc != 0) return; if (!Save()) { ShowSaveError(); return; } current = n; Bind(); Changed(); ShowMain(); }
  void ShowMain() { main.Show(); main.WindowState = WindowState.Normal; main.Activate(); }
  void ShowSaveError() { MessageBox.Show(main, "内容暂未写入磁盘。请检查磁盘空间或目录权限后按 Ctrl+S 重试。\n" + store.Folder, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
  SolidColorBrush Theme(string key) { return (SolidColorBrush)main.Resources[key]; }
  System.Windows.Controls.MenuItem Item(string label, Action click) { var i = new System.Windows.Controls.MenuItem { Header = label }; i.Click += delegate { click(); }; return i; }
  void OpenMenu(Button anchor, System.Windows.Controls.ContextMenu menu) { menu.PlacementTarget = anchor; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true; }
  void ShowList() { notesList.Focus(); var item = notesList.SelectedItem as ListBoxItem; if (item != null) { item.Focus(); notesList.ScrollIntoView(item); } }
  void ShowMore() {
   var menu = new System.Windows.Controls.ContextMenu();
   var startup = Item("开机启动", delegate { ToggleStartup(); }); startup.IsCheckable = true; startup.IsChecked = StartupEnabled(); menu.Items.Add(startup);
   var sound = Item("提醒声音", delegate { data.Sound = !data.Sound; Changed(); }); sound.IsCheckable = true; sound.IsChecked = data.Sound; menu.Items.Add(sound);
   var color = new System.Windows.Controls.MenuItem { Header = "便笺色条" };
   string[] names = { "麦黄", "晴蓝", "草绿" };
   for (int j = 0; j < names.Length; j++) { int k = j; var item = Item(names[j], delegate { current.Color = k; Bind(); Changed(); }); item.IsCheckable = true; item.IsChecked = current.Color == j; color.Items.Add(item); } menu.Items.Add(color);
   menu.Items.Add(new Separator()); menu.Items.Add(Item("导出当前便笺为文本…", Export)); menu.Items.Add(Item("打开数据文件夹", delegate { System.Diagnostics.Process.Start("explorer.exe", "\"" + store.Folder + "\""); }));
   menu.Items.Add(Item("移到回收站", DeleteNote));
   var trash = new System.Windows.Controls.MenuItem { Header = "恢复已删除便笺" };
   foreach (var n in data.Notes.Where(n => n.DeletedUtc != 0).OrderByDescending(n => n.DeletedUtc)) { var selected = n; trash.Items.Add(Item(n.Label.Replace("_", "__"), delegate { selected.DeletedUtc = 0; selected.Complete(); SelectNote(selected); })); } trash.IsEnabled = trash.Items.Count > 0; menu.Items.Add(trash);
   menu.Items.Add(new Separator()); menu.Items.Add(Item("关于与使用说明", delegate { MessageBox.Show(main, "轻笺 1.0\n\nCtrl+N 新建  ·  Ctrl+L 切换\nCtrl+T 置顶  ·  Ctrl+S 保存\n\n关闭或最小化窗口后仍在托盘运行。\n单击托盘图标显示便笺，右键可退出。\n电脑关机、休眠或程序退出期间无法即时提醒，\n恢复运行后会补报。删除的便笺可从菜单恢复。\n\n全部数据仅保存在本机，无账号、无联网。", "关于轻笺"); }));
   menu.Items.Add(Item("退出轻笺", ExitApp)); OpenMenu(Find<Button>("MoreButton"), menu);
  }
  bool StartupEnabled() { using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) { var value = key == null ? null : key.GetValue(StartupName) as string; return value != null && value.IndexOf(Assembly.GetExecutingAssembly().Location, StringComparison.OrdinalIgnoreCase) >= 0; } }
  void ToggleStartup() {
   try { bool enabled = StartupEnabled(); using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) { if (enabled) key.DeleteValue(StartupName, false); else key.SetValue(StartupName, "\"" + Assembly.GetExecutingAssembly().Location + "\""); } status.Text = enabled ? "已关闭开机启动" : "已开启开机启动"; }
   catch (Exception ex) { MessageBox.Show(main, "开机启动设置失败：" + ex.Message, "轻笺"); }
  }
  void Export() {
   var dialog = new SaveFileDialog { Title = "导出便笺", Filter = "文本文件 (*.txt)|*.txt", FileName = String.Join("_", current.Label.Split(Path.GetInvalidFileNameChars())) + ".txt" };
   if (dialog.ShowDialog(main) == true) { try { File.WriteAllText(dialog.FileName, current.Label + Environment.NewLine + Environment.NewLine + current.Body, new UTF8Encoding(true)); status.Text = "已导出文本"; } catch (Exception ex) { MessageBox.Show(main, "导出失败：" + ex.Message); } }
  }
  void DeleteNote() { DeleteNote(current); }
  void DeleteNote(Note note) {
   if (MessageBox.Show(main, "将“" + note.Label + "”移到回收站？\n可从更多菜单恢复，提醒将取消。", "删除便笺", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
   note.DeletedUtc = DateTime.UtcNow.Ticks; note.Complete(); CloseAlert(note.Id);
   if (note == current) current = data.Notes.FirstOrDefault(n => n.DeletedUtc == 0);
   if (current == null) { current = new Note(); data.Notes.Add(current); } Bind(); Changed(); Save();
  }
  void ExitApp() { PullGeometry(); if (!Save()) { ShowMain(); ShowSaveError(); return; } exiting = true; clock.Stop(); debounce.Stop(); tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); foreach (var w in alerts.Values.ToArray()) w.Close(); Shutdown(); }
  void SetupTray() {
   using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LightNote.ico")) trayIcon = new System.Drawing.Icon(stream, 32, 32);
   tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "轻笺 · 便笺与提醒", Visible = true };
   tray.MouseClick += delegate(object s, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(new Action(ShowMain)); };
   var menu = new Forms.ContextMenuStrip(); menu.Items.Add("显示便笺", null, delegate { ShowMain(); }); menu.Items.Add("新建便笺", null, delegate { NewNote(); }); menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("退出轻笺", null, delegate { ExitApp(); }); tray.ContextMenuStrip = menu;
  }
  Window Dialog(string heading, double width, double height) { var w = new Window { Title = heading, Icon = main.Icon, Owner = main, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = Theme("PaperSurface"), Foreground = Theme("Ink"), FontFamily = main.FontFamily, FontSize = 13, Topmost = main.Topmost, UseLayoutRounding = true }; w.PreviewKeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) { w.Close(); e.Handled = true; } }; return w; }
  Button MakeButton(string text, Action action, bool primary) { var b = new Button { Content = text, Margin = new Thickness(4, 0, 0, 0), MinHeight = 28, MinWidth = 72, Padding = new Thickness(10, 4, 10, 4), Style = primary ? (Style)main.Resources["PrimaryButton"] : (Style)main.Resources[typeof(Button)] }; b.Click += delegate { action(); }; return b; }
  void EditReminder() {
   var note = current; var w = Dialog("设置提醒", 328, 298); var panel = new StackPanel { Margin = new Thickness(16) }; w.Content = panel;
   panel.Children.Add(new TextBlock { Text = "提醒时间", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
   panel.Children.Add(new TextBlock { Text = "日期", Foreground = Theme("SecondaryInk"), FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
   var initial = note.DueUtc > 0 ? new DateTime(note.DueUtc, DateTimeKind.Utc).ToLocalTime() : DateTime.Now.AddHours(1);
   if (initial <= DateTime.Now) initial = DateTime.Now.AddHours(1);
   var date = new DatePicker { SelectedDate = initial.Date, Height = 28, DisplayDateStart = DateTime.Today }; System.Windows.Automation.AutomationProperties.SetName(date, "提醒日期"); panel.Children.Add(date);
   panel.Children.Add(new TextBlock { Text = "时间 · 24 小时制", Foreground = Theme("SecondaryInk"), FontSize = 12, Margin = new Thickness(0, 10, 0, 4) });
   var row = new StackPanel { Orientation = Orientation.Horizontal };
   var hour = new ComboBox { Width = 80, Height = 28, VerticalContentAlignment = VerticalAlignment.Center, ItemsSource = Enumerable.Range(0, 24).Select(n => n.ToString("00")).ToArray(), SelectedIndex = initial.Hour };
   var minute = new ComboBox { Width = 80, Height = 28, VerticalContentAlignment = VerticalAlignment.Center, ItemsSource = Enumerable.Range(0, 60).Select(n => n.ToString("00")).ToArray(), SelectedIndex = initial.Minute };
   System.Windows.Automation.AutomationProperties.SetName(hour, "小时"); System.Windows.Automation.AutomationProperties.SetName(minute, "分钟");
   row.Children.Add(hour); row.Children.Add(new TextBlock { Text = " : ", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) }); row.Children.Add(minute); panel.Children.Add(row);
   var error = new TextBlock { Foreground = Theme("ErrorInk"), FontSize = 11, TextWrapping = TextWrapping.Wrap, MinHeight = 26, Margin = new Thickness(0, 6, 0, 0) }; panel.Children.Add(error);
   var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
   var cancel = MakeButton("取消提醒", delegate { note.Complete(); CloseAlert(note.Id); RefreshReminder(); Changed(); Save(); w.Close(); }, false); cancel.IsEnabled = note.DueUtc > 0; buttons.Children.Add(cancel);
   var save = MakeButton("保存提醒", delegate {
    if (!date.SelectedDate.HasValue || hour.SelectedIndex < 0 || minute.SelectedIndex < 0) { error.Text = "请选择完整的日期与时间。"; return; }
    var due = date.SelectedDate.Value.Date.AddHours(hour.SelectedIndex).AddMinutes(minute.SelectedIndex);
    if (due <= DateTime.Now) { error.Text = "请选择晚于当前时间的提醒时间。"; return; }
    if (TimeZoneInfo.Local.IsInvalidTime(due)) { error.Text = "该时间处于夏令时跳变区间，请换一个时间。"; return; }
    note.DueUtc = due.ToUniversalTime().Ticks; note.Alerting = false; CloseAlert(note.Id); RefreshReminder(); Changed(); if (Save()) w.Close(); else error.Text = "保存失败，请检查磁盘空间后重试。";
   }, true); save.IsDefault = true; buttons.Children.Add(save); panel.Children.Add(buttons);
   panel.Children.Add(new TextBlock { Text = "退出或休眠后，恢复运行时补报。", FontSize = 11, Foreground = Theme("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
   w.ShowDialog();
  }
  void CheckReminders() { foreach (var n in data.Notes.Where(n => n.IsDue(DateTime.UtcNow.Ticks)).ToArray()) { if (!alerts.ContainsKey(n.Id)) ShowAlert(n); } }
  void CloseAlert(string id) { Window w; if (alerts.TryGetValue(id, out w)) { alerts.Remove(id); w.Close(); } }
  void ShowAlert(Note note) {
   note.Alerting = true; Save(); if (note == current) RefreshReminder();
   var w = new Window { Title = "轻笺提醒", Icon = main.Icon, Width = 360, Height = 244, MinWidth = 320, MinHeight = 220, ResizeMode = ResizeMode.CanResize, Topmost = true, ShowInTaskbar = true, WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Theme("PaperSurface"), Foreground = Theme("Ink"), FontFamily = main.FontFamily, FontSize = 13, UseLayoutRounding = true };
   var grid = new Grid { Margin = new Thickness(16) }; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); w.Content = grid;
   var heading = new StackPanel(); heading.Children.Add(new TextBlock { Text = "到时间了", Foreground = Theme("Accent"), FontSize = 11 }); heading.Children.Add(new TextBlock { Text = note.Label, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxHeight = 56, Margin = new Thickness(0, 4, 0, 10) }); grid.Children.Add(heading);
   var preview = new TextBox { Text = note.Body, IsReadOnly = true, FontSize = 13, Foreground = Theme("Ink"), BorderThickness = new Thickness(0), TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(preview, 1); grid.Children.Add(preview);
   var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) }; Grid.SetRow(buttons, 2); grid.Children.Add(buttons);
   buttons.Children.Add(MakeButton("10 分钟后", delegate { note.Snooze(DateTime.UtcNow); Save(); CloseAlert(note.Id); RefreshReminder(); }, false));
   buttons.Children.Add(MakeButton("知道了", delegate { note.Complete(); Save(); CloseAlert(note.Id); RefreshReminder(); }, true));
   // Closing the alert means snooze, so a missed click cannot silently discard a reminder.
   w.Closing += delegate { if (alerts.ContainsKey(note.Id)) { alerts.Remove(note.Id); if (!exiting) { note.Snooze(DateTime.UtcNow); Save(); RefreshReminder(); } } };
   alerts[note.Id] = w; w.Show(); w.Activate(); if (data.Sound) System.Media.SystemSounds.Exclamation.Play();
  }
 }
}
