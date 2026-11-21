using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

internal sealed partial class LumaFlyout
{
    const int SettingsHeight=523,FooterHeight=43;
    static readonly int[] SectionHeights={137,97,117,114,96,111,70};
    static readonly string[] SectionNames={"Экран","Производительность","Охлаждение","VPN","Жесты","Виджеты","Камеры"};
    readonly ModeTapperPreferences preferences;
    readonly Timer settingsTimer=new Timer{Interval=15};
    readonly Stopwatch settingsTime=new Stopwatch();
    bool settingsOpen,lightTheme;
    string settingsError;
    float settingsProgress,settingsFrom;

    bool SectionAvailable(PanelSection section){
        return section==PanelSection.Cooling?showCooling:
            section==PanelSection.Vpn?showVpn:
            section==PanelSection.Cameras?showCameras:true;
    }
    bool SectionVisible(PanelSection section){return SectionAvailable(section)&&preferences.Shows(section);}

    int SectionY(int section){
        int y=15;
        for(int i=0;i<section;i++)if(SectionVisible((PanelSection)i))y+=SectionHeights[i];
        return y;
    }

    void ApplyPalette(){
        lightTheme=preferences.IsLight;
        background=Color.FromArgb(lightTheme?245:22,lightTheme?246:22,lightTheme?248:22);
        text=Color.FromArgb(lightTheme?29:247,lightTheme?32:247,lightTheme?38:247);
        muted=lightTheme?Color.FromArgb(87,91,100):Color.FromArgb(172,172,172);
        faint=lightTheme?Color.FromArgb(110,115,123):Color.FromArgb(108,108,108);
        surface=lightTheme?Color.White:Color.FromArgb(30,30,30);
        hoverSurface=lightTheme?Color.FromArgb(225,229,235):Color.FromArgb(42,42,42);
        border=lightTheme?Color.FromArgb(211,217,226):Color.FromArgb(56,56,56);
        accent=lightTheme?Color.FromArgb(45,51,62):Color.FromArgb(222,222,222);
        green=lightTheme?Color.FromArgb(17,117,75):Color.FromArgb(110,211,157);
        amber=lightTheme?Color.FromArgb(146,91,8):Color.FromArgb(245,197,87);
        red=lightTheme?Color.FromArgb(168,49,59):Color.FromArgb(235,125,125);
        BackColor=background;
        Invalidate();
    }

    void ToggleSettings(){
        settingsFrom=settingsProgress;
        settingsOpen=!settingsOpen;
        hover=-1;
        keyboardItem=(int)(settingsOpen?Item.CloseSettings:Item.Settings);
        AccessibleName=settingsOpen?Translate("Настройки")+" · ModeTapper":"ModeTapper";
        settingsTime.Restart();
        settingsTimer.Start();
        if(settingsOpen)FitSettings();
        Invalidate();
    }

    void FitSettings(){
        if(slideDisplay.IsEmpty){Fit(Screen.PrimaryScreen.WorkingArea);return;}
        EdgePlacement(slideDisplay,MenuSize,displayRotation,out edgeLocation,out edgeOrigin);
        ResizeMenu();
        if(Visible&&!slide.Enabled)Location=edgeLocation;
    }

    void AnimateSettings(){
        float progress=Math.Min(1,(float)settingsTime.Elapsed.TotalMilliseconds/210);
        float eased=1-(float)Math.Pow(1-progress,3);
        settingsProgress=settingsFrom+((settingsOpen?1:0)-settingsFrom)*eased;
        if(progress>=1){
            settingsTimer.Stop();
            if(!settingsOpen)FitSettings();
        }
        Invalidate();
    }

    void ResetSettings(){
        settingsTimer.Stop();
        settingsOpen=false;
        settingsProgress=0;
        AccessibleName="ModeTapper";
    }

    int SheetTop {get{return ClientSize.Height-S(FooterHeight)-S(SettingsHeight*settingsProgress);}}
    Rectangle SheetRect(int x,int y,int width,int height){
        return new Rectangle(S(x),SheetTop+S(y),S(width),S(height));
    }
    Rectangle GearBounds {get{return new Rectangle(S(159),ClientSize.Height-S(39),S(38),S(34));}}
    Rectangle SettingsBounds(Item item){
        if(item==Item.Settings)return GearBounds;
        if(item==Item.CloseSettings)return SheetRect(291,12,34,32);
        if(item>=Item.ThemeSystem&&item<=Item.ThemeDark)return SheetRect(28+101*((int)item-(int)Item.ThemeSystem),83,96,34);
        if(item==Item.LanguageRu||item==Item.LanguageEn)return SheetRect(item==Item.LanguageRu?28:181,147,147,34);
        if(item>=Item.ShowScreen&&item<=Item.ShowCameras){
            int index=(int)item-(int)Item.ShowScreen;
            return SheetRect(28+(index%2)*153,218+(index/2)*32,147,28);
        }
        if(item==Item.Update)return SheetRect(46,428,264,40);
        return SheetRect(65,473,226,29);
    }

    void DrawGear(Graphics g){
        var gear=GearBounds;
        if(settingsOpen||hover==(int)Item.Settings)FillRound(g,gear,hoverSurface,8);
        DrawKeyboardFocus(g,(int)Item.Settings,gear);
        using(var font=new Font("Segoe MDL2 Assets",16*scale,FontStyle.Regular,GraphicsUnit.Pixel))
            TextAt(g,"\uE713",font,settingsOpen?text:muted,gear,TextFormatFlags.HorizontalCenter);
    }

    void SettingsButton(Graphics g,Item item,string label,bool selected=false,bool primary=false){
        var bounds=SettingsBounds(item);
        bool active=IsEnabled(item);
        Color plate=primary&&active?accent:selected||hover==(int)item?hoverSurface:surface;
        FillRound(g,bounds,plate,8);
        DrawKeyboardFocus(g,(int)item,bounds);
        var ink=primary&&active?background:!active?faint:text;
        TextAt(g,label,primary?heading:button,ink,bounds,TextFormatFlags.HorizontalCenter|TextFormatFlags.EndEllipsis);
    }

    void DrawSettings(Graphics g){
        if(settingsProgress<=0)return;
        var saved=g.Save();
        g.SetClip(new Rectangle(0,0,ClientSize.Width,ClientSize.Height-S(FooterHeight)));
        using(var dim=new SolidBrush(Color.FromArgb((int)(85*settingsProgress),Color.Black)))
            g.FillRectangle(dim,ClientRectangle);
        var sheet=SheetRect(12,0,332,SettingsHeight);
        using(var shape=TrayApp.RoundRect(sheet,S(17)))
        using(var fill=new SolidBrush(background))
        using(var edge=new Pen(border,Math.Max(1,S(.8f)))){
            g.FillPath(fill,shape);
            g.DrawPath(edge,shape);
        }
        TextAt(g,"Настройки",heading,text,SheetRect(28,13,232,30));
        SettingsButton(g,Item.CloseSettings,"×");
        TextAt(g,"Тема",small,muted,SheetRect(28,61,300,19));
        SettingsButton(g,Item.ThemeSystem,"Как в Windows",preferences.Theme==PanelTheme.System);
        SettingsButton(g,Item.ThemeLight,"Светлая",preferences.Theme==PanelTheme.Light);
        SettingsButton(g,Item.ThemeDark,"Тёмная",preferences.Theme==PanelTheme.Dark);
        TextAt(g,"Язык",small,muted,SheetRect(28,125,300,19));
        SettingsButton(g,Item.LanguageRu,"Русский",!preferences.English);
        SettingsButton(g,Item.LanguageEn,"English",preferences.English);
        TextAt(g,"Разделы панели",small,muted,SheetRect(28,193,300,20));

        for(int i=0;i<SectionNames.Length;i++){
            var item=Item.ShowScreen+i;
            var bounds=SettingsBounds(item);
            bool shown=preferences.Shows((PanelSection)i),available=SectionAvailable((PanelSection)i);
            FillRound(g,bounds,available&&hover==(int)item?hoverSurface:surface,7);
            DrawKeyboardFocus(g,(int)item,bounds);
            TextAt(g,SectionNames[i],small,available?text:faint,SheetRect(38+(i%2)*153,218+(i/2)*32,109,28),TextFormatFlags.EndEllipsis);
            var marker=SheetRect(152+(i%2)*153,226+(i/2)*32,12,12);
            using(var outline=TrayApp.RoundRect(marker,S(3)))
            using(var pen=new Pen(available?muted:faint,Math.Max(1,S(.7f))))g.DrawPath(pen,outline);
            if(shown&&available)TextAt(g,"✓",small,text,marker,TextFormatFlags.HorizontalCenter);
        }

        var product=SheetRect(28,352,300,152);
        FillRound(g,product,surface,13);
        using(var brand=new Font("Segoe UI Semibold",25*scale,FontStyle.Regular,GraphicsUnit.Pixel))
            TextAt(g,"ModeTapper",brand,text,SheetRect(39,359,278,37),TextFormatFlags.HorizontalCenter);
        TextAt(g,Translate("Версия")+" "+typeof(LumaFlyout).Assembly.GetName().Version.ToString(3),button,muted,
            SheetRect(39,395,278,22),TextFormatFlags.HorizontalCenter);
        SettingsButton(g,Item.Update,updates.Button,true,true);
        if(updates.ManualAvailable)SettingsButton(g,Item.ManualDownload,"Скачать вручную");
        string status=settingsError??updates.DisplayStatus(preferences.English);
        if(!String.IsNullOrEmpty(status)){
            var where=updates.ManualAvailable?SheetRect(34,505,288,16):SheetRect(34,473,288,24);
            TextAt(g,status,small,settingsError==null?muted:red,where,TextFormatFlags.HorizontalCenter|TextFormatFlags.EndEllipsis);
        }
        g.Restore(saved);
    }

    void SelectSetting(Item item){
        if(item==Item.CloseSettings){ToggleSettings();return;}
        if(item==Item.Update){settingsError=null;updates.Run();return;}
        if(item==Item.ManualDownload){Hide();updates.OpenRelease();return;}
        var theme=preferences.Theme;
        bool english=preferences.English;
        int sections=preferences.Sections;
        if(item>=Item.ThemeSystem&&item<=Item.ThemeDark)theme=(PanelTheme)(item-Item.ThemeSystem);
        else if(item==Item.LanguageRu||item==Item.LanguageEn)english=item==Item.LanguageEn;
        else sections^=1<<(item-Item.ShowScreen);
        try{
            preferences.Save(theme,english,sections);
            settingsError=null;
            ApplyPalette();
            AccessibleName=Translate("Настройки")+" · ModeTapper";
        }catch(IOException){settingsError=Translate("Не удалось сохранить настройки.");}
        catch(UnauthorizedAccessException){settingsError=Translate("Не удалось сохранить настройки.");}
    }

    string Translate(string value){
        string translated;
        return preferences.English&&value!=null&&EnglishWords.TryGetValue(value,out translated)?translated:value;
    }

    static readonly Dictionary<string,string> EnglishWords=new Dictionary<string,string>{
        {"Настройки","Settings"},{"Тема","Theme"},{"Как в Windows","System"},{"Светлая","Light"},{"Тёмная","Dark"},
        {"Язык","Language"},{"Разделы панели","Panel sections"},{"Версия","Version"},
        {"Экран","Display"},{"Режим экрана","Display mode"},{"Всегда включён","Always on"},{"Во время задач","During tasks"},
        {"Выключено","Off"},{"Блокировка поворота","Rotation lock"},{"Производительность","Performance"},
        {"Экономия","Efficiency"},{"Баланс","Balanced"},{"Максимум","Maximum"},{"Охлаждение","Cooling"},
        {"Тихий","Silent"},{"Производительный","Performance"},{"Турбо","Turbo"},{"Зарядка","Charging"},{"От сети","Plugged in"},
        {"Профили заблокированы ASUS. Откройте Armoury Crate.","Profiles locked by ASUS. Open Armoury Crate."},
        {"Управление охлаждением ASUS недоступно.","ASUS cooling controls are unavailable."},
        {"Armoury Crate: неверный режим. Доступен ручной выбор.","Armoury Crate reports a posture mismatch."},
        {"Турбо доступен от сети.","Turbo is available on AC power."},
        {"Ошибка","Error"},{"Нет прокси Happ","Happ proxy unavailable"},{"Включено","On"},{"Маршрутизация","Routing"},
        {"Список сайтов","Site rules"},{"Открыть Happ","Open Happ"},{"Жесты","Gestures"},{"Тачпад","Touchpad"},
        {"Домой","Home"},{"Грани","Edges"},{"Анимации+","Animations+"},{"Лупа","Zoom"},
        {"Виджеты","Widgets"},{"Лимит Codex","Codex usage"},{"Задачи Codex","Codex tasks"},{"Часы","Clock"},{"Календарь","Calendar"},
        {"Камеры","Cameras"},{"Переключение","Switching"},{"Выключены","Off"},{"Включены","On"},
        {"Часть доступна","Some available"},{"Камер нет","No cameras"},{"Недоступно","Unavailable"},
        {"Открыть камеру","Open camera"},{"Включить","Turn on"},{"Выключить","Turn off"},
        {"Безопасный режим","Safe mode"},{"Возобновить работу ModeTapper","Resume ModeTapper"},{"Выход","Exit"},
        {"Проверить обновления","Check for updates"},{"Обновить сейчас","Update now"},{"Скачать вручную","Download manually"},
        {"Проверяем…","Checking…"},{"Обновление…","Updating…"},
        {"Не удалось сохранить настройки.","Could not save settings."},
        {"Датчик поворота Windows недоступен","Windows orientation sensor is unavailable"},
        {"Отключает камеры в Windows, включая камеру входа по лицу","Disables cameras in Windows, including Windows Hello"},
        {"Турбо доступен от сети","Turbo is available on AC power"},
        {"Последний профиль, применённый ModeTapper","Last profile applied by ModeTapper"},
        {"Производительный профиль ASUS","ASUS performance profile"},
        {"От сети — максимум, от батареи — баланс","Maximum on AC power, balanced on battery"},
        {"Штатное энергосбережение Windows и отключение турбоускорения","Windows Energy Saver and CPU boost disabled"},
        {"Умный тачпад","Smart touchpad"},{"Индикатор «Домой»","Home indicator"},{"Грани экрана","Screen edges"},
        {"Переходы окон к значкам панели задач","Window transitions to taskbar icons"},
        {"Telegram: щипок приближает к курсору, движение двумя пальцами сдвигает фото","Telegram: pinch to zoom and use two fingers to pan"}
    };
}
