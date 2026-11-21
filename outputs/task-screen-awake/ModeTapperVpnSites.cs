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
    readonly string path;readonly RichTextBox proxy,direct;readonly Label title,description,proxyLabel,directLabel,status;
    readonly Panel proxyPanel,directPanel;readonly Button cancel,save;
    readonly Color bg=Color.FromArgb(22,22,22),panel=Color.FromArgb(29,29,29),fg=Color.FromArgb(246,246,246),muted=Color.FromArgb(174,176,177);
    readonly Color line=Color.FromArgb(64,66,67),accent=Color.FromArgb(126,216,170);
    readonly JavaScriptSerializer json=new JavaScriptSerializer();
    readonly float previewScale;float scale;Font regular,titleFont,heading;
    internal VpnSitesForm(string file,float renderScale=0){
        path=file;previewScale=renderScale;scale=renderScale>0?renderScale:GetDpiForSystem()/96f;
        AutoScaleMode=AutoScaleMode.None;Text="ModeTapper · Список сайтов";BackColor=bg;ForeColor=fg;
        string iconPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ModeTapper.ico");if(File.Exists(iconPath))Icon=new Icon(iconPath);
        StartPosition=FormStartPosition.CenterScreen;MaximizeBox=false;MinimizeBox=false;ShowInTaskbar=false;
        title=Label("Список сайтов",fg);
        description=Label("Один домен на строку, без https://. Поддомены учитываются автоматически.",muted);
        proxyLabel=Label("Через Happ",accent);directLabel=Label("Напрямую",fg);
        proxy=Editor("Через Happ");direct=Editor("Напрямую");proxy.HandleCreated+=(s,e)=>SetWindowTheme(proxy.Handle,"DarkMode_Explorer",null);direct.HandleCreated+=(s,e)=>SetWindowTheme(direct.Handle,"DarkMode_Explorer",null);proxyPanel=EditorPanel(proxy);directPanel=EditorPanel(direct);
        status=Label("",Color.FromArgb(236,104,104));status.AutoEllipsis=true;status.TextAlign=ContentAlignment.MiddleLeft;
        cancel=MakeButton("Отмена",false);cancel.Click+=(s,e)=>Close();cancel.DialogResult=DialogResult.Cancel;
        save=MakeButton("Сохранить",true);save.Click+=(s,e)=>Save();CancelButton=cancel;
        Controls.AddRange(new Control[]{title,description,proxyLabel,directLabel,proxyPanel,directPanel,status,cancel,save});
        ApplyScale();ClientSize=new Size(S(568),S(306));LayoutControls();
        var values=json.Deserialize<Dictionary<string,string[]>>(File.ReadAllText(path));string[] list;
        if(values.TryGetValue("proxy",out list)&&list!=null)proxy.Lines=list;
        if(values.TryGetValue("direct",out list)&&list!=null)direct.Lines=list;
    }
    int S(float value){return (int)Math.Round(value*scale);}
    Label Label(string text,Color color){return new Label{Text=text,ForeColor=color,AutoSize=false,UseCompatibleTextRendering=false,Margin=Padding.Empty};}
    RichTextBox Editor(string name){
        return new RichTextBox{AccessibleName=name,Multiline=true,AcceptsTab=false,DetectUrls=false,WordWrap=false,
            ScrollBars=RichTextBoxScrollBars.Both,BorderStyle=BorderStyle.None,BackColor=panel,ForeColor=fg,Dock=DockStyle.Fill};
    }
    Panel EditorPanel(TextBoxBase editor){
        var box=new Panel{BackColor=panel};
        box.Paint+=(s,e)=>{using(var pen=new Pen(editor.Focused?accent:line))e.Graphics.DrawRectangle(pen,0,0,box.Width-1,box.Height-1);};
        editor.GotFocus+=(s,e)=>box.Invalidate();editor.LostFocus+=(s,e)=>box.Invalidate();
        box.Controls.Add(editor);return box;
    }
    Button MakeButton(string text,bool primary){
        var button=new Button{Text=text,FlatStyle=FlatStyle.Flat,BackColor=primary?accent:panel,ForeColor=primary?bg:fg,
            Cursor=Cursors.Hand,UseVisualStyleBackColor=false};
        button.FlatAppearance.BorderColor=primary?accent:line;
        button.FlatAppearance.MouseOverBackColor=primary?Color.FromArgb(152,229,188):Color.FromArgb(42,44,44);
        return button;
    }
    void ApplyScale(){
        var oldRegular=regular;var oldTitle=titleFont;var oldHeading=heading;
        regular=new Font("Segoe UI",13*scale,FontStyle.Regular,GraphicsUnit.Pixel);
        titleFont=new Font("Segoe UI Semibold",25*scale,FontStyle.Regular,GraphicsUnit.Pixel);
        heading=new Font("Segoe UI Semibold",14*scale,FontStyle.Regular,GraphicsUnit.Pixel);
        Font=regular;title.Font=titleFont;proxyLabel.Font=directLabel.Font=heading;
        proxyPanel.Padding=directPanel.Padding=new Padding(S(11),S(9),S(10),S(8));
        MinimumSize=SizeFromClientSize(new Size(S(496),S(282)));
        if(oldRegular!=null){oldRegular.Dispose();oldTitle.Dispose();oldHeading.Dispose();}
        LayoutControls();
    }
    void LayoutControls(){
        if(save==null)return;
        int margin=S(22),gap=S(14),width=ClientSize.Width-margin*2,half=(width-gap)/2,right=margin+half+gap;
        title.SetBounds(margin,S(16),width,S(33));
        description.SetBounds(margin,S(53),width,S(35));
        proxyLabel.SetBounds(margin,S(91),half,S(22));directLabel.SetBounds(right,S(91),half,S(22));
        int footer=ClientSize.Height-S(54),editorTop=S(116),editorHeight=Math.Max(S(94),footer-editorTop-S(13));
        proxyPanel.SetBounds(margin,editorTop,half,editorHeight);directPanel.SetBounds(right,editorTop,width-half-gap,editorHeight);
        int saveWidth=S(114),cancelWidth=S(85),saveLeft=ClientSize.Width-margin-saveWidth,cancelLeft=saveLeft-cancelWidth-S(8);
        status.SetBounds(margin,footer,Math.Max(0,cancelLeft-margin-S(10)),S(36));
        cancel.SetBounds(cancelLeft,footer,cancelWidth,S(36));save.SetBounds(saveLeft,footer,saveWidth,S(36));
    }
    protected override void OnResize(EventArgs e){base.OnResize(e);LayoutControls();}
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);if(previewScale<=0){float actual=GetDpiForWindow(Handle)/96f;if(Math.Abs(actual-scale)>.01f){scale=actual;ApplyScale();}}int dark=1,round=2;DwmSetWindowAttribute(Handle,20,ref dark,4);DwmSetWindowAttribute(Handle,33,ref round,4);}
    protected override void WndProc(ref Message m){base.WndProc(ref m);if(m.Msg==0x02E0&&previewScale<=0&&IsHandleCreated){scale=GetDpiForWindow(Handle)/96f;ApplyScale();}}
    string[] Read(TextBoxBase box){var domains=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(string raw in box.Lines){string host=raw.Trim().TrimEnd('.').ToLowerInvariant();if(host=="")continue;if(Uri.CheckHostName(host)!=UriHostNameType.Dns||!host.Contains(".")||host.Contains("://")||host.Contains("/"))throw new InvalidDataException("Проверьте домен «"+host+"».");domains.Add(host);}var values=new List<string>(domains);values.Sort(StringComparer.OrdinalIgnoreCase);return values.ToArray();}
    void Save(){try{var values=new Dictionary<string,string[]>{{"direct",Read(direct)},{"proxy",Read(proxy)}};string staging=path+".new";File.WriteAllText(staging,json.Serialize(values));File.Replace(staging,path,null);Close();}catch(Exception ex){status.Text=ex.Message;}}
    protected override bool ShowWithoutActivation {get{return previewScale>0;}}
    internal void SavePreview(string file){StartPosition=FormStartPosition.Manual;Location=new Point(-24000,-24000);Show();Application.DoEvents();LayoutControls();using(var bmp=new Bitmap(Width,Height)){DrawToBitmap(bmp,new Rectangle(Point.Empty,Size));bmp.Save(file,System.Drawing.Imaging.ImageFormat.Png);}}
    protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing&&regular!=null){regular.Dispose();titleFont.Dispose();heading.Dispose();regular=null;}}
}
