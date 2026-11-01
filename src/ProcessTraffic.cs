using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace FlowDesk
{
    public sealed class ProcessTrafficRow
    {
        public string Name { get; set; }
        public string Identity { get; set; }
        public string ProcessIds { get; set; }
        public double DownloadPerSecond { get; set; }
        public double UploadPerSecond { get; set; }
        public long TodayDownload { get; set; }
        public long TodayUpload { get; set; }
        public long CycleDownload { get; set; }
        public long CycleUpload { get; set; }
    }

    public sealed class ProcessTrafficSnapshot
    {
        public List<ProcessTrafficRow> Rows { get; set; }
        public bool IsCollecting { get; set; }
        public bool NeedsElevation { get; set; }
        public string Status { get; set; }
        public string StorageError { get; set; }
        public long EventsLost { get; set; }
        public DateTime StartedOn { get; set; }
        public DateTime SampledAt { get; set; }
    }

    public sealed class ProcessUsageDay
    {
        public string Date { get; set; }
        public string Identity { get; set; }
        public string Name { get; set; }
        public long Download { get; set; }
        public long Upload { get; set; }
    }

    public sealed class ProcessUsageStore
    {
        public int Version { get; set; }
        public DateTime StartedOn { get; set; }
        public List<ProcessUsageDay> Days { get; set; }
    }

    /// <summary>
    /// Application attribution from kernel TCP/UDP send/receive events, across all
    /// interfaces. Packet event sizes do not have the same scope as interface octets.
    /// Start requires an elevated token; this class never requests elevation itself.
    /// Native layouts/event schemas: Microsoft Learn EVENT_TRACE_LOGFILEW,
    /// TcpIp_TypeGroup1/3/4 and UdpIp_TypeGroup1/2. Only opcodes 10/11/26/27 count;
    /// retransmission, connect, disconnect and copy events are deliberately excluded.
    /// </summary>
    public sealed class ProcessTrafficMonitor : IDisposable
    {
        private sealed class IdentityInfo
        {
            internal string Key;
            internal string Name;
            internal int Pid;
            internal DateTime Started;
            internal DateTime Checked;
            internal Process Instance;
        }
        private sealed class Rate
        {
            internal string Name;
            internal long Down;
            internal long Up;
            internal double DownRate;
            internal double UpRate;
        }
        private readonly object sync = new object();
        private readonly object lifecycle = new object();
        private readonly object writeLock = new object();
        private readonly Dictionary<string, ProcessUsageDay> days = new Dictionary<string, ProcessUsageDay>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Rate> rates = new Dictionary<string, Rate>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, IdentityInfo> identities = new Dictionary<int, IdentityInfo>();
        private readonly string path;
        private readonly string sessionName;
        private readonly string unknownSession = Guid.NewGuid().ToString("N");
        private readonly Timer timer;
        private Thread consumer;
        private Native.EventCallback eventCallback;
        private Native.BufferCallback bufferCallback;
        private ulong sessionHandle;
        private ulong consumerHandle = UInt64.MaxValue;
        private IntPtr properties;
        private volatile bool disposed;
        private volatile bool disposeRequested;
        private volatile bool cleanupCompleted;
        private volatile bool collecting;
        private bool nativeStopRequested;
        private bool nativeCloseRequested;
        private bool needsElevation;
        private bool preservePrimary;
        private bool preserveBackup;
        private bool recoveredBackup;
        private bool limited;
        private string status = "尚未启用程序流量统计";
        private string storageError;
        private DateTime startedOn;
        private long lost;
        private long revision;
        private long savedRevision;
        private long rateStamp = Stopwatch.GetTimestamp();
        private DateTime nextSave = DateTime.UtcNow.AddSeconds(10);
        private int timerBusy;
        private const int MaximumRecords = 50000;
        private const int MaximumIdentities = 4096;
        private const int RetentionDays = 400;
        private static readonly Guid TcpProvider = new Guid("9a280ac0-c8e0-11d1-84e2-00c04fb998a2");
        private static readonly Guid UdpProvider = new Guid("bf3a50c5-a9c9-4988-a005-2df0b7c80f80");

        public static bool IsAdministrator
        {
            get
            {
                try { using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
                catch (System.Security.SecurityException) { return false; }
            }
        }

        public bool IsShutdownComplete { get { return cleanupCompleted; } }

        public ProcessTrafficMonitor(string dataDirectory)
        {
            path = Path.Combine(Path.GetFullPath(dataDirectory), "process-usage.xml");
            using (SHA256 hash = SHA256.Create())
                sessionName = "FlowDesk.AppTraffic." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).Replace("-", "").Substring(0, 24);
            startedOn = DateTime.Now;
            Load();
            timer = new Timer(TimerTick, null, 1000, 1000);
        }

        public void Start()
        {
            lock (lifecycle)
            {
                if (disposed || disposeRequested) throw new ObjectDisposedException("ProcessTrafficMonitor");
                if (collecting) return;
                if (!IsAdministrator)
                {
                    lock (sync) { needsElevation = true; status = "启用程序流量统计需要管理员权限"; }
                    return;
                }
                if (sessionHandle != 0 || consumerHandle != UInt64.MaxValue || consumer != null)
                {
                    if (!StopNative())
                    {
                        lock (sync) { collecting = false; status = "正在等待上一轮采集结束，暂不能重新启用"; }
                        return;
                    }
                }
                try
                {
                    properties = CreateProperties();
                    uint result = Native.StartTrace(out sessionHandle, sessionName, properties);
                    if (result == 183)
                    {
                        // Only this application's deterministic private session is
                        // reclaimed after a crash. Never stop NT Kernel Logger.
                        Native.ControlTrace(0, sessionName, properties, 1);
                        Marshal.FreeHGlobal(properties);
                        properties = CreateProperties();
                        result = Native.StartTrace(out sessionHandle, sessionName, properties);
                    }
                    if (result != 0) throw new System.ComponentModel.Win32Exception((int)result);
                    eventCallback = OnEvent;
                    bufferCallback = OnBuffer;
                    Native.LogFile logfile = new Native.LogFile
                    {
                        LoggerName = sessionName,
                        ProcessTraceMode = 0x10000100,
                        EventCallback = eventCallback,
                        BufferCallback = bufferCallback
                    };
                    consumerHandle = Native.OpenTrace(ref logfile);
                    if (consumerHandle == UInt64.MaxValue) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    lock (sync) { collecting = true; needsElevation = false; status = "正在统计 TCP / UDP 程序流量"; }
                    consumer = new Thread(Consume) { IsBackground = true, Name = "FlowDesk program traffic" };
                    consumer.Start();
                }
                catch (Exception error)
                {
                    if (!(error is System.ComponentModel.Win32Exception || error is DllNotFoundException || error is EntryPointNotFoundException)) throw;
                    StopNative();
                    lock (sync)
                    {
                        collecting = false;
                        var nativeError = error as System.ComponentModel.Win32Exception;
                        needsElevation = nativeError != null && (nativeError.NativeErrorCode == 5 || nativeError.NativeErrorCode == 1314);
                        status = "程序流量统计未启用：" + error.Message;
                    }
                }
            }
        }

        private void Consume()
        {
            ulong handle = consumerHandle;
            uint result = Native.ProcessTrace(new ulong[] { handle }, 1, IntPtr.Zero, IntPtr.Zero);
            lock (sync)
            {
                collecting = false;
                if (!disposed) status = result == 0 || result == 1223 ? "程序流量统计已停止，可重新启用" : "程序流量采集中断（" + result + "），可重新启用";
                foreach (Rate rate in rates.Values) { rate.DownRate = 0; rate.UpRate = 0; }
            }
        }

        private IntPtr CreateProperties()
        {
            int size = Marshal.SizeOf(typeof(Native.Properties));
            int bytes = size + (sessionName.Length + 1) * 2;
            IntPtr memory = Marshal.AllocHGlobal(bytes);
            Marshal.Copy(new byte[bytes], 0, memory, bytes);
            var value = new Native.Properties();
            value.Wnode.BufferSize = (uint)bytes;
            value.Wnode.Guid = Guid.NewGuid();
            value.Wnode.ClientContext = 1;
            value.Wnode.Flags = 0x20000;
            value.BufferSize = 64;
            value.MinimumBuffers = 16;
            value.MaximumBuffers = 128;
            value.FlushTimer = 1;
            value.LogFileMode = 0x02000100;
            value.EnableFlags = 0x10010000;
            value.LoggerNameOffset = (uint)size;
            Marshal.StructureToPtr(value, memory, false);
            byte[] name = Encoding.Unicode.GetBytes(sessionName + "\0");
            Marshal.Copy(name, 0, IntPtr.Add(memory, size), name.Length);
            return memory;
        }

        private void OnEvent(IntPtr pointer)
        {
            // An exception must never escape a native callback.
            try
            {
                Native.Record record = (Native.Record)Marshal.PtrToStructure(pointer, typeof(Native.Record));
                if (record.Header.ProviderId != TcpProvider && record.Header.ProviderId != UdpProvider) return;
                byte opcode = record.Header.Opcode;
                if (opcode != 10 && opcode != 11 && opcode != 26 && opcode != 27) return;
                if (record.UserDataLength < 8 || record.UserData == IntPtr.Zero) { Interlocked.Increment(ref lost); return; }
                // Documented Vista+ schemas begin with uint32 PID, uint32 size,
                // including IPv6. Event-header PID can instead be System/DPC.
                if (record.Header.Version < 1) { Interlocked.Increment(ref lost); return; }
                int pid = Marshal.ReadInt32(record.UserData);
                uint size = unchecked((uint)Marshal.ReadInt32(record.UserData, 4));
                if (pid < 0 || size == 0) return;
                DateTime at = DateTime.FromFileTimeUtc(record.Header.TimeStamp).ToLocalTime();
                RecordPacket(pid, size, opcode == 11 || opcode == 27, at);
            }
            catch { Interlocked.Increment(ref lost); }
        }

        // Internal entry point also permits deterministic parser/accounting tests
        // without requiring an elevated machine or altering the user's history.
        internal void RecordPacket(int pid, uint bytes, bool download, DateTime at)
        {
            if (disposed) return;
            IdentityInfo identity;
            if (disposeRequested)
            {
                // CloseTrace may still deliver its final buffered events. During
                // shutdown retain attribution already resolved, but never open new
                // process handles from those late callbacks.
                lock (sync)
                {
                    if (!identities.TryGetValue(pid, out identity) || identity.Started > at)
                        identity = new IdentityInfo { Key = "unknown:" + unknownSession + ":" + pid, Name = "无法识别（PID " + pid + "）" };
                }
            }
            else identity = ResolveIdentity(pid, at);
            lock (sync)
            {
                if (disposed) return;
                AddPacket(identity.Key, identity.Name, bytes, download, at);
            }
        }

        private IdentityInfo ResolveIdentity(int pid, DateTime at)
        {
            IdentityInfo cached;
            lock (sync)
            {
                if (identities.TryGetValue(pid, out cached) && (DateTime.UtcNow - cached.Checked).TotalSeconds < 2 && cached.Started <= at)
                {
                    if (cached.Instance == null) return cached;
                    try { if (cached.Instance != null && !cached.Instance.HasExited) return cached; }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
            var result = new IdentityInfo { Pid = pid, Checked = DateTime.UtcNow, Started = DateTime.MinValue };
            try
            {
                Process process = Process.GetProcessById(pid);
                result.Instance = process;
                {
                    DateTime began = process.StartTime;
                    if (began > at)
                    {
                        // An ETW buffer can arrive after a PID has been reused.
                        if (cached != null && cached.Started <= at && EndedAfter(cached, at)) { process.Dispose(); return cached; }
                        throw new InvalidOperationException("PID was reused before event delivery.");
                    }
                    result.Started = began;
                    result.Name = process.ProcessName;
                    string executable = null;
                    try { executable = process.MainModule.FileName; } catch (System.ComponentModel.Win32Exception) { } catch (InvalidOperationException) { }
                    if (!String.IsNullOrEmpty(executable))
                    {
                        result.Key = executable;
                        result.Name = Path.GetFileName(executable);
                    }
                    else result.Key = "process:" + result.Name + ":" + began.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception error)
            {
                if (!(error is ArgumentException || error is InvalidOperationException || error is System.ComponentModel.Win32Exception || error is System.Security.SecurityException || error is NotSupportedException)) throw;
                if (result.Instance != null) { result.Instance.Dispose(); result.Instance = null; }
                if (cached != null && cached.Started <= at && EndedAfter(cached, at)) return cached;
                result.Name = "无法识别（PID " + pid + "）";
                result.Key = "unknown:" + unknownSession + ":" + pid + ":" + at.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            }
            lock (sync)
            {
                if (identities.Count >= MaximumIdentities && !identities.ContainsKey(pid))
                {
                    int oldest = identities.OrderBy(p => p.Value.Checked).First().Key;
                    if (identities[oldest].Instance != null) identities[oldest].Instance.Dispose();
                    identities.Remove(oldest);
                }
                if (cached != null && cached.Instance != null && cached != result) cached.Instance.Dispose();
                identities[pid] = result;
            }
            return result;
        }

        private static bool EndedAfter(IdentityInfo identity, DateTime at)
        {
            try { return identity.Instance != null && (!identity.Instance.HasExited || identity.Instance.ExitTime >= at); }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }

        private void AddPacket(string identity, string name, uint bytes, bool download, DateTime at)
        {
            if (at.Date < DateTime.Today.AddDays(-RetentionDays) || at.Date > DateTime.Today.AddDays(1)) { limited = true; return; }
            string date = at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!rates.ContainsKey(identity) && rates.Count >= MaximumIdentities)
            {
                limited = true;
                identity = "other:record-limit";
                name = "其他程序（记录上限）";
            }
            string key = date + "\n" + identity;
            ProcessUsageDay day;
            if (!days.TryGetValue(key, out day))
            {
                if (days.Count >= MaximumRecords || rates.Count >= MaximumIdentities)
                {
                    limited = true;
                    identity = "other:record-limit";
                    name = "其他程序（记录上限）";
                    key = date + "\n" + identity;
                }
                if (!days.TryGetValue(key, out day))
                {
                    day = new ProcessUsageDay { Date = date, Identity = identity, Name = name };
                    days.Add(key, day);
                }
            }
            if (download) day.Download = Add(day.Download, bytes); else day.Upload = Add(day.Upload, bytes);
            Rate rate;
            if (!rates.TryGetValue(identity, out rate)) { rate = new Rate { Name = name }; rates.Add(identity, rate); }
            if (download) rate.Down = Add(rate.Down, bytes); else rate.Up = Add(rate.Up, bytes);
            revision++;
        }

        public ProcessTrafficSnapshot GetSnapshot(DateTime now, int billingDay)
        {
            var period = TrafficMonitor.BillingPeriod(now, billingDay);
            string today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string first = period.Item1.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string end = period.Item2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            lock (sync)
            {
                var rows = new Dictionary<string, ProcessTrafficRow>(StringComparer.OrdinalIgnoreCase);
                foreach (ProcessUsageDay day in days.Values)
                {
                    bool inPeriod = String.CompareOrdinal(day.Date, first) >= 0 && String.CompareOrdinal(day.Date, end) < 0;
                    if (!inPeriod && day.Date != today) continue;
                    ProcessTrafficRow row;
                    if (!rows.TryGetValue(day.Identity, out row))
                    {
                        row = new ProcessTrafficRow { Identity = day.Identity, Name = day.Name, ProcessIds = "—" };
                        rows.Add(day.Identity, row);
                    }
                    if (day.Date == today) { row.TodayDownload = Add(row.TodayDownload, day.Download); row.TodayUpload = Add(row.TodayUpload, day.Upload); }
                    if (inPeriod) { row.CycleDownload = Add(row.CycleDownload, day.Download); row.CycleUpload = Add(row.CycleUpload, day.Upload); }
                }
                var activePids = identities.Values.Where(p => (DateTime.UtcNow - p.Checked).TotalSeconds < 5)
                    .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => String.Join(", ", g.Select(p => p.Pid).Distinct().OrderBy(p => p).Select(p => p.ToString(CultureInfo.InvariantCulture)).ToArray()), StringComparer.OrdinalIgnoreCase);
                foreach (var pair in rates)
                {
                    ProcessTrafficRow row;
                    if (!rows.TryGetValue(pair.Key, out row)) continue;
                    row.DownloadPerSecond = collecting ? pair.Value.DownRate : 0;
                    row.UploadPerSecond = collecting ? pair.Value.UpRate : 0;
                    string pids;
                    row.ProcessIds = activePids.TryGetValue(pair.Key, out pids) ? pids : "—";
                }
                return new ProcessTrafficSnapshot
                {
                    Rows = rows.Values.OrderByDescending(r => (double)r.CycleDownload + r.CycleUpload).ToList(),
                    IsCollecting = collecting, NeedsElevation = needsElevation, EventsLost = Interlocked.Read(ref lost),
                    Status = status + (limited ? "；已到记录上限，部分流量归入其他程序（保留最近 400 天）" : ""),
                    StorageError = storageError, StartedOn = startedOn, SampledAt = now
                };
            }
        }

        private uint OnBuffer(IntPtr pointer)
        {
            try
            {
                ulong handle = sessionHandle;
                if (handle != 0)
                {
                    // ControlTrace query statistics, rather than the documented
                    // unused EVENT_TRACE_LOGFILE.EventsLost member.
                    IntPtr query = CreateProperties();
                    try
                    {
                        if (Native.ControlTrace(handle, sessionName, query, 0) == 0)
                        {
                            var value = (Native.Properties)Marshal.PtrToStructure(query, typeof(Native.Properties));
                            long observed = value.EventsLost + (long)value.RealTimeBuffersLost;
                            long prior;
                            do { prior = Interlocked.Read(ref lost); if (observed <= prior) break; } while (Interlocked.CompareExchange(ref lost, observed, prior) != prior);
                        }
                    }
                    finally { Marshal.FreeHGlobal(query); }
                }
            }
            catch { }
            return 1;
        }

        private void TimerTick(object ignored)
        {
            if (Interlocked.Exchange(ref timerBusy, 1) != 0) return;
            try
            {
                lock (sync)
                {
                    if (disposed || disposeRequested) return;
                    long stamp = Stopwatch.GetTimestamp();
                    double seconds = (stamp - rateStamp) / (double)Stopwatch.Frequency;
                    rateStamp = stamp;
                    foreach (Rate rate in rates.Values)
                    {
                        rate.DownRate = collecting && seconds > .1 && seconds <= 10 ? rate.Down / seconds : 0;
                        rate.UpRate = collecting && seconds > .1 && seconds <= 10 ? rate.Up / seconds : 0;
                        rate.Down = 0; rate.Up = 0;
                    }
                    string earliest = DateTime.Today.AddDays(-RetentionDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    foreach (string key in days.Where(p => String.CompareOrdinal(p.Value.Date, earliest) < 0).Select(p => p.Key).ToArray()) { days.Remove(key); revision++; }
                }
                if (DateTime.UtcNow >= nextSave) Save();
            }
            catch (Exception error) { lock (sync) storageError = "程序记录保存失败：" + error.Message; nextSave = DateTime.UtcNow.AddSeconds(30); }
            finally { Interlocked.Exchange(ref timerBusy, 0); }
        }

        private void Load()
        {
            ProcessUsageStore store = null;
            if (File.Exists(path))
            {
                try { store = Read(path); }
                catch (Exception error) { if (!StorageFailure(error)) throw; preservePrimary = true; storageError = "程序记录损坏，尝试恢复备份。"; }
            }
            if (store == null && File.Exists(path + ".bak"))
            {
                try { store = Read(path + ".bak"); recoveredBackup = true; revision++; storageError = "程序记录已从备份恢复。"; }
                catch (Exception error) { if (!StorageFailure(error)) throw; preserveBackup = true; storageError = "程序记录和备份无法读取；原件将在重新保存时留档。"; }
            }
            if (store == null) return;
            startedOn = store.StartedOn;
            foreach (ProcessUsageDay day in store.Days)
            {
                string key = day.Date + "\n" + day.Identity;
                ProcessUsageDay existing;
                if (days.TryGetValue(key, out existing)) { existing.Download = Add(existing.Download, day.Download); existing.Upload = Add(existing.Upload, day.Upload); }
                else days.Add(key, day);
            }
        }

        private static ProcessUsageStore Read(string file)
        {
            var options = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 };
            var document = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(file, options)) document.Load(reader);
            if (document.DocumentElement == null || document.DocumentElement.Name != "ProcessUsageStore") throw new FormatException("Incomplete program usage file.");
            foreach (string name in new[] { "Version", "StartedOn", "Days" })
                if (document.SelectNodes("/ProcessUsageStore/" + name).Count != 1) throw new FormatException("Missing or duplicate program usage field.");
            foreach (XmlNode node in document.SelectSingleNode("/ProcessUsageStore/Days").ChildNodes)
                if (node.NodeType == XmlNodeType.Element && node.Name != "ProcessUsageDay") throw new FormatException("Unknown program usage entry element.");
            XmlNodeList entries = document.SelectNodes("/ProcessUsageStore/Days/ProcessUsageDay");
            if (entries.Count > MaximumRecords + RetentionDays + 2) throw new FormatException("Too many program usage entries.");
            foreach (XmlNode node in entries)
                foreach (string name in new[] { "Date", "Identity", "Name", "Download", "Upload" })
                    if (node.SelectNodes(name).Count != 1) throw new FormatException("Missing or duplicate program usage entry field.");
            ProcessUsageStore store;
            using (XmlReader reader = new XmlNodeReader(document)) store = (ProcessUsageStore)new XmlSerializer(typeof(ProcessUsageStore)).Deserialize(reader);
            if (store == null || store.Version != 1 || store.StartedOn == DateTime.MinValue || store.StartedOn > DateTime.Now.AddDays(1) || store.Days == null || store.Days.Count > MaximumRecords + RetentionDays + 2) throw new FormatException("Unsupported program usage file.");
            foreach (ProcessUsageDay day in store.Days)
            {
                DateTime date;
                if (day == null || String.IsNullOrWhiteSpace(day.Identity) || day.Identity.Length > 4096 || String.IsNullOrWhiteSpace(day.Name) || day.Name.Length > 1024 || !DateTime.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date) || day.Download < 0 || day.Upload < 0) throw new FormatException("Invalid program usage entry.");
            }
            return store;
        }

        private void Save()
        {
            lock (writeLock)
            {
                ProcessUsageStore snapshot;
                long version;
                lock (sync)
                {
                    version = revision;
                    if (version == savedRevision) { nextSave = DateTime.UtcNow.AddSeconds(10); return; }
                    snapshot = new ProcessUsageStore { Version = 1, StartedOn = startedOn, Days = days.Values.Select(d => new ProcessUsageDay { Date = d.Date, Identity = d.Identity, Name = d.Name, Download = d.Download, Upload = d.Upload }).ToList() };
                }
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { new XmlSerializer(typeof(ProcessUsageStore)).Serialize(stream, snapshot); stream.Flush(true); }
                    string damaged = ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
                    if (preserveBackup && File.Exists(path + ".bak")) File.Copy(path + ".bak", path + ".bak" + damaged, false);
                    if (recoveredBackup && File.Exists(path + ".bak")) File.Copy(path + ".bak", path + ".bak.recovered-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture), false);
                    if (File.Exists(path)) File.Replace(temporary, path, preservePrimary ? path + damaged : path + ".bak", true);
                    else File.Move(temporary, path);
                    preserveBackup = false; preservePrimary = false; recoveredBackup = false;
                    lock (sync) { savedRevision = version; storageError = null; }
                    nextSave = DateTime.UtcNow.AddSeconds(10);
                }
                catch (Exception error)
                {
                    if (!StorageFailure(error)) throw;
                    lock (sync) storageError = "程序记录保存失败：" + error.Message;
                    nextSave = DateTime.UtcNow.AddSeconds(30);
                }
                finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
        }

        private static bool StorageFailure(Exception error)
        {
            return error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException || error is XmlException || error is FormatException || error is System.Security.SecurityException;
        }
        private static long Add(long left, long right) { return right > Int64.MaxValue - left ? Int64.MaxValue : left + right; }

        private bool StopNative()
        {
            if (!nativeStopRequested && sessionHandle != 0 && properties != IntPtr.Zero)
            {
                uint result = Native.ControlTrace(sessionHandle, sessionName, properties, 1);
                nativeStopRequested = result == 0 || result == 4201;
            }
            if (!nativeCloseRequested && consumerHandle != UInt64.MaxValue)
            {
                uint result = Native.CloseTrace(consumerHandle);
                // ERROR_CTX_CLOSE_PENDING is successful asynchronous cancellation:
                // ProcessTrace still owns its callback pointers until it returns.
                nativeCloseRequested = result == 0 || result == 7007;
            }
            if (consumer != null && consumer != Thread.CurrentThread && !consumer.Join(5000)) return false;
            if (consumer == Thread.CurrentThread) return false;
            consumer = null;
            consumerHandle = UInt64.MaxValue;
            sessionHandle = 0;
            if (properties != IntPtr.Zero) { Marshal.FreeHGlobal(properties); properties = IntPtr.Zero; }
            nativeStopRequested = false;
            nativeCloseRequested = false;
            return true;
        }

        public void Dispose()
        {
            lock (lifecycle)
            {
                if (disposed || disposeRequested) return;
                disposeRequested = true;
                timer.Dispose();
                // Stop/flush first so the final buffered events are persisted.
                if (!StopNative())
                {
                    lock (sync) { collecting = false; status = "采集正在结束；最后一批记录仍在处理，暂不能重新启用"; }
                    Save();
                    // Keep the monitor, callbacks, properties and process handles
                    // rooted until the native consumer has acknowledged shutdown.
                    var finalizer = new Thread(delegate()
                    {
                        Thread pending = consumer;
                        if (pending != null) pending.Join();
                        lock (lifecycle) { StopNative(); FinishDispose(); }
                    }) { IsBackground = false, Name = "FlowDesk final program traffic save" };
                    // A foreground thread keeps the process alive after the WPF
                    // loop exits, so CLR shutdown cannot cut off its final save.
                    finalizer.Start();
                    return;
                }
                FinishDispose();
            }
        }

        private void FinishDispose()
        {
            lock (sync)
            {
                disposed = true; collecting = false;
                foreach (IdentityInfo identity in identities.Values) if (identity.Instance != null) identity.Instance.Dispose();
            }
            Save();
            lock (sync) { status = "程序流量统计已停止"; cleanupCompleted = true; }
        }

        private static class Native
        {
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void EventCallback(IntPtr record);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint BufferCallback(IntPtr logfile);
            [StructLayout(LayoutKind.Sequential)] internal struct Wnode { internal uint BufferSize, ProviderId; internal ulong HistoricalContext, TimeStamp; internal Guid Guid; internal uint ClientContext, Flags; }
            [StructLayout(LayoutKind.Sequential)] internal struct Properties
            {
                internal Wnode Wnode;
                internal uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
                internal int AgeLimit;
                internal uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
                internal IntPtr LoggerThreadId;
                internal uint LogFileNameOffset, LoggerNameOffset;
            }
            [StructLayout(LayoutKind.Sequential)] internal struct TraceHeader { internal ushort Size, Flags; internal byte Type, Level; internal ushort Version; internal uint ThreadId, ProcessId; internal long TimeStamp; internal Guid Guid; internal uint KernelTime, UserTime; }
            [StructLayout(LayoutKind.Sequential)] internal struct Trace { internal TraceHeader Header; internal uint InstanceId, ParentInstanceId; internal Guid ParentGuid; internal IntPtr MofData; internal uint MofLength, BufferContext; }
            [StructLayout(LayoutKind.Sequential, Size = 172)] internal struct TimeZone { internal int Bias; }
            [StructLayout(LayoutKind.Sequential)] internal struct LogHeader
            {
                internal uint BufferSize, Version, ProviderVersion, NumberOfProcessors;
                internal long EndTime;
                internal uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten, StartBuffers, PointerSize, EventsLost, CpuSpeedInMHz;
                internal IntPtr LoggerName, LogFileName;
                internal TimeZone TimeZone;
                internal long BootTime, PerfFreq, StartTime;
                internal uint ReservedFlags, BuffersLost;
            }
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct LogFile
            {
                [MarshalAs(UnmanagedType.LPWStr)] internal string LogFileName;
                [MarshalAs(UnmanagedType.LPWStr)] internal string LoggerName;
                internal long CurrentTime;
                internal uint BuffersRead, ProcessTraceMode;
                internal Trace CurrentEvent;
                internal LogHeader LogfileHeader;
                internal BufferCallback BufferCallback;
                internal uint BufferSize, Filled, EventsLost;
                internal EventCallback EventCallback;
                internal uint IsKernelTrace;
                internal IntPtr Context;
            }
            [StructLayout(LayoutKind.Sequential)] internal struct EventHeader
            {
                internal ushort Size, HeaderType, Flags, EventProperty;
                internal uint ThreadId, ProcessId;
                internal long TimeStamp;
                internal Guid ProviderId;
                internal ushort Id;
                internal byte Version, Channel, Level, Opcode;
                internal ushort Task;
                internal ulong Keyword;
                internal uint KernelTime, UserTime;
                internal Guid ActivityId;
            }
            [StructLayout(LayoutKind.Sequential)] internal struct Record
            {
                internal EventHeader Header;
                internal uint BufferContext;
                internal ushort ExtendedDataCount, UserDataLength;
                internal IntPtr ExtendedData, UserData, UserContext;
            }
            [DllImport("advapi32.dll", EntryPoint = "StartTraceW", CharSet = CharSet.Unicode)] internal static extern uint StartTrace(out ulong handle, string name, IntPtr properties);
            [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", CharSet = CharSet.Unicode)] internal static extern uint ControlTrace(ulong handle, string name, IntPtr properties, uint code);
            [DllImport("advapi32.dll", EntryPoint = "OpenTraceW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ulong OpenTrace(ref LogFile logfile);
            [DllImport("advapi32.dll")] internal static extern uint ProcessTrace([In] ulong[] handles, uint count, IntPtr start, IntPtr end);
            [DllImport("advapi32.dll")] internal static extern uint CloseTrace(ulong handle);
        }
    }
}
