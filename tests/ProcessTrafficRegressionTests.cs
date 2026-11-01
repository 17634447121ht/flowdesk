using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml.Serialization;
using FlowDesk;
class ProcessTrafficTests
{
    static int assertions;
    static void Check(bool value, string name) { if(!value) throw new Exception(name); assertions++; Console.WriteLine("PASS " + name); }
    static void CallbackNoop(IntPtr pointer) { }
    static void Main(string[] args)
    {
        if(args.Length>0 && args[0]=="--quick-exit") return;
        string dir=Path.Combine(Environment.CurrentDirectory,"work","process-traffic-tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Type native=typeof(ProcessTrafficMonitor).GetNestedType("Native",BindingFlags.NonPublic);
        Console.WriteLine("NATIVE bitness="+IntPtr.Size);
        foreach(string name in new[]{"Properties","LogFile","EventHeader","Record"}) Console.WriteLine(name+"="+Marshal.SizeOf(native.GetNestedType(name,BindingFlags.NonPublic)));
        using(var monitor=new ProcessTrafficMonitor(dir))
        {
            int pid=Process.GetCurrentProcess().Id;
            DateTime at=DateTime.Now;
            monitor.RecordPacket(pid,101,true,at);
            monitor.RecordPacket(pid,33,false,at);
            var snap=monitor.GetSnapshot(at,1);
            Check(snap.Rows.Count==1,"same executable grouped");
            Check(snap.Rows[0].TodayDownload==101 && snap.Rows[0].TodayUpload==33,"daily accounting");
            Check(snap.Rows[0].CycleDownload==101 && snap.Rows[0].CycleUpload==33,"period accounting");
            Check(snap.Rows[0].Name.EndsWith("ProcessTrafficTests.exe",StringComparison.OrdinalIgnoreCase),"current PID attributed to executable");
            IntPtr payload=Marshal.AllocHGlobal(8), record=Marshal.AllocHGlobal(IntPtr.Size==8?112:100);
            try
            {
                for(int i=0;i<(IntPtr.Size==8?112:100);i++) Marshal.WriteByte(record,i,0);
                Marshal.WriteInt32(payload,pid); Marshal.WriteInt32(payload,4,1000);
                Marshal.WriteInt64(record,16,DateTime.UtcNow.ToFileTimeUtc());
                byte[] provider=new Guid("9a280ac0-c8e0-11d1-84e2-00c04fb998a2").ToByteArray(); Marshal.Copy(provider,0,IntPtr.Add(record,24),16);
                Marshal.WriteByte(record,42,2); Marshal.WriteByte(record,45,11); Marshal.WriteInt16(record,86,8);
                Marshal.WriteIntPtr(record,IntPtr.Size==8?96:92,payload);
                var callback=typeof(ProcessTrafficMonitor).GetMethod("OnEvent",BindingFlags.Instance|BindingFlags.NonPublic);
                callback.Invoke(monitor,new object[]{record});
                Check(monitor.GetSnapshot(at,1).Rows[0].TodayDownload==1101,"TCP receive payload PID parser");
                Marshal.WriteByte(record,45,14); callback.Invoke(monitor,new object[]{record});
                Check(monitor.GetSnapshot(at,1).Rows[0].TodayDownload==1101,"TCP retransmission excluded");
                provider=new Guid("bf3a50c5-a9c9-4988-a005-2df0b7c80f80").ToByteArray(); Marshal.Copy(provider,0,IntPtr.Add(record,24),16);
                Marshal.WriteByte(record,45,26); callback.Invoke(monitor,new object[]{record});
                Check(monitor.GetSnapshot(at,1).Rows[0].TodayUpload==1033,"UDP IPv6 send parser");
            }
            finally { Marshal.FreeHGlobal(payload); Marshal.FreeHGlobal(record); }
        }
        using(var restored=new ProcessTrafficMonitor(dir))
        {
            var snap=restored.GetSnapshot(DateTime.Now,1);
            Check(snap.Rows[0].TodayDownload==1101 && snap.Rows[0].TodayUpload==1033,"persisted attribution restored");
        }
        string file=Path.Combine(dir,"process-usage.xml");
        File.Copy(file,file+".bak",true);
        File.WriteAllText(file,"<ProcessUsageStore />");
        using(var restored=new ProcessTrafficMonitor(dir))
        {
            var snap=restored.GetSnapshot(DateTime.Now,1);
            Check(snap.Rows.Count==1 && snap.Rows[0].TodayDownload==1101,"incomplete primary recovers backup");
            Check(!String.IsNullOrEmpty(snap.StorageError),"recovery warning retained");
            restored.RecordPacket(Process.GetCurrentProcess().Id,1,true,DateTime.Now);
        }
        Check(Directory.GetFiles(dir,"*.damaged-*").Length==1,"damaged primary archived");
        Check(Directory.GetFiles(dir,"*.recovered-*").Length==1,"recovered backup archived");
        string valid=File.ReadAllText(file);
        foreach(string test in new[]{"Download","Upload","StartedOn","duplicate Version","unknown day"})
        {
            var doc=new System.Xml.XmlDocument(); doc.LoadXml(valid);
            if(test=="unknown day") doc.LoadXml(doc.OuterXml.Replace("<ProcessUsageDay>","<WrongName>").Replace("</ProcessUsageDay>","</WrongName>"));
            else if(test=="duplicate Version") doc.DocumentElement.AppendChild(doc.SelectSingleNode("/ProcessUsageStore/Version").CloneNode(true));
            else { var node=doc.SelectSingleNode(test=="StartedOn"?"/ProcessUsageStore/StartedOn":"/ProcessUsageStore/Days/ProcessUsageDay/"+test); node.ParentNode.RemoveChild(node); }
            string malformed=Path.Combine(dir,"malformed-"+test.Replace(" ","-")); Directory.CreateDirectory(malformed);
            File.WriteAllText(Path.Combine(malformed,"process-usage.xml"),doc.OuterXml);
            File.WriteAllText(Path.Combine(malformed,"process-usage.xml.bak"),valid);
            using(var restored=new ProcessTrafficMonitor(malformed)) Check(restored.GetSnapshot(DateTime.Now,1).StorageError!=null,"missing/duplicate field rejected: "+test);
        }
        using(var capped=new ProcessTrafficMonitor(Path.Combine(dir,"cap")))
        {
            var add=typeof(ProcessTrafficMonitor).GetMethod("AddPacket",BindingFlags.Instance|BindingFlags.NonPublic);
            for(int i=0;i<5000;i++) add.Invoke(capped,new object[]{"test:"+i,"Test "+i,(uint)1,true,DateTime.Now});
            var snap=capped.GetSnapshot(DateTime.Now,1);
            Check(snap.Rows.Count<=4097,"identity memory bound");
            Check(snap.Rows.Sum(r=>r.TodayDownload)==5000,"record-limit overflow preserves byte totals");
            Check(snap.Rows.Any(r=>r.Identity=="other:record-limit"),"overflow attribution explicitly marked");
        }
        using(var monitor=new ProcessTrafficMonitor(Path.Combine(dir,"pid-reuse")))
        using(var ended=Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,"--quick-exit"){UseShellExecute=false,CreateNoWindow=true}))
        {
            ended.WaitForExit();
            Type identityType=typeof(ProcessTrafficMonitor).GetNestedType("IdentityInfo",BindingFlags.NonPublic);
            object fake=Activator.CreateInstance(identityType,true);
            foreach(var item in new[]{new object[]{"Key","wrong-old-executable"},new object[]{"Name","Wrong"},new object[]{"Pid",Process.GetCurrentProcess().Id},new object[]{"Started",DateTime.Now.AddHours(-1)},new object[]{"Checked",DateTime.UtcNow},new object[]{"Instance",ended}})
                identityType.GetField((string)item[0],BindingFlags.Instance|BindingFlags.NonPublic).SetValue(fake,item[1]);
            var identities=typeof(ProcessTrafficMonitor).GetField("identities",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(monitor) as System.Collections.IDictionary;
            identities.Add(Process.GetCurrentProcess().Id,fake);
            monitor.RecordPacket(Process.GetCurrentProcess().Id,1,true,DateTime.Now);
            Check(monitor.GetSnapshot(DateTime.Now,1).Rows[0].Name.EndsWith("ProcessTrafficTests.exe",StringComparison.OrdinalIgnoreCase),"exited cached process is not reused for new PID lifetime");
        }
        string pendingDir=Path.Combine(dir,"pending-shutdown");
        using(var release=new ManualResetEvent(false))
        {
            var pending=new ProcessTrafficMonitor(pendingDir);
            pending.RecordPacket(Process.GetCurrentProcess().Id,1,true,DateTime.Now);
            var worker=new Thread(delegate(){release.WaitOne();}){IsBackground=true}; worker.Start();
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var monitorType=typeof(ProcessTrafficMonitor);
            monitorType.GetField("consumer",flags).SetValue(pending,worker);
            IntPtr allocation=Marshal.AllocHGlobal(120);
            monitorType.GetField("properties",flags).SetValue(pending,allocation);
            Type callbackType=native.GetNestedType("EventCallback",BindingFlags.NonPublic);
            var callback=Delegate.CreateDelegate(callbackType,typeof(ProcessTrafficTests).GetMethod("CallbackNoop",BindingFlags.Static|BindingFlags.NonPublic));
            monitorType.GetField("eventCallback",flags).SetValue(pending,callback);
            pending.Dispose();
            Check((IntPtr)monitorType.GetField("properties",flags).GetValue(pending)==allocation,"shutdown timeout retains native memory until consumer exits");
            Check(Object.ReferenceEquals(monitorType.GetField("eventCallback",flags).GetValue(pending),callback),"shutdown timeout retains callback delegate");
            Check(pending.GetSnapshot(DateTime.Now,1).Status.Contains("最后一批"),"pending shutdown explicitly reported");
            bool refused=false;try{pending.Start();}catch(ObjectDisposedException){refused=true;}
            Check(refused,"restart refused while previous native consumer is alive");
            var identities=monitorType.GetField("identities",flags).GetValue(pending) as System.Collections.IDictionary;
            int identityCount=identities.Count;
            pending.RecordPacket(99999999,2,true,DateTime.Now);
            Check(identities.Count==identityCount,"late shutdown events do not open or cache new process handles");
            release.Set(); worker.Join();
            Check(SpinWait.SpinUntil(delegate(){return pending.IsShutdownComplete;},2000),"shutdown finalizer acknowledges consumer exit and final save");
            Check((IntPtr)monitorType.GetField("properties",flags).GetValue(pending)==IntPtr.Zero,"native memory released after consumer acknowledgement");
            pending.Dispose();
        }
        using(var restored=new ProcessTrafficMonitor(pendingDir)) Check(restored.GetSnapshot(DateTime.Now,1).Rows.Sum(r=>r.TodayDownload)==3,"late buffered bytes persisted after deferred shutdown");
        Console.WriteLine("ADMIN="+ProcessTrafficMonitor.IsAdministrator);
        string live=Path.Combine(dir,"live");
        using(var monitor=new ProcessTrafficMonitor(live))
        {
            monitor.Start();
            var snap=monitor.GetSnapshot(DateTime.Now,1);
            Console.WriteLine("LIVE="+snap.IsCollecting+" needsElevation="+snap.NeedsElevation+" status="+snap.Status);
            if(!ProcessTrafficMonitor.IsAdministrator)
            {
                Check(!snap.IsCollecting && snap.NeedsElevation,"standard token returns elevation status");
                Console.WriteLine("SKIP real ETW capture and network attribution: requires an elevated token; no UAC requested.");
            }
            else if(snap.IsCollecting)
            {
                try { using(var web=new System.Net.WebClient()) Console.WriteLine("download="+web.DownloadData("https://learn.microsoft.com/en-us/windows/win32/etw/udpip-typegroup1").Length); }
                catch(Exception error) { Console.WriteLine("download failed="+error.Message); }
                Thread.Sleep(3500);
                snap=monitor.GetSnapshot(DateTime.Now,1);
                Console.WriteLine("LIVE rows="+snap.Rows.Count+" lost="+snap.EventsLost);
                foreach(var row in snap.Rows) Console.WriteLine(row.Name+" pid="+row.ProcessIds+" down="+row.TodayDownload+" up="+row.TodayUpload);
                Check(snap.Rows.Any(r=>r.Name.EndsWith("ProcessTrafficTests.exe",StringComparison.OrdinalIgnoreCase) && r.TodayDownload>0),"real network receive attributed to test executable");
            }
            else throw new Exception("Elevated ETW start failed: "+snap.Status);
        }
        Console.WriteLine("ASSERTIONS="+assertions);
    }
}
