#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishTrimmed=false

// WPF UIA 测试程序:作为 csharp-uia / csharp-screenshot 技能的 WPF 测试目标。
// 运行: dotnet run --file WpfTestApp.cs
using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WpfTestApp;

public class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new Application();
        var win = new MainWindow();
        app.Run(win);
    }
}

public partial class MainWindow : Window
{
    private readonly TextBox txtInput = new() { Width = 220 };
    private readonly TextBlock txtStatus = new() { Text = "就绪" };
    private readonly ListBox lstFruit = new() { Height = 110, Margin = new Thickness(6) };
    private readonly Expander expMore = new() { Margin = new Thickness(6) };
    private readonly TreeView treeMain = new() { Height = 130, Margin = new Thickness(6) };
    private readonly DispatcherTimer timer = new();
    private int addedCount;

    public MainWindow()
    {
        Title = "WPF UIA 测试";
        Width = 900;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Aid(txtInput, "txtInput");
        AutomationProperties.SetName(txtInput, "输入框");
        Aid(txtStatus, "txtStatus");
        Aid(lstFruit, "lstFruit");
        AutomationProperties.SetName(lstFruit, "水果列表");
        Aid(expMore, "expMore");
        AutomationProperties.SetName(expMore, "更多选项");
        Aid(treeMain, "treeMain");
        AutomationProperties.SetName(treeMain, "目录树");

        // 输入区
        var btnOk = MakeButton("确定", "btnOk", OnOk);
        var btnAdd = MakeButton("加一项", "btnAdd", OnAdd);
        var chkReconnect = new CheckBox { Content = "自动重连", Margin = new Thickness(6) };
        Aid(chkReconnect, "chkReconnect");

        var inputPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        inputPanel.Children.Add(new Label { Content = "输入:" });
        inputPanel.Children.Add(txtInput);
        inputPanel.Children.Add(btnOk);
        inputPanel.Children.Add(btnAdd);
        inputPanel.Children.Add(chkReconnect);

        // Tab:基本页 = ListBox;高级页 = Expander
        lstFruit.Items.Add("苹果");
        lstFruit.Items.Add("香蕉");
        lstFruit.Items.Add("橙子");

        expMore.Header = "更多选项";
        expMore.Content = new TextBlock { Text = "展开后的内容", Margin = new Thickness(12, 6, 0, 6) };

        var tabBasic = new TabItem { Header = "基本", Content = lstFruit };
        Aid(tabBasic, "tabBasic");
        var tabAdvanced = new TabItem { Header = "高级", Content = expMore };
        Aid(tabAdvanced, "tabAdvanced");
        var tabs = new TabControl { Margin = new Thickness(6), Height = 260 };
        Aid(tabs, "tabs");
        tabs.Items.Add(tabBasic);
        tabs.Items.Add(tabAdvanced);

        // TreeView
        var root = new TreeViewItem { Header = "文档", IsExpanded = true };
        root.Items.Add(new TreeViewItem { Header = "报告" });
        root.Items.Add(new TreeViewItem { Header = "图片" });
        treeMain.Items.Add(root);

        txtStatus.Margin = new Thickness(6);
        txtStatus.FontSize = 14;
        txtStatus.Foreground = Brushes.DimGray;

        var rootPanel = new StackPanel();
        rootPanel.Children.Add(inputPanel);
        rootPanel.Children.Add(tabs);
        rootPanel.Children.Add(treeMain);
        rootPanel.Children.Add(txtStatus);
        Content = rootPanel;

        timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            txtStatus.Text = "监听中";
        };
    }

    private static UIElement Aid(UIElement e, string id)
    {
        AutomationProperties.SetAutomationId(e, id);
        return e;
    }

    private static Button MakeButton(string text, string id, RoutedEventHandler onClick)
    {
        var b = new Button { Content = text, Margin = new Thickness(12, 0, 0, 0), Width = 88 };
        Aid(b, id);
        b.Click += onClick;
        return b;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        txtStatus.Text = "处理中";
        timer.Start();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        addedCount++;
        lstFruit.Items.Add($"新项{addedCount}");
        txtStatus.Text = $"已加{addedCount}项";
    }
}
