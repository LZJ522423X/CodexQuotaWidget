using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using CodexQuotaWidget.Core;
using CodexQuotaWidget.Core.ControlCenter;

namespace CodexQuotaWidget;
public sealed class StatusWindow:Window
{
    readonly QuotaMonitor monitor;
    readonly SkillUpdateController skillUpdates;
    readonly Action save,appearance;
    readonly Action modeChanged;
    readonly TextBlock status=Text("正在连接",12,true),plan=Text("Codex Quota",13),updated=Text("尚未刷新",11);
    readonly TextBlock countdown=Text("--",25),resetTime=Text("等待服务器提供重置时间",12,true);
    readonly TextBlock credits=Text("--",26),creditExpiry=Text("服务端未提供到期时间",11,true);
    readonly TextBlock details=Text("",11,true);
    readonly StackPanel extra=new(),preferences=new(),diagnostics=new();
    readonly Ring primary=new(){Width=160,Height=160},weekly=new(){Width=160,Height=160};
    readonly TextBlock primaryHealth=Text("未提供",11,true),weeklyHealth=Text("未提供",11,true);
    readonly TextBlock primaryReset=Text("--",11,true),weeklyReset=Text("--",11,true);
    readonly DispatcherTimer clock=new(){Interval=TimeSpan.FromSeconds(1)};
    readonly Button refresh=IconButton("\uE72C","手动刷新");
    readonly Button settingsToggle=IconButton("\uE713","外观设置");
    readonly Button smallWindow=IconButton("\uE9A6","小窗显示");
    readonly Button hideToTray=IconButton("\uE8BB","隐藏到托盘");
    readonly Button resetCredit=new(){Content="使用一次重置机会",MinWidth=152};
    readonly Button checkUpdates=new(){Name="UnifiedUpdateCheckAction",Content="检查更新",MinWidth=98,Margin=new Thickness(8,0,0,0)};
    readonly TextBlock updateStatus=Text("当前 v"+ReleaseUpdates.CurrentLabel,11,true);
    readonly ResetCreditBadge creditBadge=new(true);
    readonly List<Action> updateChoices=new();
    Border mainSurface=null!;
    readonly ControlCenterPanel? controlPanel;
    readonly IAutomationUiController? automation;
    AutomationSettingsPanel? automationPanel;
    StartupBehaviorPanel? startupPanel;
    SkillUpdatePanel? skillUpdatePanel;
    readonly ReliabilityPanel? reliabilityPanel;
    Expander? advancedWorkflow,localTasksSection,automationSection,accountSection,additionalQuotas;
    ScrollViewer? quotaPage,workflowPage,skillsPage,settingsPage;
    Button? quotaTab,workflowTab,skillsTab;
    readonly TextBlock footerHint=Text("",11,true);
    string currentPage="quota",previousPage="quota";
    QuotaSnapshot? displayed;
    bool positionReady,hideInProgress,resetDialogOpen;
    long lastDisplayedSuccess=-1;
    [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    public StatusWindow(QuotaMonitor monitor,Func<Task> diagnose,Action save,Action appearance,Action modeChanged,ControlCenterService? center=null,IAutomationUiController? automation=null,IReliabilityUiController? reliability=null,IWorkflowLoopUiController? workflow=null,Func<Task<UnifiedUpdateCheck>>? updateCheck=null)
    {
        this.monitor=monitor;this.save=save;this.appearance=appearance;this.modeChanged=modeChanged;this.automation=automation;
        skillUpdates=updateCheck is null?SkillUpdateController.Shared:new SkillUpdateController(check:_=>updateCheck());
        Title="Codex AI 控制中心";Width=Math.Clamp(monitor.Settings.Width,520,760);Height=Math.Clamp(monitor.Settings.Height,700,1000);
        MinWidth=480;MinHeight=580;ShowInTaskbar=false;ShowActivated=false;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.CanResizeWithGrip;
        SetResourceReference(BackgroundProperty,"Glass");SetResourceReference(ForegroundProperty,"Ink");
        WindowChrome.SetWindowChrome(this,new WindowChrome{CaptionHeight=56,ResizeBorderThickness=new Thickness(5),GlassFrameThickness=new Thickness(-1),UseAeroCaptionButtons=false,CornerRadius=new CornerRadius(12)});
        mainSurface=new Border{BorderThickness=new Thickness(1),Padding=new Thickness(24,18,24,16),CornerRadius=new CornerRadius(12)};
        mainSurface.SetResourceReference(Border.BorderBrushProperty,"Line");Content=mainSurface;
        var root=new Grid();root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new());root.RowDefinitions.Add(new(){Height=GridLength.Auto});mainSurface.Child=root;
        var header=new Grid();header.ColumnDefinitions.Add(new());header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});root.Children.Add(header);
        var title=new StackPanel();title.Children.Add(Text("AI 控制中心",22));title.Children.Add(plan);header.Children.Add(title);
        var tools=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Top};Grid.SetColumn(tools,1);header.Children.Add(tools);
        smallWindow.Style=(Style)Application.Current.FindResource(typeof(Button));smallWindow.Content="胶囊";smallWindow.Width=64;smallWindow.Height=40;
        System.Windows.Automation.AutomationProperties.SetHelpText(smallWindow,"切换为桌面额度胶囊；靠上、左、右边缘时自动收起");
        foreach(var b in new[]{settingsToggle,smallWindow,hideToTray}){tools.Children.Add(b);WindowChrome.SetIsHitTestVisibleInChrome(b,true);}
        smallWindow.Click+=(_,_)=>ShowSmallWindow();
        hideToTray.Click+=(_,_)=>HidePanel();settingsToggle.Click+=(_,_)=>SelectPage(currentPage=="settings"?previousPage:"settings");

        var navigation=new Grid{Margin=new Thickness(0,20,0,16)};
        for(var i=0;i<3;i++)navigation.ColumnDefinitions.Add(new());
        quotaTab=NavigationButton("额度","QuotaPageTab",0);workflowTab=NavigationButton("循环任务","LoopPageTab",1);skillsTab=NavigationButton("Skills","SkillsPageTab",2);
        navigation.Children.Add(quotaTab);navigation.Children.Add(workflowTab);navigation.Children.Add(skillsTab);
        quotaTab.Click+=(_,_)=>SelectPage("quota");workflowTab.Click+=(_,_)=>SelectPage("workflow");skillsTab.Click+=(_,_)=>SelectPage("skills");
        Grid.SetRow(navigation,1);root.Children.Add(navigation);

        var body=new StackPanel();quotaPage=Page(body,"QuotaPage");Grid.SetRow(quotaPage,2);root.Children.Add(quotaPage);
        var quotaCard=new StackPanel();quotaCard.Children.Add(Text("剩余额度",17));quotaCard.Children.Add(status);
        var rings=new Grid{Margin=new Thickness(0,12,0,4)};rings.ColumnDefinitions.Add(new());rings.ColumnDefinitions.Add(new());quotaCard.Children.Add(rings);
        rings.Children.Add(QuotaColumn("5 小时",primary,primaryHealth,primaryReset));var weekColumn=QuotaColumn("每周",weekly,weeklyHealth,weeklyReset);Grid.SetColumn(weekColumn,1);rings.Children.Add(weekColumn);
        body.Children.Add(Card(quotaCard));
        var next=new Grid();next.ColumnDefinitions.Add(new());next.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var nextText=new StackPanel();nextText.Children.Add(Text("下次额度重置",13));nextText.Children.Add(resetTime);next.Children.Add(nextText);
        countdown.FontSize=24;countdown.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(countdown,1);next.Children.Add(countdown);body.Children.Add(Card(next));
        var creditBody=new StackPanel();var creditRow=new Grid();creditRow.ColumnDefinitions.Add(new());creditRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var creditCopy=new StackPanel();creditCopy.Children.Add(Text("额度重置机会",14));creditCopy.Children.Add(creditExpiry);creditRow.Children.Add(creditCopy);Grid.SetColumn(credits,1);creditRow.Children.Add(credits);creditBody.Children.Add(creditRow);
        resetCredit.HorizontalAlignment=HorizontalAlignment.Left;resetCredit.MinHeight=40;resetCredit.Margin=new Thickness(0,12,0,0);
        resetCredit.SetResourceReference(Button.BorderBrushProperty,"Accent");System.Windows.Automation.AutomationProperties.SetName(resetCredit,"使用一次官方额度重置机会");
        resetCredit.Click+=(_,_)=>BeginResetCreditUse();creditBody.Children.Add(resetCredit);body.Children.Add(Card(creditBody));
        additionalQuotas=Section("其他额度",extra,"AdditionalQuotaSection");body.Children.Add(additionalQuotas);

        var workflowBody=new StackPanel();
        if(workflow is not null)workflowBody.Children.Add(new WorkflowLoopPanel(workflow));
        else{workflowBody.Children.Add(Text("循环任务",20));workflowBody.Children.Add(Text("工作流连接暂不可用，请从设置查看连接与恢复信息。",13,true));}
        workflowPage=Page(workflowBody,"LoopPage");Grid.SetRow(workflowPage,2);root.Children.Add(workflowPage);
        var skillsBody=new StackPanel();skillsBody.Children.Add(Text("Skills",20));skillsBody.Children.Add(Text("让已安装的技能保持更新。",13,true));
        skillUpdatePanel=new SkillUpdatePanel(()=>monitor.Settings.EnableAnimations,skillUpdates);skillsBody.Children.Add(skillUpdatePanel);
        skillsBody.Children.Add(Text("检查结果会显示来源与版本；只安装你勾选的 Skill。系统和本地维护项目保持原样。",13,true));
        skillsPage=Page(skillsBody,"SkillsPage");Grid.SetRow(skillsPage,2);root.Children.Add(skillsPage);

        if(center is not null)controlPanel=new ControlCenterPanel(center);
        if(reliability is not null)reliabilityPanel=new ReliabilityPanel(reliability);
        BuildPreferences();
        diagnostics.Children.Add(details);diagnostics.Visibility=Visibility.Collapsed;
        var diagnosisBody=new StackPanel();
        var diagnostic=new Button{Content="检查额度连接",MinHeight=40,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,12)};
        diagnostic.Click+=async(_,_)=>await diagnose();diagnosisBody.Children.Add(diagnostic);diagnosisBody.Children.Add(diagnostics);
        if(reliabilityPanel is not null)diagnosisBody.Children.Add(reliabilityPanel);
        advancedWorkflow=Section("连接诊断与恢复",diagnosisBody,"DiagnosticsSettingsSection");preferences.Children.Add(advancedWorkflow);
        if(controlPanel is not null){localTasksSection=Section("本地任务清单与额度保护",controlPanel,"LocalTasksSettingsSection");preferences.Children.Add(localTasksSection);}
        var about=new StackPanel();about.Children.Add(updateStatus);
        var updateActions=new WrapPanel{Margin=new Thickness(0,10,0,0)};checkUpdates.Content="检查更新";checkUpdates.Margin=new Thickness(0,0,8,0);checkUpdates.MinHeight=40;
        updateActions.Children.Add(checkUpdates);about.Children.Add(updateActions);preferences.Children.Add(Section("关于与版本更新",about,"AboutSettingsSection"));
        settingsPage=Page(preferences,"SettingsPage");Grid.SetRow(settingsPage,2);root.Children.Add(settingsPage);
        var footer=new Grid{Margin=new Thickness(0,12,0,0)};footer.ColumnDefinitions.Add(new());footer.ColumnDefinitions.Add(new(){Width=GridLength.Auto});Grid.SetRow(footer,3);root.Children.Add(footer);
        footerHint.VerticalAlignment=VerticalAlignment.Center;footer.Children.Add(footerHint);Grid.SetColumn(refresh,1);footer.Children.Add(refresh);
        System.Windows.Automation.AutomationProperties.SetName(checkUpdates,"检查小组件和已安装 Skills 的更新");
        checkUpdates.Click+=async(_,_)=>await CheckForUpdatesAsync();
        refresh.Click+=async(_,_)=>{AnimationHelper.RotateOnce(refresh);await monitor.RefreshAsync();};
        SelectPage("quota");
        Closing+=(_,e)=>{if(!Application.Current.Dispatcher.HasShutdownStarted){e.Cancel=true;HidePanel();}};
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){if(currentPage=="settings")SelectPage(previousPage);else HidePanel();e.Handled=true;}else if(Keyboard.Modifiers==ModifierKeys.Control && e.Key is Key.D1 or Key.D2 or Key.D3){SelectPage(e.Key==Key.D1?"quota":e.Key==Key.D2?"workflow":"skills");e.Handled=true;}};
        // The full control-center window stays visible when focus moves elsewhere.
        // Hiding is an explicit tray/close action; the former pin toggle is now the small-window button.
        IsVisibleChanged+=(_,_)=>{if(IsVisible)clock.Start();else{clock.Stop();SavePosition();}};
        clock.Tick+=(_,_)=>RefreshTimes();
        SourceInitialized+=(_,_)=>{ApplyMaterial();PositionWindow();};
        AnimationHelper.AttachButtonHover(this);
        RefreshView();
    }
    static TextBlock Text(string text,double size,bool muted=false)
    {
        var block=new TextBlock{Text=text,FontSize=size,FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,2,0,2)};
        block.SetResourceReference(TextBlock.ForegroundProperty,muted?"Muted":"Ink");return block;
    }
    static Button IconButton(string glyph,string name)
    {
        var button=new Button{Content=glyph,Style=(Style)Application.Current.FindResource("Icon"),Margin=new Thickness(4,0,0,0)};
        System.Windows.Automation.AutomationProperties.SetName(button,name);return button;
    }
    static Border Divider(){var b=new Border{Height=1};b.SetResourceReference(Border.BackgroundProperty,"Line");return b;}
    static Border Card(UIElement content)
    {
        var card=new Border{Child=content,Padding=new Thickness(16),Margin=new Thickness(0,0,0,12),CornerRadius=new CornerRadius(12),BorderThickness=new Thickness(1)};
        card.SetResourceReference(Border.BackgroundProperty,"Surface");card.SetResourceReference(Border.BorderBrushProperty,"Line");return card;
    }
    static ScrollViewer Page(UIElement content,string name)=>new(){Name=name,Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Visibility=Visibility.Collapsed,Padding=new Thickness(0,0,4,0)};
    static Button NavigationButton(string text,string name,int column)
    {
        var button=new Button{Name=name,Content=text,MinHeight=44,FontSize=14,Margin=new Thickness(column==0?0:4,0,column==2?0:4,0)};
        System.Windows.Automation.AutomationProperties.SetName(button,text+"页");Grid.SetColumn(button,column);return button;
    }
    static Expander Section(string heading,UIElement content,string name)=>DisclosureSection.Create(heading,content,name);
    static StackPanel QuotaColumn(string label,Ring ring,TextBlock health,TextBlock reset)
    {
        var p=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center};var heading=Text(label,15);heading.TextAlignment=TextAlignment.Center;p.Children.Add(heading);
        var frame=new Grid{Height=172,Width=172};frame.Children.Add(ring);ring.VerticalAlignment=VerticalAlignment.Center;ring.HorizontalAlignment=HorizontalAlignment.Center;p.Children.Add(frame);
        health.TextAlignment=TextAlignment.Center;reset.TextAlignment=TextAlignment.Center;p.Children.Add(health);p.Children.Add(reset);return p;
    }
    void BuildPreferences()
    {
        preferences.Children.Add(Text("设置",20));preferences.Children.Add(Text("外观、启动和高级选项集中在这里。",13,true));
        var visual=new StackPanel();
        AddChoices(visual,"主题",new[]{("Dark","深色"),("Light","浅色"),("System","跟随系统")},()=>monitor.Settings.Theme,value=>{monitor.Settings.Theme=value;appearance();ApplyMaterial();save();});
        AddChoices(visual,"文字颜色",new[]{("Auto","自动"),("Contrast","高对比"),("Accent","强调色")},()=>monitor.Settings.TextColor,value=>{monitor.Settings.TextColor=value;appearance();save();});
        AddChoices(visual,"强调色",new[]{("Mint","薄荷绿"),("Blue","晴空蓝"),("Violet","紫罗兰")},()=>monitor.Settings.Accent,value=>{monitor.Settings.Accent=value;appearance();save();RefreshView();});
        AddChoices(visual,"显示方式",new[]{("Full","完整窗口"),("Capsule","额度胶囊")},()=>monitor.Settings.WindowMode,value=>{if(monitor.Settings.WindowMode==value)return;monitor.Settings.WindowMode=value;save();modeChanged();});
        var dwellLabel=Text($"鼠标离开后收起：{monitor.Settings.CapsuleStaySeconds:0.#} 秒",12,true);visual.Children.Add(dwellLabel);
        var dwell=new Slider{Minimum=.5,Maximum=30,TickFrequency=.5,SmallChange=.5,LargeChange=5,IsSnapToTickEnabled=true,Value=monitor.Settings.CapsuleStaySeconds,Margin=new Thickness(0,8,0,14)};
        dwell.SetResourceReference(Slider.ForegroundProperty,"Accent");System.Windows.Automation.AutomationProperties.SetName(dwell,"胶囊鼠标离开后收起延迟，单位秒");
        dwell.ValueChanged+=(_,_)=>{monitor.Settings.CapsuleStaySeconds=dwell.Value;dwellLabel.Text=$"鼠标离开后收起：{dwell.Value:0.#} 秒";};
        dwell.PreviewMouseLeftButtonUp+=(_,_)=>save();dwell.PreviewKeyUp+=(_,_)=>save();dwell.LostKeyboardFocus+=(_,_)=>save();visual.Children.Add(dwell);
        var animations=new CheckBox{Content="启用界面动画",IsChecked=monitor.Settings.EnableAnimations,MinHeight=36};System.Windows.Automation.AutomationProperties.SetName(animations,"启用轻量界面动画");
        animations.Checked+=(_,_)=>SetAnimations(true);animations.Unchecked+=(_,_)=>SetAnimations(false);visual.Children.Add(animations);
        preferences.Children.Add(Section("外观与胶囊",visual,"AppearanceSettingsSection"));
        var startup=new StackPanel();startupPanel=new StartupBehaviorPanel(monitor.Settings,save);startup.Children.Add(startupPanel);
        AddChoices(startup,"额度刷新间隔",new[]{("60","1 分钟"),("120","2 分钟"),("300","5 分钟")},()=>monitor.Settings.RefreshSeconds.ToString(),value=>{monitor.Settings.RefreshSeconds=int.Parse(value);save();monitor.SettingsChanged();});
        var follow=new CheckBox{Content="仅在 Codex 运行时刷新额度",IsChecked=monitor.Settings.FollowCodex,MinHeight=36};
        System.Windows.Automation.AutomationProperties.SetName(follow,"跟随 Codex 运行状态");
        follow.Checked+=(_,_)=>{monitor.Settings.FollowCodex=true;save();monitor.SettingsChanged();};follow.Unchecked+=(_,_)=>{monitor.Settings.FollowCodex=false;save();monitor.SettingsChanged();};startup.Children.Add(follow);
        preferences.Children.Add(Section("启动与刷新",startup,"StartupSettingsSection"));
        automationPanel=new AutomationSettingsPanel(automation);automationSection=Section("自动收尾计划",automationPanel,"AutomationSettingsSection");preferences.Children.Add(automationSection);
        var account=new StackPanel();account.Children.Add(Text("额度使用小组件独立账户；循环任务的 GPT / Codex 连接在循环任务页设置。",12,true));
        var locate=new Button{Content="定位官方 codex.exe",HorizontalAlignment=HorizontalAlignment.Left,MinHeight=40,Margin=new Thickness(0,8,0,12)};
        locate.Click+=(_,_)=>{var picker=new Microsoft.Win32.OpenFileDialog{Filter="Codex CLI|codex.exe",Title="选择官方 Codex CLI"};if(picker.ShowDialog(this)==true){monitor.Settings.ManualCli=picker.FileName;save();monitor.SettingsChanged();}};account.Children.Add(locate);
        var login=new Button{Content="登录 / 重新绑定额度账户",HorizontalAlignment=HorizontalAlignment.Left,MinHeight=40};
        login.Click+=async(_,_)=>{if(ShowOwnedMessage("将通过官方浏览器登录为本小组件创建独立认证，不会读取或修改 Codex Desktop 的认证。继续吗？",MessageBoxButton.OKCancel,MessageBoxImage.Information)!=MessageBoxResult.OK)return;login.IsEnabled=false;try{await monitor.LoginAsync();}finally{login.IsEnabled=true;RefreshView();}};account.Children.Add(login);
        accountSection=Section("额度账户与连接",account,"AccountSettingsSection");preferences.Children.Add(accountSection);
    }
    void AddChoices(Panel target,string title,(string Key,string Label)[] options,Func<string> selected,Action<string> choose)
    {
        target.Children.Add(Text(title,12,true));
        var row=new WrapPanel{Margin=new Thickness(0,6,0,12)};target.Children.Add(row);
        var buttons=new List<(string Key,Button Button)>();
        foreach(var (key,label) in options)
        {
            var button=new Button{Content=label,MinWidth=98,MinHeight=40,Margin=new Thickness(0,0,8,8)};
            System.Windows.Automation.AutomationProperties.SetName(button,title+"："+label);
            buttons.Add((key,button));row.Children.Add(button);
            button.Click+=(_,_)=>{choose(key);Paint();};
        }
        void Paint()
        {
            foreach(var (key,button) in buttons)
            {
                bool active=selected()==key;
                button.BorderThickness=new Thickness(active?2:1);
                button.FontWeight=active?FontWeights.SemiBold:FontWeights.Normal;
                button.SetResourceReference(Button.BorderBrushProperty,active?"Accent":"Line");
            }
        }
        updateChoices.Add(Paint);Paint();
    }
    void SetAnimations(bool enabled)
    {
        monitor.Settings.EnableAnimations=enabled;AnimationHelper.Enabled=enabled;
        skillUpdatePanel?.Refresh();
        primary.EnableAnimations=enabled;weekly.EnableAnimations=enabled;creditBadge.EnableAnimations=enabled;
        save();
    }
    async Task CheckForUpdatesAsync()
    {
        if(!checkUpdates.IsEnabled)return;
        checkUpdates.IsEnabled=false;
        updateStatus.Text="正在检查小组件和 Skills；不会安装更新…";
        try
        {
            await skillUpdates.CheckAsync();
            updateStatus.Text=skillUpdates.CheckResult?.Summary ?? skillUpdates.State.Summary;
            SelectPage("skills");
        }
        catch(Exception){updateStatus.Text="检查失败，请稍后重试";}
        finally{checkUpdates.IsEnabled=true;}
    }
    public void ApplyMaterial()
    {
        var handle=new WindowInteropHelper(this).Handle;if(handle==IntPtr.Zero)return;
        int backdrop=3,dark=((App)Application.Current).IsDark?1:0,rounded=2;
        DwmSetWindowAttribute(handle,38,ref backdrop,4);DwmSetWindowAttribute(handle,20,ref dark,4);DwmSetWindowAttribute(handle,33,ref rounded,4);
        primary.InvalidateVisual();weekly.InvalidateVisual();
    }
    void PositionWindow()
    {
        var area=SystemParameters.WorkArea;
        Left=Math.Clamp(monitor.Settings.Left??area.Right-Width-14,area.Left,Math.Max(area.Left,area.Right-Width));
        Top=Math.Clamp(monitor.Settings.Top??area.Bottom-Height-14,area.Top,Math.Max(area.Top,area.Bottom-Height));positionReady=true;
    }
    public void SavePosition()
    {
        if(!positionReady)return;var s=monitor.Settings;
        if(s.Left==Left && s.Top==Top && s.Width==Width && s.Height==Height)return;
        s.Left=Left;s.Top=Top;s.Width=Width;s.Height=Height;save();
    }
    public void ShowPanel()
    {
        hideInProgress=false;RefreshView();
        if(!IsVisible){Show();AnimationHelper.Reveal(mainSurface,AnimationDurations.WindowShow);}
        ApplyMaterial();
    }
    public void HidePanel(Action? completed=null)
    {
        if(!IsVisible){completed?.Invoke();return;}if(hideInProgress)return;hideInProgress=true;
        AnimationHelper.Conceal(mainSurface,()=>{if(!hideInProgress)return;hideInProgress=false;Hide();mainSurface.Opacity=1;completed?.Invoke();},AnimationDurations.WindowHide);
    }
    public void TogglePanel(){if(IsVisible)HidePanel();else ShowPanel();}
    void ShowSmallWindow(){monitor.Settings.WindowMode="Capsule";save();modeChanged();}
    public void ShowDiagnostics(){SelectPage("settings");if(advancedWorkflow is not null)advancedWorkflow.IsExpanded=true;diagnostics.Visibility=Visibility.Visible;ShowPanel();diagnostics.BringIntoView();}
    void SetWorkflowPage(bool show)=>SelectPage(show?"workflow":"quota");
    void SelectPage(string page)
    {
        if(page=="settings" && currentPage!="settings")previousPage=currentPage;
        bool changed=currentPage!=page;currentPage=page;
        foreach(var (name,view,button) in new (string,ScrollViewer?,Button?)[]{("quota",quotaPage,quotaTab),("workflow",workflowPage,workflowTab),("skills",skillsPage,skillsTab),("settings",settingsPage,settingsToggle)})
        {
            var active=name==page;if(view is not null)view.Visibility=active?Visibility.Visible:Visibility.Collapsed;
            if(button is not null){button.SetResourceReference(Button.BorderBrushProperty,active?"Accent":"Line");button.SetResourceReference(Button.BackgroundProperty,active?"Hover":"Surface");button.FontWeight=active?FontWeights.SemiBold:FontWeights.Normal;System.Windows.Automation.AutomationProperties.SetItemStatus(button,active?"当前页面":"");}
            if(changed && active && view is not null && IsVisible)AnimationHelper.Reveal(view,TimeSpan.FromMilliseconds(160));
        }
        preferences.Visibility=page=="settings"?Visibility.Visible:Visibility.Collapsed;
        refresh.Visibility=page=="quota"?Visibility.Visible:Visibility.Collapsed;
        if(page=="settings"){startupPanel?.RefreshState();controlPanel?.RefreshView();reliabilityPanel?.RefreshState();}
        if(page=="skills")skillUpdatePanel?.Refresh();
        RefreshFooter();
    }
    void RefreshFooter()=>footerHint.Text=currentPage switch{"quota"=>updated.Text,"workflow"=>"任务进度自动保存 · 连接异常时安全暂停","skills"=>"仅更新已安装且来源明确的技能",_=>"设置即时保存 · Esc 返回"};
    public void ShowWorkflow(){SetWorkflowPage(true);ShowPanel();}
    public void ShowSkills(){SelectPage("skills");ShowPanel();}
    public void ShowSettings(){SelectPage("settings");ShowPanel();}
    public void ShowReliability(){SelectPage("settings");if(advancedWorkflow is not null)advancedWorkflow.IsExpanded=true;ShowPanel();advancedWorkflow?.BringIntoView();}
    public void ShowStatusDiagnostics(){ShowReliability();reliabilityPanel?.ShowStateDiagnostics();}
    internal MessageBoxResult ShowOwnedMessage(string message,MessageBoxButton buttons=MessageBoxButton.OK,MessageBoxImage image=MessageBoxImage.Information)
    {
        bool previous=resetDialogOpen;resetDialogOpen=true;
        try{return MessageBox.Show(this,message,"AI 控制中心",buttons,image);}
        finally{resetDialogOpen=previous;}
    }
    internal ControlCenterPanel? ControlPanelForLayout=>controlPanel;
    internal AutomationSettingsPanel? AutomationPanelForLayout=>automationPanel;
    internal Button SettingsButtonForLayout=>settingsToggle;
    internal Button SmallWindowButtonForLayout=>smallWindow;
    internal Button HideButtonForLayout=>hideToTray;
    internal bool PreferencesVisibleForLayout=>currentPage=="settings";
    internal string CurrentPageForLayout=>currentPage;
    internal void ShowSkillsForLayout()=>SelectPage("skills");
    internal void ShowQuotaForLayout()=>SelectPage("quota");
    internal void ShowWorkflowForLayout()=>SetWorkflowPage(true);
    internal void ShowAdvancedWorkflowForLayout(){SelectPage("settings");if(localTasksSection is not null)localTasksSection.IsExpanded=true;UpdateLayout();localTasksSection?.BringIntoView();}
    internal void ShowPreferencesForLayout(){SelectPage("settings");UpdateLayout();}
    internal void ShowAutomationForLayout(){SelectPage("settings");if(automationSection is not null)automationSection.IsExpanded=true;UpdateLayout();automationSection?.BringIntoView();}
    public void BeginResetCreditUse()=>_ = UseResetCreditAsync();
    async Task UseResetCreditAsync()
    {
        if(resetDialogOpen)return;
        resetDialogOpen=true;
        try
        {
            await monitor.RefreshAsync();
            RefreshView();
            if(!monitor.CanConsumeResetCredit)
            {
                MessageBox.Show(this,monitor.ResetResultUnknown?"上次重置请求的结果不明确。请先核对账户额度，本次运行不会再次提交。":"当前没有可用的重置机会，或额度数据尚未成功刷新。", "Codex Quota Widget",MessageBoxButton.OK,MessageBoxImage.Information);
                return;
            }
            var count=monitor.Snapshot!.AvailableCount!.Value;
            var confirmation=$"当前有 {count} 次可用重置机会。\n\n确定使用其中 1 次重置符合条件的 Codex 额度吗？此操作不能撤销。";
            if(MessageBox.Show(this,confirmation,"确认使用重置机会",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
            var result=await monitor.ConsumeResetCreditAsync();
            RefreshView();
            var message=result.Outcome switch
            {
                ResetCreditOutcome.Reset=>result.QuotaRefreshed?"已使用一次重置机会，最新额度已读取。":"重置已完成，但最新额度暂时读取失败。请稍后手动刷新。",
                ResetCreditOutcome.AlreadyRedeemed=>"这次请求此前已完成。已重新读取额度。",
                ResetCreditOutcome.NothingToReset=>"当前没有符合条件的额度窗口，本次未使用重置机会。",
                ResetCreditOutcome.NoCredit=>"服务端报告没有可用重置机会，本次未执行重置。",
                ResetCreditOutcome.Busy=>"正在刷新额度，请稍后再试。",
                ResetCreditOutcome.Unknown=>"服务端没有明确返回重置结果。本次运行已禁止再次提交，请核对账户额度后重启小组件。",
                _=>"当前无法执行重置，请检查 Codex、网络和登录状态。"
            };
            MessageBox.Show(this,message,"Codex Quota Widget",MessageBoxButton.OK,result.Outcome==ResetCreditOutcome.Unknown?MessageBoxImage.Warning:MessageBoxImage.Information);
        }
        catch(Exception)
        {
            MessageBox.Show(this,"重置流程遇到错误。请刷新额度并检查独立账户状态。","Codex Quota Widget",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        finally{resetDialogOpen=false;RefreshView();}
    }
    public void StopTimers(){clock.Stop();startupPanel?.Stop();controlPanel?.Dispose();automationPanel?.Dispose();reliabilityPanel?.Dispose();AnimationHelper.StopAnimations(mainSurface);foreach(var page in new[]{quotaPage,workflowPage,skillsPage,settingsPage})if(page is not null)AnimationHelper.StopAnimations(page);primary.BeginAnimation(Ring.AnimatedValueProperty,null);primary.BeginAnimation(Ring.PulseOpacityProperty,null);weekly.BeginAnimation(Ring.AnimatedValueProperty,null);weekly.BeginAnimation(Ring.PulseOpacityProperty,null);SavePosition();}
    public void RefreshView()
    {
        controlPanel?.RefreshView();
        reliabilityPanel?.RefreshState();
        automationPanel?.RefreshState();
        status.Text=monitor.Status switch{
            MonitorStatus.Live=>monitor.IsStale?"数据已过期":"●  Codex 运行中 · 数据已更新",
            MonitorStatus.DesktopClosed=>"○  Codex 未运行 · 数据已过期",MonitorStatus.Starting=>"正在连接 Codex",
            MonitorStatus.NeedsLogin=>"!  认证失败或未登录 · 数据已过期",MonitorStatus.Offline=>"!  网络不可用 · 数据已过期",
            MonitorStatus.Sleeping=>"已暂停 · 数据已过期",MonitorStatus.ProtocolError=>"!  接口不兼容 · 数据已过期",
            MonitorStatus.CliMissing=>"!  未找到官方 CLI，请在设置中定位",MonitorStatus.Stopped=>"已停止",_=>"!  读取失败，等待重试 · 数据已过期"};
        status.SetResourceReference(TextBlock.ForegroundProperty,monitor.Status==MonitorStatus.Live&&!monitor.IsStale?"Accent":"Muted");
        var q=monitor.Snapshot;var data=QuotaPresentation.From(monitor);plan.Text=q is null?"独立账户 · 等待额度":"ChatGPT "+q.Plan+" · 独立账户";
        var first=data.FiveHour;var week=data.Weekly;
        var resetAt=q is null?DateTimeOffset.MinValue:q.UpdatedAt;
        if(displayed is not null && q is not null && first is not null && data.FiveHour is not null && QuotaPresentation.IsReset(displayed.Windows.Find(w=>w.LimitId=="codex"&&w.WindowDurationMins==300),first,resetAt))primary.PulseGlow();
        if(displayed is not null && q is not null && week is not null && QuotaPresentation.IsReset(displayed.Windows.Find(w=>w.LimitId=="codex"&&w.WindowDurationMins==10080),week,resetAt))weekly.PulseGlow();
        primary.EnableAnimations=monitor.Settings.EnableAnimations;weekly.EnableAnimations=monitor.Settings.EnableAnimations;creditBadge.EnableAnimations=monitor.Settings.EnableAnimations;
        AnimationHelper.Enabled=monitor.Settings.EnableAnimations;
        primary.Value=first?.Remaining;weekly.Value=week?.Remaining;primary.SetResourceReference(Ring.AccentProperty,"Accent");weekly.SetResourceReference(Ring.AccentProperty,"Accent");
        System.Windows.Automation.AutomationProperties.SetName(primary,$"5 小时剩余 {first?.Remaining?.ToString("0.#")??"未提供"}%");
        System.Windows.Automation.AutomationProperties.SetName(weekly,$"每周剩余 {week?.Remaining?.ToString("0.#")??"未提供"}%");
        primaryHealth.Text=Ring.Health(first?.Remaining);weeklyHealth.Text=Ring.Health(week?.Remaining);
        primaryReset.Text=ShortReset(first?.ResetsAt);weeklyReset.Text=ShortReset(week?.ResetsAt);
        System.Windows.Automation.AutomationProperties.SetHelpText(primary,first?.ResetsAt is {} f?HeadlessCommand.Local(f):"服务端未提供重置时间");
        System.Windows.Automation.AutomationProperties.SetHelpText(weekly,week?.ResetsAt is {} w?HeadlessCommand.Local(w):"服务端未提供重置时间");
        credits.Text=q?.AvailableCount is {} n?$"{n} 次":"--";creditBadge.AvailableCount=data.AvailableCount;
        creditExpiry.Text=q?.CreditExpirations.Count>0?string.Join("\n",q.CreditExpirations.Select(t=>"到期 "+HeadlessCommand.Local(t))):"服务端未提供到期时间";
        if(!ReferenceEquals(displayed,q))
        {
            displayed=q;extra.Children.Clear();
            foreach(var item in q?.Windows.Where(x=>x!=first&&x!=week)??Enumerable.Empty<QuotaWindow>())
            {
                var row=new StackPanel{Margin=new Thickness(0,0,0,14)};
                var label=item.LimitId=="base_model_inference"||item.Name.Contains("reserve",StringComparison.OrdinalIgnoreCase)?"GPT reserve":item.Name+" · "+item.Slot;
                row.Children.Add(Text(label+"     "+(item.Remaining is {} r?$"{r:0.#}% 剩余":"未提供"),14));
                row.Children.Add(Text(item.ResetsAt is {} t?"重置 "+HeadlessCommand.Local(t):"服务端未提供重置时间",11,true));extra.Children.Add(row);
            }
        }
        if(additionalQuotas is not null)additionalQuotas.Visibility=extra.Children.Count>0?Visibility.Visible:Visibility.Collapsed;
        if(monitor.SuccessCount>lastDisplayedSuccess)
        {
            if(lastDisplayedSuccess>=0)AnimationHelper.Flash(status);
            lastDisplayedSuccess=monitor.SuccessCount;
        }
        refresh.IsEnabled=!monitor.IsBusy;refresh.Opacity=monitor.IsBusy?.5:1;
        resetCredit.IsEnabled=monitor.CanConsumeResetCredit&&!resetDialogOpen;
        System.Windows.Automation.AutomationProperties.SetHelpText(resetCredit,monitor.ResetResultUnknown?"上次请求结果不明确，请核对额度后重启小组件":monitor.Snapshot?.AvailableCount is >0?"使用一次官方重置机会，操作前会再次确认":"没有可用重置机会或额度尚未刷新");
        foreach(var updateChoice in updateChoices)updateChoice();
        details.Text=$"CLI  {monitor.CliVersion}\n{monitor.CliPath}\nCODEX_HOME  {Paths.Home}\nApp Server  {monitor.ServerVersion}\nPID  {monitor.ServerPid?.ToString()??"无"} · 成功 {monitor.SuccessCount} · 请求 {monitor.AttemptCount}\n手动 {monitor.ManualRefreshCount} · 自动 {monitor.AutomaticRefreshCount}\n退出码 {monitor.LastServerExitCode?.ToString()??"待退出"} · 残留 {monitor.LastResidualProcesses?.ToString()??"待检查"}";
        RefreshTimes();
    }
    static string ShortReset(long? time)=>time is {} t?DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime().ToString("MM-dd HH:mm 重置"):"重置时间未提供";
    void RefreshTimes()
    {
        var q=monitor.Snapshot;updated.Text=q is null?"尚无成功读数":$"{(monitor.IsStale?"旧数据":"已更新")} {q.UpdatedAt.ToLocalTime():HH:mm:ss} · {monitor.Settings.RefreshSeconds} 秒刷新";
        var next=q?.Windows.Where(w=>w.ResetsAt.HasValue).Select(w=>w.ResetsAt!.Value).Order().FirstOrDefault();
        RefreshFooter();
        if(next is not >0){countdown.Text="--";resetTime.Text="等待有效额度数据";return;}
        var span=DateTimeOffset.FromUnixTimeSeconds(next.Value)-DateTimeOffset.UtcNow;
        countdown.Text=span<=TimeSpan.Zero?"等待额度刷新":$"{(span.Days>0?$"{span.Days} 天 ":"")}{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}";
        resetTime.Text=HeadlessCommand.Local(next.Value);
    }
}
