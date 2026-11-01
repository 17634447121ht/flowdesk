using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlowDesk;
internal class DialogRegressionTests {
 static BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
 static object F(object o,string name){return o.GetType().GetField(name,flags).GetValue(o);}
 static void Call(object o,string name){o.GetType().GetMethod(name,flags).Invoke(o,null);}
 static void Check(bool pass,string name){Console.WriteLine((pass?"PASS ":"FAIL ")+name);if(!pass)throw new Exception(name);}
 static string FirstName(ProcessUsageDialog d){foreach(object r in (IEnumerable)((ListView)F(d,"list")).ItemsSource)return (string)r.GetType().GetProperty("Name").GetValue(r,null);return "";}
 static void Render(Window window,string path,int width,int height){FrameworkElement ui=(FrameworkElement)window.Content;ui.Measure(new Size(width,height));ui.Arrange(new Rect(0,0,width,height));ui.UpdateLayout();RenderTargetBitmap visual=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);DrawingVisual background=new DrawingVisual();using(DrawingContext dc=background.RenderOpen()){dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(17,27,44)),null,new Rect(0,0,width,height));}visual.Render(background);visual.Render(ui);PngBitmapEncoder png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(visual));using(FileStream s=File.Create(path))png.Save(s);}
 static List<UsageDay> MakeHistory(long amount){List<UsageDay> days=new List<UsageDay>();for(int i=0;i<120;i++)days.Add(new UsageDay{Date=DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd"),Download=i==0?amount:0,Upload=i==0?20:0});return days;}
 static AppSettings SaveSettings(SettingsDialog dialog){
   try{dialog.GetType().GetMethod("Save",flags).Invoke(dialog,new object[]{null,new RoutedEventArgs()});}
   catch(TargetInvocationException e){if(!(e.InnerException is InvalidOperationException)||dialog.Result==null)throw;}
   return dialog.Result;
 }
 static void SettingsModes(string dir){
   foreach(DisplayMode mode in Enum.GetValues(typeof(DisplayMode))){
     AppSettings original=new AppSettings{Mode=mode,MinimalLeft=-1700,MinimalTop=55,HasMinimalPosition=true,MinimalLocked=true,MinimalClickThrough=true,MinimalTextTheme="dark",MinimalTextShadow=false,Left=-800,Top=-200,HasSavedPosition=true,Width=450,Height=300};
     SettingsDialog dialog=new SettingsDialog(original,new List<AdapterChoice>(),false);
     AppSettings saved=SaveSettings(dialog);
     Check(saved.Mode==mode,"settings saves display choice: "+mode);
     Check(saved.MinimalLeft==-1700&&saved.MinimalTop==55&&saved.HasMinimalPosition&&saved.Left==-800&&saved.Top==-200&&saved.Width==450&&saved.Height==300,"settings preserves independent saved positions: "+mode);
     Check(saved.MinimalLocked&&saved.MinimalClickThrough&&saved.MinimalTextTheme=="dark"&&!saved.MinimalTextShadow,"hidden minimal settings retain preferences: "+mode);
     dialog.Close();
   }
   AppSettings current=new AppSettings{Mode=DisplayMode.Minimal};
   SettingsDialog editing=new SettingsDialog(current,new List<AdapterChoice>(),false);
   FrameworkElement options=((ComboBox)F(editing,"minimalTextTheme")).Parent as FrameworkElement;
   Check(options.Visibility==Visibility.Visible,"minimal options shown for minimal mode");
   ((ComboBox)F(editing,"mode")).SelectedIndex=0;
   Check(options.Visibility==Visibility.Collapsed,"minimal options collapse for tray mode");
   ((ComboBox)F(editing,"mode")).SelectedIndex=1;
   ((ComboBox)F(editing,"minimalTextTheme")).SelectedIndex=1;
   ((CheckBox)F(editing,"minimalTextShadow")).IsChecked=false;
   ((CheckBox)F(editing,"minimalLocked")).IsChecked=true;
   ((CheckBox)F(editing,"minimalClickThrough")).IsChecked=true;
   Render(editing,Path.Combine(dir,"settings-minimal.png"),530,680);
   Render(editing,Path.Combine(dir,"settings-minimal-small.png"),460,420);
   AppSettings result=SaveSettings(editing);
   Check(result.Mode==DisplayMode.Minimal&&result.MinimalTextTheme=="light"&&!result.MinimalTextShadow&&result.MinimalLocked&&result.MinimalClickThrough,"minimal controls save chosen appearance and interaction");
   Check(current.MinimalTextTheme=="auto"&&current.MinimalTextShadow&&!current.MinimalLocked&&!current.MinimalClickThrough,"editing settings does not mutate current preferences before applying");
   editing.Close();
 }
 [STAThread] static int Main(string[] args){
   string dir=Path.GetFullPath(args[0]);Directory.CreateDirectory(dir);
   SettingsModes(dir);
   long amount=100;
   HistoryDialog h=new HistoryDialog(delegate{return new MonitorSnapshot{SampledAt=new DateTime(2026,11,1,13,0,0),History=MakeHistory(amount)};});
   Render(h,Path.Combine(dir,"history-before.png"),750,650);
   Check(((TextBlock)F(h,"downloadTotal")).Text=="100 B","history initial totals");
   ScrollViewer hs=(ScrollViewer)F(h,"rowsScroll");hs.ScrollToVerticalOffset(100);hs.UpdateLayout();double historyOffset=hs.VerticalOffset;
   amount=700;Call(h,"RefreshSnapshot");hs.UpdateLayout();Check(Math.Abs(hs.VerticalOffset-historyOffset)<1,"history refresh preserves scroll");
   Check(((TextBlock)F(h,"downloadTotal")).Text=="700 B","history refresh obtains newer usage");
   Check(((TextBlock)F(h,"updatedAt")).Text.Contains("2026-11-01 13:00:00"),"history sample cutoff shown");
   Render(h,Path.Combine(dir,"history-small.png"),460,380);
   ProcessTrafficSnapshot snap=new ProcessTrafficSnapshot{SampledAt=new DateTime(2026,11,1,13,0,0),StartedOn=DateTime.Today,IsCollecting=true,Status="实时监测中",Rows=new List<ProcessTrafficRow>{new ProcessTrafficRow{Name="Browser",Identity="C:\\Apps\\Browser.exe",ProcessIds="111",DownloadPerSecond=200,UploadPerSecond=100,TodayDownload=1000,TodayUpload=200,CycleDownload=5000,CycleUpload=1000},new ProcessTrafficRow{Name="Downloader",Identity="C:\\Apps\\Downloader.exe",ProcessIds="222",DownloadPerSecond=4000,UploadPerSecond=500,TodayDownload=50,TodayUpload=0,CycleDownload=20000,CycleUpload=500}}};
   int enables=0;
   ProcessUsageDialog d=new ProcessUsageDialog(delegate{return snap;},delegate{enables++;snap.IsCollecting=true;});
   Check(FirstName(d)=="Downloader","process default sorts rates numerically");
   ((ComboBox)F(d,"sort")).SelectedIndex=1;
   Check(FirstName(d)=="Browser","today totals sorting");
   ((TextBox)F(d,"search")).Text="222";
   Check(FirstName(d)=="Downloader","search process id");
   ((TextBox)F(d,"search")).Text="nomatch";
   Check(((TextBlock)F(d,"empty")).Visibility==Visibility.Visible,"search empty state");
   ((TextBox)F(d,"search")).Text="";
   Render(d,Path.Combine(dir,"process-rows.png"),952,620);
   snap.IsCollecting=false;snap.NeedsElevation=true;snap.Status="启用程序统计需要管理员权限。";Call(d,"RefreshSnapshot");
   Check(Convert.ToString(((Button)F(d,"enable")).Content)=="以管理员身份启用","permission action shown");
   Call(d,"EnableCollection");
   Check(enables==1&&((Button)F(d,"enable")).Visibility==Visibility.Collapsed,"enable action invokes host then updates");
   snap.Rows=new List<ProcessTrafficRow>();for(int i=0;i<120;i++)snap.Rows.Add(new ProcessTrafficRow{Name="Program "+i,Identity="program-"+i,ProcessIds=i.ToString(),TodayDownload=i*100,CycleDownload=i*200});Call(d,"RefreshSnapshot");
   Render(d,Path.Combine(dir,"process-many.png"),952,620);ListView lv=(ListView)F(d,"list");ScrollViewer ps=(ScrollViewer)d.GetType().GetMethod("FindScroller",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{lv});ps.ScrollToVerticalOffset(20);ps.UpdateLayout();double processOffset=ps.VerticalOffset;Check(processOffset>0,"process list scroll fixture");lv.SelectedIndex=30;string selectedName=(string)lv.SelectedItem.GetType().GetProperty("Name").GetValue(lv.SelectedItem,null);Call(d,"RefreshSnapshot");ps.UpdateLayout();Check(Math.Abs(ps.VerticalOffset-processOffset)<1,"process refresh preserves scroll");Check((string)lv.SelectedItem.GetType().GetProperty("Name").GetValue(lv.SelectedItem,null)==selectedName,"process refresh preserves selection");
   h.Close();d.Close();return 0;
 }
}
