using System.Windows.Controls;
namespace ExHyperV.Views.Pages;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Management.Automation;
using System.Net;
using System.Net.Mail;
using System.Windows;
using System.Xml.Linq;
using Wpf.Ui.Controls;
using static Microsoft.ApplicationInsights.MetricDimensionNames.TelemetryContext;

public partial class VMNetPage
{
    public bool refreshlock = false;

    public VMNetPage()
    {
        InitializeComponent();
        Task.Run(() => Initialinfo()); //获取宿主虚拟交换机信息
    }

        //虚拟交换机的数据结构
        public class SwitchInfo
        {
            public string SwitchName { get; set; }
            public string SwitchType { get; set; }
            public string AllowManagementOS { get; set; }
            public string Id { get; set; }
            public string NetAdapterInterfaceDescription { get; set; }
            public List<AdapterInfo> Adapters { get; set; }  // 网络适配器列表

            // 构造函数
            public SwitchInfo(string switchName, string switchType, string host, string id, string phydesc,List<AdapterInfo> adapters)
            {
                SwitchName = switchName;
                SwitchType = switchType;
                AllowManagementOS = host;
                Id = id;
                NetAdapterInterfaceDescription = phydesc;
                Adapters = adapters;
        }
        }

    //网络适配器的数据结构
    public class AdapterInfo
    {
        public string VMName { get; set; }
        public string MacAddress { get; set; }
        public string Status { get; set; }
        public string IPAddresses { get; set; }

        // 构造函数
        public AdapterInfo(string vMName, string macAddress, string status, string ipAddresses)
        {
            VMName = vMName;
            MacAddress = macAddress;
            Status = status;
            IPAddresses = ipAddresses;
        }
    }


    private async void Initialinfo(){
        List<SwitchInfo> SwitchList = new List<SwitchInfo>();
        await GetInfo(SwitchList); //获取数据
        Dispatcher.Invoke(() => //更新UI
        {
            ParentPanel.Children.Clear();
            progressRing.Visibility = Visibility.Collapsed; //隐藏加载条
            foreach (var Switch1 in SwitchList) 
            {
                var cardExpander = Utils.CardExpander1();
                cardExpander.Icon = Utils.FontIcon1("Switch", Switch1.SwitchName);
                Grid.SetRow(cardExpander, ParentPanel.RowDefinitions.Count);
                ParentPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 增加新的一行

                var headerGrid = new Grid(); // 创建 header 的 Grid 布局，包含两列，第一列占满剩余空间，第二列根据内容自适应宽度
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var drivername = Utils.TextBlock1(Switch1.SwitchName); //设备名



                Grid.SetColumn(drivername, 0); // 添加到第一列
                headerGrid.Children.Add(drivername);
                cardExpander.Header = headerGrid;

                //交换机参数

                // 详细数据
                var contentPanel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };

                var grid = new Grid();
                // 定义 Grid 的列和行
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

                var textData = new (string text, int row, int column)[]
                {
                                ("名称", 0, 0),
                                ("类型", 1, 0),
                                ("宿主是否连接", 2, 0),
                                ("ID", 3, 0),
                                ("物理网卡", 4, 0),
                                (Switch1.SwitchName, 0, 1),
                                (Switch1.SwitchType, 1, 1),
                                (Switch1.AllowManagementOS, 2, 1),
                                (Switch1.Id, 3, 1),
                                (Switch1.NetAdapterInterfaceDescription, 4, 1),
                };

                foreach (var (text, row, column) in textData)
                {
                    var textBlock = Utils.WriteTextBlock2(text, row, column);
                    grid.Children.Add(textBlock);
                }
                contentPanel.Children.Add(grid);
                cardExpander.Content = contentPanel;
                ParentPanel.Children.Add(cardExpander);
            }




        });
    }

    private async Task GetInfo(List<SwitchInfo> SwitchList)
        {
            try
            { 
                    List<AdapterInfo> VMAdapterList = new List<AdapterInfo>() ;// 存储虚拟适配器
                    Utils.Run("Set-ExecutionPolicy RemoteSigned -Scope Process -Force"); //设置策略

                    //检查是否安装hyperv，没安装就不获取信息。

                    if (Utils.Run("Get-Module -ListAvailable -Name Hyper-V").Count == 0) { return; } 
                    
                    //获取虚拟交换机信息
                    var switchdata = Utils.Run(@"Get-VMSwitch | Select-Object Name, Id, SwitchType, AllowManagementOS, NetAdapterInterfaceDescription");
                    if (switchdata == null) { return; } 

                    foreach (var result in switchdata)
                    {
                        var SwitchName = result.Properties["Name"]?.Value?.ToString();
                        var SwitchType = result.Properties["SwitchType"]?.Value?.ToString();
                        var Host = result.Properties["AllowManagementOS"]?.Value?.ToString();
                        var Id = result.Properties["Id"]?.Value?.ToString();
                        var Phydesc = result.Properties["NetAdapterInterfaceDescription"]?.Value?.ToString();
                        
                        List<AdapterInfo> adapters = new List<AdapterInfo>(); 

                        var AdapterData = Utils.Run($@"Get-VMNetworkAdapter -VMName * | Where-Object {{$_.SwitchName -eq '{SwitchName}'}} | select VMName,MacAddress,Status,IPAddresses");//获取连接此交换机的适配器

                        if (AdapterData != null && AdapterData.Count > 0)
                        {
                            foreach (var Adapter in AdapterData)
                            {
                                var VMName = Adapter.Members["VMName"]?.Value?.ToString();
                                var MacAddress = Adapter.Members["MacAddress"]?.Value?.ToString();
                                var Status = Adapter.Members["Status"]?.Value?.ToString();
                                var IPAddresses = Adapter.Members["IPAddresses"]?.Value?.ToString();
                                adapters.Add(new AdapterInfo(VMName, MacAddress, Status, IPAddresses)); //每个适配器都添加
                            }
                        }
                        SwitchList.Add(new SwitchInfo(SwitchName, SwitchType, Host, Id, Phydesc, adapters));
                    }
                    
        }
        catch(Exception ex){
            System.Windows.MessageBox.Show(ex.ToString());
        }
    }



}
