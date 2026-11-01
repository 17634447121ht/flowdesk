using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace FlowDesk
{
    public enum DisplayMode { UltraMinimal, Minimal, Detailed }

    public sealed class AppSettings
    {
        private DisplayMode mode;
        public string AdapterId { get; set; }
        public double QuotaGB { get; set; }
        public int BillingDay { get; set; }
        public double Opacity { get; set; }
        public bool TopMost { get; set; }
        public bool StartWithWindows { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool HasSavedPosition { get; set; }
        // Keep the old XML field readable by earlier releases. Mode follows it
        // in newly saved files, and ReadStore restores Mode independently of order.
        public bool DesktopWidgetEnabled
        {
            get { return mode == DisplayMode.Detailed; }
            set { mode = value ? DisplayMode.Detailed : DisplayMode.UltraMinimal; }
        }
        public DisplayMode Mode { get { return mode; } set { mode = value; } }
        public double MinimalLeft { get; set; }
        public double MinimalTop { get; set; }
        public bool HasMinimalPosition { get; set; }
        public bool MinimalLocked { get; set; }
        public bool MinimalClickThrough { get; set; }
        public string MinimalTextTheme { get; set; }
        public bool MinimalTextShadow { get; set; }

        public AppSettings()
        {
            AdapterId = "auto";
            BillingDay = 1;
            Opacity = 0.94;
            TopMost = true;
            Left = -1;
            Top = -1;
            Width = 286;
            Height = 240;
            Mode = DisplayMode.Minimal;
            MinimalLeft = -1;
            MinimalTop = -1;
            MinimalTextTheme = "auto";
            MinimalTextShadow = true;
        }

        public AppSettings Clone()
        {
            return (AppSettings)MemberwiseClone();
        }
    }

    public sealed class AdapterChoice
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public override string ToString() { return Name; }
    }

    public sealed class UsageDay
    {
        public string Date { get; set; }
        public long Download { get; set; }
        public long Upload { get; set; }
    }

    public sealed class MonitorSnapshot
    {
        public double DownloadPerSecond { get; set; }
        public double UploadPerSecond { get; set; }
        public long TodayDownload { get; set; }
        public long TodayUpload { get; set; }
        public long CycleDownload { get; set; }
        public long CycleUpload { get; set; }
        public DateTime CycleStart { get; set; }
        public DateTime CycleEnd { get; set; }
        public DateTime StartedOn { get; set; }
        public string AdapterLabel { get; set; }
        public string Status { get; set; }
        public bool IsMonitoringAvailable { get; set; }
        public DateTime SampledAt { get; set; }
        public List<UsageDay> History { get; set; }
    }

    // Public for XmlSerializer; only TrafficMonitor owns and mutates this state.
    public sealed class UsageStore
    {
        public int Version { get; set; }
        public DateTime StartedOn { get; set; }
        public AppSettings Settings { get; set; }
        public List<UsageDay> Days { get; set; }
        public List<QuotaNotice> Notices { get; set; }
        public UsageStore()
        {
            Version = 1;
            StartedOn = DateTime.Now;
            Settings = new AppSettings();
            Days = new List<UsageDay>();
            Notices = new List<QuotaNotice>();
        }
    }

    public sealed class QuotaNotice
    {
        public string Key { get; set; }
        public int Level { get; set; }
    }

    /// <summary>
    /// Samples cumulative Windows interface counters. It intentionally never imports
    /// bytes from before the current process established a baseline. All byte units
    /// are decimal (one GB = 1,000,000,000 bytes). Call Tick about once per second.
    /// </summary>
    public sealed class TrafficMonitor : IDisposable
    {
        private sealed class Baseline
        {
            public long Download;
            public long Upload;
            public long Stamp;
            public DateTime At;
        }

        private readonly object sync = new object();
        private readonly string dataDirectory;
        private readonly string dataPath;
        private readonly Dictionary<string, UsageDay> days = new Dictionary<string, UsageDay>(StringComparer.Ordinal);
        private readonly Dictionary<string, Baseline> baselines = new Dictionary<string, Baseline>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> hardwareAdapters = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private UsageStore store;
        private bool dirty;
        private bool preserveBadPrimary;
        private bool preserveBadBackup;
        private bool backupWasRecovered;
        private bool disposed;
        private long lastSaveStamp;
        private long lastAdapterRefresh;
        private string lastSelection;
        private string lastStorageError;
        private string recoveryNotice;
        private readonly AutoResetEvent saveSignal = new AutoResetEvent(false);
        private readonly object saveSync = new object();
        private readonly Thread saveThread;
        private UsageStore pendingSave;
        private long queuedSaveVersion;
        private long attemptedSaveVersion;
        private bool stopSaving;
        private bool forceSave;

        public event Action<int> QuotaReached;
        public AppSettings Settings { get { return store.Settings; } }
        public DateTime StartedOn { get { return store.StartedOn; } }
        public string LastStorageError { get { lock (saveSync) return lastStorageError; } }
        public string RecoveryNotice { get { return recoveryNotice; } }

        public TrafficMonitor(string dataDir)
        {
            if (String.IsNullOrWhiteSpace(dataDir)) throw new ArgumentException("A data directory is required.", "dataDir");
            dataDirectory = Path.GetFullPath(dataDir);
            dataPath = Path.Combine(dataDirectory, "usage.xml");
            Load();
            // Use native local metadata rather than WMI, whose optional providers
            // can block startup for minutes on some Windows builds.
            RefreshPhysicalAdapters();
            lastSaveStamp = Stopwatch.GetTimestamp();
            lastSelection = store.Settings.AdapterId;
            saveThread = new Thread(SaveLoop) { IsBackground = true, Name = "FlowDesk storage" };
            saveThread.Start();
        }

        public List<AdapterChoice> RefreshAdapters()
        {
            lock (sync)
            {
                ThrowIfDisposed();
                RefreshPhysicalAdapters();
                List<AdapterChoice> result = new List<AdapterChoice>();
                result.Add(new AdapterChoice { Id = "auto", Name = "自动 · 已连接的物理网卡" });
                try
                {
                    foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces().OrderBy(a => a.Name))
                    {
                        if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback || IsFilter(adapter)) continue;
                        string kind = IsPhysical(adapter) ? "物理" : "虚拟 / 其他";
                        string state = adapter.OperationalStatus == OperationalStatus.Up ? "已连接" : "未连接";
                        result.Add(new AdapterChoice { Id = adapter.Id, Name = adapter.Name + " · " + kind + " · " + state });
                    }
                }
                catch (NetworkInformationException) { }
                return result;
            }
        }

        public void ApplySettings(AppSettings value)
        {
            if (value == null) throw new ArgumentNullException("value");
            lock (sync)
            {
                ThrowIfDisposed();
                AppSettings copy = value.Clone();
                NormalizeSettings(copy);
                if (!String.Equals(lastSelection, copy.AdapterId, StringComparison.OrdinalIgnoreCase))
                {
                    baselines.Clear();
                    lastSelection = copy.AdapterId;
                }
                store.Settings = copy;
                dirty = true;
                Save();
            }
        }

        public MonitorSnapshot Tick(DateTime now)
        {
            MonitorSnapshot snapshot;
            int notice = 0;
            lock (sync)
            {
                ThrowIfDisposed();
                NormalizeSettings(store.Settings);
                if (!String.Equals(lastSelection, Settings.AdapterId, StringComparison.OrdinalIgnoreCase))
                {
                    baselines.Clear();
                    lastSelection = Settings.AdapterId;
                }
                long stamp = Stopwatch.GetTimestamp();
                if (SecondsBetween(lastAdapterRefresh, stamp) >= 60) RefreshPhysicalAdapters();
                double downRate = 0, upRate = 0;
                string status = "实时监测中";
                string label = "自动 · 物理网卡";
                bool anyBaseline = false, anyCounter = false, anyReset = false, longGap = false;
                List<NetworkInterface> selected = new List<NetworkInterface>();
                HashSet<string> currentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    NetworkInterface[] available = NetworkInterface.GetAllNetworkInterfaces();
                    if (Settings.AdapterId == "auto")
                    {
                        selected.AddRange(available.Where(a => a.OperationalStatus == OperationalStatus.Up && IsPhysical(a)));
                        if (selected.Count == 0) status = "未发现已连接的物理网卡";
                        else label = String.Join(" + ", selected.Select(a => a.Name).ToArray());
                    }
                    else
                    {
                        NetworkInterface chosen = available.FirstOrDefault(a => String.Equals(a.Id, Settings.AdapterId, StringComparison.OrdinalIgnoreCase));
                        if (chosen == null)
                        {
                            status = "选定网卡不存在，请重新选择";
                            label = "选定网卡不可用";
                        }
                        else
                        {
                            label = chosen.Name;
                            if (chosen.OperationalStatus == OperationalStatus.Up) selected.Add(chosen);
                            else status = "选定网卡未连接";
                        }
                    }

                    foreach (NetworkInterface adapter in selected)
                    {
                        currentIds.Add(adapter.Id);
                        try
                        {
                            // On Windows these are interface octet counters, including IPv6.
                            IPv4InterfaceStatistics statistics = adapter.GetIPv4Statistics();
                            long download = statistics.BytesReceived;
                            long upload = statistics.BytesSent;
                            if (download < 0 || upload < 0) { baselines.Remove(adapter.Id); continue; }
                            anyCounter = true;
                            Baseline previous;
                            if (baselines.TryGetValue(adapter.Id, out previous))
                            {
                                double seconds = SecondsBetween(previous.Stamp, stamp);
                                long deltaDownload = download - previous.Download;
                                long deltaUpload = upload - previous.Upload;
                                bool reset = deltaDownload < 0 || deltaUpload < 0;
                                bool clockChanged = Math.Abs((now - previous.At).TotalSeconds - seconds) > 5;
                                if (!reset)
                                {
                                    if (clockChanged || now < previous.At)
                                        AddToDay(now.Date, deltaDownload, deltaUpload);
                                    else
                                        AddInterval(previous.At, now, deltaDownload, deltaUpload);
                                    // Totals keep valid counter increments over sleep or delayed
                                    // polling, but suppress the misleading first resumed speed.
                                    if (seconds >= 0.1 && seconds <= 10 && !clockChanged)
                                    {
                                        downRate += deltaDownload / seconds;
                                        upRate += deltaUpload / seconds;
                                    }
                                    else longGap = true;
                                }
                                else anyReset = true;
                            }
                            else anyBaseline = true;
                            baselines[adapter.Id] = new Baseline { Download = download, Upload = upload, Stamp = stamp, At = now };
                        }
                        catch (NetworkInformationException) { baselines.Remove(adapter.Id); }
                        catch (InvalidOperationException) { baselines.Remove(adapter.Id); }
                    }
                    foreach (string id in baselines.Keys.Where(id => !currentIds.Contains(id)).ToArray()) baselines.Remove(id);
                    if (selected.Count > 0)
                    {
                        if (!anyCounter) status = "暂时无法读取网卡，请稍后重试";
                        else if (anyReset) status = "网卡计数已重置，继续统计";
                        else if (longGap) status = "已恢复监测，网速重新采样";
                        else if (anyBaseline) status = "正在建立统计基线";
                    }
                }
                catch (NetworkInformationException)
                {
                    baselines.Clear();
                    status = "暂时无法读取网络状态";
                }

                Tuple<DateTime, DateTime> period = BillingPeriod(now, Settings.BillingDay);
                snapshot = new MonitorSnapshot
                {
                    DownloadPerSecond = downRate,
                    UploadPerSecond = upRate,
                    CycleStart = period.Item1,
                    CycleEnd = period.Item2,
                    StartedOn = store.StartedOn,
                    AdapterLabel = label,
                    Status = status,
                    IsMonitoringAvailable = anyCounter,
                    SampledAt = now,
                    History = CopyDays()
                };
                foreach (UsageDay day in snapshot.History)
                {
                    DateTime date = ParseDate(day.Date);
                    if (date == now.Date)
                    {
                        snapshot.TodayDownload = day.Download;
                        snapshot.TodayUpload = day.Upload;
                    }
                    if (date >= period.Item1 && date < period.Item2)
                    {
                        snapshot.CycleDownload = SaturatingAdd(snapshot.CycleDownload, day.Download);
                        snapshot.CycleUpload = SaturatingAdd(snapshot.CycleUpload, day.Upload);
                    }
                }
                notice = CheckQuota(snapshot);
                if (notice > 0 || (dirty && SecondsBetween(lastSaveStamp, stamp) >= 10)) Save();
            }
            Action<int> handler = QuotaReached;
            if (notice > 0 && handler != null) handler(notice);
            return snapshot;
        }

        /// <summary>Returns local midnight bounds; the end is exclusive.</summary>
        public static Tuple<DateTime, DateTime> BillingPeriod(DateTime now, int billingDay)
        {
            billingDay = Math.Max(1, Math.Min(31, billingDay));
            DateTime month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, now.Kind);
            DateTime start = BillingDate(month, billingDay);
            if (now < start) start = BillingDate(month.AddMonths(-1), billingDay);
            DateTime end = BillingDate(new DateTime(start.Year, start.Month, 1, 0, 0, 0, start.Kind).AddMonths(1), billingDay);
            return Tuple.Create(start, end);
        }

        private static DateTime BillingDate(DateTime month, int day)
        {
            return new DateTime(month.Year, month.Month, Math.Min(day, DateTime.DaysInMonth(month.Year, month.Month)), 0, 0, 0, month.Kind);
        }

        /// <summary>
        /// Allocates observed bytes across local midnights proportionally, preserving
        /// every integer byte. This deterministic method never reads network counters.
        /// </summary>
        public void AddInterval(DateTime start, DateTime end, long download, long upload)
        {
            if (download < 0) throw new ArgumentOutOfRangeException("download");
            if (upload < 0) throw new ArgumentOutOfRangeException("upload");
            if (end < start) throw new ArgumentException("The interval end precedes its start.", "end");
            lock (sync)
            {
                ThrowIfDisposed();
                if (download == 0 && upload == 0) return;
                if (start.Date == end.Date || end == start)
                {
                    AddToDay(end.Date, download, upload);
                    return;
                }
                long totalTicks = (end - start).Ticks;
                long assignedDownload = 0, assignedUpload = 0;
                DateTime cursor = start;
                while (cursor < end)
                {
                    DateTime next = cursor.Date.AddDays(1);
                    if (next > end) next = end;
                    long elapsedTicks = (next - start).Ticks;
                    // Decimal avoids both long multiplication overflow and floating
                    // point loss of bytes when totals grow beyond double precision.
                    long cumulativeDownload = next == end ? download : (long)((decimal)download * elapsedTicks / totalTicks);
                    long cumulativeUpload = next == end ? upload : (long)((decimal)upload * elapsedTicks / totalTicks);
                    AddToDay(cursor.Date, cumulativeDownload - assignedDownload, cumulativeUpload - assignedUpload);
                    assignedDownload = cumulativeDownload;
                    assignedUpload = cumulativeUpload;
                    cursor = next;
                }
            }
        }

        private void AddToDay(DateTime date, long download, long upload)
        {
            if (download == 0 && upload == 0) return;
            string key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            UsageDay day;
            if (!days.TryGetValue(key, out day))
            {
                day = new UsageDay { Date = key };
                days.Add(key, day);
            }
            day.Download = SaturatingAdd(day.Download, download);
            day.Upload = SaturatingAdd(day.Upload, upload);
            dirty = true;
        }

        private int CheckQuota(MonitorSnapshot snapshot)
        {
            if (Settings.QuotaGB <= 0) return 0;
            double quota = Settings.QuotaGB * 1000000000.0;
            double used = (double)snapshot.CycleDownload + snapshot.CycleUpload;
            int level = used >= quota ? 100 : (used >= quota * 0.8 ? 80 : 0);
            if (level == 0) return 0;
            string key = snapshot.CycleStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "/" +
                snapshot.CycleEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "/" +
                Settings.QuotaGB.ToString("R", CultureInfo.InvariantCulture);
            QuotaNotice prior = store.Notices.FirstOrDefault(n => n.Key == key);
            if (prior != null && prior.Level >= level) return 0;
            if (prior == null) { prior = new QuotaNotice { Key = key }; store.Notices.Add(prior); }
            prior.Level = level;
            dirty = true;
            return level;
        }

        /// <summary>Queues an immutable snapshot; disk access runs on one storage thread.</summary>
        public void Save()
        {
            lock (sync)
            {
                if (disposed) return;
                NormalizeSettings(store.Settings);
                UsageStore copy = new UsageStore
                {
                    Version = store.Version,
                    StartedOn = store.StartedOn,
                    Settings = store.Settings.Clone(),
                    Days = CopyDays(),
                    Notices = store.Notices.Select(n => new QuotaNotice { Key = n.Key, Level = n.Level }).ToList()
                };
                lock (saveSync)
                {
                    pendingSave = copy;
                    queuedSaveVersion++;
                }
                dirty = false;
                lastSaveStamp = Stopwatch.GetTimestamp();
                saveSignal.Set();
            }
        }

        /// <summary>Waits for currently queued data to be attempted once. Errors remain visible and retry in the background.</summary>
        public void Flush()
        {
            lock (saveSync)
            {
                long target = queuedSaveVersion;
                forceSave = true;
                saveSignal.Set();
                while (attemptedSaveVersion < target) Monitor.Wait(saveSync);
            }
        }

        private void SaveLoop()
        {
            UsageStore retry = null;
            long retryVersion = 0;
            int failures = 0;
            DateTime retryAt = DateTime.MinValue;
            while (true)
            {
                bool stopping;
                lock (saveSync)
                {
                    stopping = stopSaving;
                    if (forceSave) { retryAt = DateTime.MinValue; forceSave = false; }
                    if (pendingSave != null)
                    {
                        retry = pendingSave;
                        retryVersion = queuedSaveVersion;
                        pendingSave = null;
                    }
                }
                if (retry == null)
                {
                    if (stopping) return;
                    saveSignal.WaitOne();
                    continue;
                }
                // A stream of new samples must not defeat the failure backoff.
                if (!stopping && DateTime.UtcNow < retryAt)
                {
                    int delay = (int)Math.Min(60000, Math.Max(1, (retryAt - DateTime.UtcNow).TotalMilliseconds));
                    saveSignal.WaitOne(delay);
                    continue;
                }
                string error = WriteStore(retry);
                lock (saveSync)
                {
                    lastStorageError = error;
                    attemptedSaveVersion = Math.Max(attemptedSaveVersion, retryVersion);
                    Monitor.PulseAll(saveSync);
                    stopping = stopSaving;
                    if (error == null)
                    {
                        retry = null;
                        failures = 0;
                        retryAt = DateTime.MinValue;
                    }
                    else
                    {
                        failures = Math.Min(failures + 1, 6);
                        retryAt = DateTime.UtcNow.AddSeconds(Math.Min(60, Math.Pow(2, failures)));
                    }
                    // On exit, make one final attempt on the newest queued snapshot,
                    // then terminate even if the destination remains unwritable.
                    if (stopping && pendingSave == null) return;
                }
            }
        }

        private string WriteStore(UsageStore snapshot)
        {
            string temporary = null;
            try
            {
                Directory.CreateDirectory(dataDirectory);
                // Preserve both invalid inputs before either is replaced. A unique
                // suffix also avoids collisions after rapid restarts.
                if (preserveBadBackup && File.Exists(dataPath + ".bak"))
                {
                    File.Copy(dataPath + ".bak", ArchivePath(dataPath + ".bak", backupWasRecovered ? ".recovered-" : ".damaged-"));
                    preserveBadBackup = false;
                }
                temporary = Path.Combine(dataDirectory, "usage." + Guid.NewGuid().ToString("N") + ".tmp");
                XmlSerializer serializer = new XmlSerializer(typeof(UsageStore));
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    serializer.Serialize(stream, snapshot);
                    stream.Flush(true);
                }
                if (File.Exists(dataPath))
                {
                    string backup = preserveBadPrimary ? DamagedPath(dataPath) : dataPath + ".bak";
                    File.Replace(temporary, dataPath, backup, true);
                }
                else File.Move(temporary, dataPath);
                temporary = null;
                preserveBadPrimary = false;
                return null;
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException || error is System.Security.SecurityException))
                    LogStorageError(error);
                return "保存失败：" + error.Message;
            }
            finally
            {
                if (temporary != null)
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception cleanupError) { LogStorageError(cleanupError); }
                }
            }
        }

        private static string DamagedPath(string path)
        {
            return ArchivePath(path, ".damaged-");
        }

        private static string ArchivePath(string path, string suffix)
        {
            return path + suffix + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        }

        private void LogStorageError(Exception error)
        {
            // Keep Core independently testable; unexpected storage errors must not
            // take down the background thread or leave Flush waiting indefinitely.
            try { File.AppendAllText(Path.Combine(dataDirectory, "errors.log"), DateTime.Now.ToString("s") + " " + error + Environment.NewLine); }
            catch { }
        }
        private void Load()
        {
            store = null;
            if (File.Exists(dataPath))
            {
                try { store = ReadStore(dataPath); }
                catch (Exception error)
                {
                    if (!(error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException || error is XmlException || error is FormatException)) throw;
                    preserveBadPrimary = true;
                    recoveryNotice = "统计文件读取失败，已保留原文件并尝试备份。";
                }
            }
            if (store == null && File.Exists(dataPath + ".bak"))
            {
                try
                {
                    store = ReadStore(dataPath + ".bak");
                    preserveBadBackup = true;
                    backupWasRecovered = true;
                    recoveryNotice = "已从上一次备份恢复统计；最近一次保存的数据可能缺失。原文件和恢复所用备份会另存保留。";
                }
                catch (Exception error)
                {
                    if (!(error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException || error is XmlException || error is FormatException)) throw;
                    preserveBadBackup = true;
                    recoveryNotice = "统计文件及备份无法读取，已开始新的统计；两个原文件都将另存为 damaged 文件。";
                }
            }
            if (store == null) store = new UsageStore();
            NormalizeSettings(store.Settings);
            foreach (UsageDay day in store.Days)
            {
                UsageDay existing;
                if (days.TryGetValue(day.Date, out existing))
                {
                    existing.Download = SaturatingAdd(existing.Download, day.Download);
                    existing.Upload = SaturatingAdd(existing.Upload, day.Upload);
                }
                else days.Add(day.Date, new UsageDay { Date = day.Date, Download = day.Download, Upload = day.Upload });
            }
            dirty = true;
        }

        private static UsageStore ReadStore(string path)
        {
            XmlReaderSettings options = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (FileStream stream = File.OpenRead(path))
            using (XmlReader reader = XmlReader.Create(stream, options))
            {
                // XmlSerializer runs constructors first. Checking the resulting
                // object alone accepts <UsageStore/> as a fresh, empty history.
                XmlDocument document = new XmlDocument { XmlResolver = null };
                document.Load(reader);
                XmlElement root = document.DocumentElement;
                if (root == null || root.Name != "UsageStore") throw new FormatException("Invalid statistics root.");
                foreach (string required in new[] { "Version", "StartedOn", "Settings", "Days" })
                    if (root.SelectNodes(required).Count != 1) throw new FormatException("Missing or duplicate statistics field: " + required);
                foreach (XmlNode entry in root.SelectSingleNode("Days").ChildNodes)
                {
                    if (entry.NodeType != XmlNodeType.Element) continue;
                    if (entry.Name != "UsageDay") throw new FormatException("Invalid daily statistics entry.");
                    foreach (string required in new[] { "Date", "Download", "Upload" })
                        if (entry.SelectNodes(required).Count != 1) throw new FormatException("Missing or duplicate daily statistics field: " + required);
                }
                XmlNode modeNode = root.SelectSingleNode("Settings/Mode");
                DisplayMode savedMode = DisplayMode.Minimal;
                bool validMode = modeNode != null && Enum.TryParse<DisplayMode>(modeNode.InnerText, false, out savedMode)
                    && Enum.IsDefined(typeof(DisplayMode), savedMode);
                // An invalid optional display preference must not discard valid
                // traffic history just because XmlSerializer cannot parse an enum.
                if (modeNode != null) modeNode.InnerText = validMode ? savedMode.ToString() : DisplayMode.Minimal.ToString();
                UsageStore result;
                using (XmlNodeReader validated = new XmlNodeReader(document))
                    result = (UsageStore)new XmlSerializer(typeof(UsageStore)).Deserialize(validated);
                if (result == null || result.Version != 1 || result.Settings == null || result.Days == null)
                    throw new FormatException("Unsupported or incomplete statistics file.");
                if (result.StartedOn == DateTime.MinValue) throw new FormatException("Invalid statistics start date.");
                if (modeNode == null)
                    result.Settings.Mode = result.Settings.DesktopWidgetEnabled ? DisplayMode.Detailed : DisplayMode.UltraMinimal;
                else
                    result.Settings.Mode = (DisplayMode)Enum.Parse(typeof(DisplayMode), modeNode.InnerText, false);
                if (root.SelectSingleNode("Settings/HasSavedPosition") == null)
                {
                    double left = result.Settings.Left, top = result.Settings.Top;
                    result.Settings.HasSavedPosition = !Double.IsNaN(left) && !Double.IsInfinity(left) &&
                        !Double.IsNaN(top) && !Double.IsInfinity(top) && (left != -1 || top != -1);
                }
                foreach (UsageDay day in result.Days)
                {
                    DateTime parsed;
                    if (day == null || !DateTime.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed) || day.Download < 0 || day.Upload < 0)
                        throw new FormatException("Invalid daily statistics.");
                }
                if (result.Notices == null) result.Notices = new List<QuotaNotice>();
                result.Notices.RemoveAll(n => n == null || String.IsNullOrEmpty(n.Key) || (n.Level != 80 && n.Level != 100));
                return result;
            }
        }

        private List<UsageDay> CopyDays()
        {
            return days.Values.OrderBy(d => d.Date, StringComparer.Ordinal).Select(d => new UsageDay { Date = d.Date, Download = d.Download, Upload = d.Upload }).ToList();
        }

        private static void NormalizeSettings(AppSettings value)
        {
            if (String.IsNullOrWhiteSpace(value.AdapterId)) value.AdapterId = "auto";
            if (String.Equals(value.AdapterId, "auto", StringComparison.OrdinalIgnoreCase)) value.AdapterId = "auto";
            value.BillingDay = Math.Max(1, Math.Min(31, value.BillingDay));
            if (Double.IsNaN(value.QuotaGB) || Double.IsInfinity(value.QuotaGB) || value.QuotaGB < 0) value.QuotaGB = 0;
            if (Double.IsNaN(value.Opacity) || Double.IsInfinity(value.Opacity)) value.Opacity = 0.94;
            value.Opacity = Math.Max(0.35, Math.Min(1, value.Opacity));
            if (Double.IsNaN(value.Left) || Double.IsInfinity(value.Left)) { value.Left = -1; value.HasSavedPosition = false; }
            if (Double.IsNaN(value.Top) || Double.IsInfinity(value.Top)) { value.Top = -1; value.HasSavedPosition = false; }
            if (!Enum.IsDefined(typeof(DisplayMode), value.Mode)) value.Mode = DisplayMode.Minimal;
            if (Double.IsNaN(value.MinimalLeft) || Double.IsInfinity(value.MinimalLeft)) { value.MinimalLeft = -1; value.HasMinimalPosition = false; }
            if (Double.IsNaN(value.MinimalTop) || Double.IsInfinity(value.MinimalTop)) { value.MinimalTop = -1; value.HasMinimalPosition = false; }
            value.MinimalTextTheme = (value.MinimalTextTheme ?? "auto").Trim().ToLowerInvariant();
            if (value.MinimalTextTheme != "light" && value.MinimalTextTheme != "dark") value.MinimalTextTheme = "auto";
            if (Double.IsNaN(value.Width) || Double.IsInfinity(value.Width) || value.Width <= 0) value.Width = 286;
            if (Double.IsNaN(value.Height) || Double.IsInfinity(value.Height) || value.Height <= 0) value.Height = 240;
            value.Width = Math.Max(286, Math.Min(900, value.Width));
            value.Height = Math.Max(240, Math.Min(700, value.Height));
        }

        private void RefreshPhysicalAdapters()
        {
            // GetIfEntry2 reads local interface metadata without the blocking WMI
            // provider. Cache per adapter, and refresh for hotplug/driver changes.
            hardwareAdapters.Clear();
            lastAdapterRefresh = Stopwatch.GetTimestamp();
        }

        private bool IsPhysical(NetworkInterface adapter)
        {
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback || adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel || IsFilter(adapter)) return false;
            string words = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
            if (words.Contains("bluetooth") || words.Contains("蓝牙")) return false;
            bool hardware;
            if (TryGetHardwareAdapter(adapter, out hardware)) return hardware;
            string[] virtualWords = { "virtual", "vmware", "vmnet", "vethernet", "hyper-v", "tunnel", "tap-", "tap ", "tun ", "wintun", "wireguard", "vpn", "mihomo", "clash", "tailscale", "zerotier", "docker", "container", "npcap", "loopback", "bluetooth", "虚拟", "蓝牙" };
            if (virtualWords.Any(words.Contains)) return false;
            // Safe fallback when the optional WMI provider is unavailable. Ethernet
            // and 802.11 are the media types exposed by physical LAN/Wi-Fi drivers;
            // known virtual/tunnel names were excluded above.
            switch (adapter.NetworkInterfaceType)
            {
                case NetworkInterfaceType.Ethernet:
                case NetworkInterfaceType.GigabitEthernet:
                case NetworkInterfaceType.FastEthernetT:
                case NetworkInterfaceType.FastEthernetFx:
                case NetworkInterfaceType.Wireless80211:
                    return true;
                default:
                    return false;
            }
        }

        // Native MIB_IF_ROW2 layout. IF_MAX_STRING_SIZE is 256 plus its terminator.
        // Reference: learn.microsoft.com/windows/win32/api/netioapi/ns-netioapi-mib_if_row2
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeInterfaceRow
        {
            public ulong Luid;
            public uint Index;
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
            public uint PhysicalAddressLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
            public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
            public byte Flags;
            public uint OperStatus, AdminStatus, MediaConnectState;
            public Guid NetworkGuid;
            public uint ConnectionType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)] public ulong[] Counters;
        }

        [DllImport("iphlpapi.dll")]
        private static extern uint GetIfEntry2(ref NativeInterfaceRow row);

        private bool TryGetHardwareAdapter(NetworkInterface adapter, out bool hardware)
        {
            if (hardwareAdapters.TryGetValue(adapter.Id, out hardware)) return true;
            hardware = false;
            try
            {
                IPInterfaceProperties properties = adapter.GetIPProperties();
                int index = adapter.Supports(NetworkInterfaceComponent.IPv4) ? properties.GetIPv4Properties().Index : properties.GetIPv6Properties().Index;
                NativeInterfaceRow row = new NativeInterfaceRow
                {
                    Index = (uint)index,
                    PhysicalAddress = new byte[32],
                    PermanentPhysicalAddress = new byte[32],
                    Counters = new ulong[20]
                };
                if (GetIfEntry2(ref row) != 0) return false;
                // Bit 0: HardwareInterface; bit 1: FilterInterface.
                hardware = (row.Flags & 1) != 0 && (row.Flags & 2) == 0;
                hardwareAdapters[adapter.Id] = hardware;
                return true;
            }
            catch (NetworkInformationException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        private static bool IsFilter(NetworkInterface adapter)
        {
            string words = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
            return words.Contains("wfp") || words.Contains("qos packet") || words.Contains("native mac layer") || words.Contains("filter") || words.Contains("筛选") || words.Contains("调度程序");
        }

        private static DateTime ParseDate(string value) { return DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture); }
        private static double SecondsBetween(long earlier, long later) { return (later - earlier) / (double)Stopwatch.Frequency; }
        private static long SaturatingAdd(long left, long right) { return right > Int64.MaxValue - left ? Int64.MaxValue : left + right; }
        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException("TrafficMonitor"); }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                Save();
                disposed = true;
                baselines.Clear();
            }
            lock (saveSync) stopSaving = true;
            saveSignal.Set();
            saveThread.Join();
            saveSignal.Dispose();
        }
    }
}
