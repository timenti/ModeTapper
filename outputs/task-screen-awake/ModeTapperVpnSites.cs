using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class VpnSitesForm : Form {
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int value,int size);
    [DllImport("uxtheme.dll",CharSet=CharSet.Unicode)]static extern int SetWindowTheme(IntPtr window,string theme,string ids);
    [DllImport("user32.dll")]static extern uint GetDpiForSystem();
    [DllImport("user32.dll")]static extern uint GetDpiForWindow(IntPtr hwnd);
    readonly string path;readonly TextBox proxy,direct;readonly Label title,description,proxyLabel,directLabel,status;
    readonly Panel proxyPanel,directPanel;readonly Button cancel,save;
    readonly Color bg=Color.FromArgb(32,32,32),panel=Color.FromArgb(43,43,43),fg=Color.FromArgb(243,243,243),muted=Color.FromArgb(166,166,166);
    readonly JavaScriptSerializer json=new JavaScriptSerializer();
    readonly float previewScale;float scale;Font regular,titleFont,heading;
    internal VpnSitesForm(string file,float renderScale=0){
        path=file;previewScale=renderScale;scale=renderScale>0?renderScale:GetDpiForSystem()/96f;
        AutoScaleMode=AutoScaleMode.None;Text="ModeTapper · Сайты";BackColor=bg;ForeColor=fg;
        string iconPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ModeTapper.ico");if(File.Exists(iconPath))Icon=new Icon(iconPath);
        StartPosition=FormStartPosition.CenterScreen;MaximizeBox=false;MinimizeBox=false;ShowInTaskbar=false;
        title=Label("Мои сайты",fg);description=Label("Дополнения к встроенным правилам. Один домен в строке, без https://.\nПоддомены учитываются автоматически.",muted);
        proxyLabel=Label("Через VPN",Color.FromArgb(78,202,129));directLabel=Label("Напрямую",fg);
        proxy=Editor();direct=Editor();proxy.HandleCreated+=(s,e)=>SetWindowTheme(proxy.Handle,"DarkMode_Explorer",null);direct.HandleCreated+=(s,e)=>SetWindowTheme(direct.Handle,"DarkMode_Explorer",null);proxyPanel=EditorPanel(proxy);directPanel=EditorPanel(direct);
        status=Label("",Color.FromArgb(236,104,104));status.AutoEllipsis=true;
        cancel=MakeButton("Отмена");cancel.Click+=(s,e)=>Close();cancel.DialogResult=DialogResult.Cancel;
        save=MakeButton("Сохранить");save.Click+=(s,e)=>Save();AcceptButton=save;CancelButton=cancel;
        Controls.AddRange(new Control[]{title,description,proxyLabel,directLabel,proxyPanel,directPanel,status,cancel,save});
        ApplyScale();ClientSize=new Size(S(580),S(450));LayoutControls();
        var values=json.Deserialize<Dictionary<string,string[]>>(File.ReadAllText(path));string[] list;
        if(values.TryGetValue("proxy",out list)&&list!=null)proxy.Lines=list;
        if(values.TryGetValue("direct",out list)&&list!=null)direct.Lines=list;
    }
    int S(float value){return (int)Math.Round(value*scale);}
    Label Label(string text,Color color){return new Label{Text=text,ForeColor=color,AutoSize=false,UseCompatibleTextRendering=false,Margin=Padding.Empty};}
    TextBox Editor(){return new TextBox{Multiline=true,AcceptsReturn=true,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None,BackColor=panel,ForeColor=fg,Dock=DockStyle.Fill};}
    Panel EditorPanel(TextBox editor){var box=new Panel{BackColor=panel};box.Controls.Add(editor);return box;}
    Button MakeButton(string text){var button=new Button{Text=text,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(48,48,48),ForeColor=fg,Cursor=Cursors.Hand,UseCompatibleTextRendering=false};button.FlatAppearance.BorderSize=0;button.FlatAppearance.MouseOverBackColor=Color.FromArgb(61,61,61);return button;}
    void ApplyScale(){
        var oldRegular=regular;var oldTitle=titleFont;var oldHeading=heading;
        regular=new Font("Segoe UI",13*scale,FontStyle.Regular,GraphicsUnit.Pixel);
        titleFont=new Font("Segoe UI Semibold",22*scale,FontStyle.Regular,GraphicsUnit.Pixel);
        heading=new Font("Segoe UI Semibold",13*scale,FontStyle.Regular,GraphicsUnit.Pixel);
        Font=regular;title.Font=titleFont;proxyLabel.Font=directLabel.Font=heading;
        proxyPanel.Padding=directPanel.Padding=new Padding(S(12));
        MinimumSize=new Size(S(520),S(410));
        if(oldRegular!=null){oldRegular.Dispose();oldTitle.Dispose();oldHeading.Dispose();}
        LayoutControls();
    }
    void LayoutControls(){
        if(save==null)return;
        int margin=S(24),gap=S(16),width=ClientSize.Width-margin*2,half=(width-gap)/2,right=margin+half+gap;
        title.SetBounds(margin,S(20),width,S(32));
        description.SetBounds(margin,S(62),width,S(46));
        proxyLabel.SetBounds(margin,S(120),half,S(24));directLabel.SetBounds(right,S(120),half,S(24));
        int bottom=ClientSize.Height-S(22),buttonHeight=S(38),statusHeight=S(32),editorTop=S(152);
        int statusTop=bottom-buttonHeight-S(8)-statusHeight;
        int editorHeight=Math.Max(S(110),statusTop-editorTop-S(4));
        proxyPanel.SetBounds(margin,editorTop,half,editorHeight);directPanel.SetBounds(right,editorTop,half,editorHeight);
        status.SetBounds(margin,statusTop,width,statusHeight);
        cancel.SetBounds(margin,bottom-buttonHeight,half,buttonHeight);save.SetBounds(right,bottom-buttonHeight,half,buttonHeight);
    }
    protected override void OnResize(EventArgs e){base.OnResize(e);LayoutControls();}
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);if(previewScale<=0){float actual=GetDpiForWindow(Handle)/96f;if(Math.Abs(actual-scale)>.01f){scale=actual;ApplyScale();}}int dark=1,round=2;DwmSetWindowAttribute(Handle,20,ref dark,4);DwmSetWindowAttribute(Handle,33,ref round,4);}
    protected override void WndProc(ref Message m){base.WndProc(ref m);if(m.Msg==0x02E0&&previewScale<=0&&IsHandleCreated){scale=GetDpiForWindow(Handle)/96f;ApplyScale();}}
    string[] Read(TextBox box){var domains=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(string raw in box.Lines){string host=raw.Trim().TrimEnd('.').ToLowerInvariant();if(host=="")continue;if(Uri.CheckHostName(host)!=UriHostNameType.Dns||!host.Contains(".")||host.Contains("://")||host.Contains("/"))throw new InvalidDataException("Проверь домен: "+host);domains.Add(host);}var values=new List<string>(domains);values.Sort(StringComparer.OrdinalIgnoreCase);return values.ToArray();}
    void Save(){try{var values=new Dictionary<string,string[]>{{"direct",Read(direct)},{"proxy",Read(proxy)}};string staging=path+".new";File.WriteAllText(staging,json.Serialize(values));File.Replace(staging,path,null);Close();}catch(Exception ex){status.Text=ex.Message;}}
    protected override bool ShowWithoutActivation {get{return previewScale>0;}}
    internal void SavePreview(string file){StartPosition=FormStartPosition.Manual;Location=new Point(-24000,-24000);Show();Application.DoEvents();LayoutControls();using(var bmp=new Bitmap(Width,Height)){DrawToBitmap(bmp,new Rectangle(Point.Empty,Size));bmp.Save(file,System.Drawing.Imaging.ImageFormat.Png);}}
    protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing&&regular!=null){regular.Dispose();titleFont.Dispose();heading.Dispose();regular=null;}}
}
