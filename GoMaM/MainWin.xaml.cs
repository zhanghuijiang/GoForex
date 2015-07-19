using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.IO;
using System.Threading;
using System.Collections.ObjectModel;
using MAM;
//using MT4;
using MT4ServerAPI;
using GoMaM.Properties;
using System.Reflection;
using System.Collections.Concurrent;

namespace GoMaM
{
    /// <summary>
    /// Interaction logic for Window1.xaml
    /// </summary>
    public partial class MainWin : Window
    {
        private Mutex singleInstanceMutex;
        private Processor processor;
        private Group selectedGroup = null;
        private Manager selectedManager = null;
        private Client selectedClient = null;
        private Client selectedClientGr = null;
        private bool bLogsVisible = false;
        private int timeZoneOffset = 3; // time zone offset from UTC
        private DispatcherTimer timerManagers = null;
        private DispatcherTimer timerGroups = null;
        private bool groupsCheckStatusOK = true;
        private bool managersCheckStatusOK = true;

        private Dictionary<string, Group> groupsLocalCopy = new Dictionary<string, Group>();
        private Dictionary<int, Manager> managersLocalCopy = new Dictionary<int, Manager>();
        private ObservableCollection<LogInfo> logs = new ObservableCollection<LogInfo>();
       
        public MainWin()
        {
            CheckForMMultiInstance();
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                timeZoneOffset = Properties.Settings.Default.TimeZoneOffset;

                processor = new Processor(timeZoneOffset, Settings.Default.LogFilePath);
                processor.SetTradesSyncInterval(Properties.Settings.Default.TradesSyncInterval);
                processor.SetAcceptedTradesSyncInterval(Properties.Settings.Default.AcceptedTradesSyncInterval);
                processor.SetMaxAttempts(Properties.Settings.Default.MaxAttempts);
                processor.OnConnectionChanged += new Action<ConnType, bool>(processor_OnConnectionChanged);
                processor.OnLog += new Action<DateTime, string, string, MsgType>(processor_OnLog);

                processor.SetSymbolTransform_Manager_Accounts(1, Properties.Settings.Default.SymbolTransform1_Manager.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
                processor.SetSymbolTransform_Client_Accounts(1, Properties.Settings.Default.SymbolTransform1_Client.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
                processor.SetSymbolTransform_Manager_Accounts(2, Properties.Settings.Default.SymbolTransform2_Manager.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
                processor.SetSymbolTransform_Client_Accounts(2, Properties.Settings.Default.SymbolTransform2_Client.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));

                processor.SetVolumeTransform_Client_Accounts(1, Properties.Settings.Default.VolumeTransform1_Client.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
               
                processor.SetPhoneNumbersForSMS(Properties.Settings.Default.PhoneNumbers_SMS);

                processor.DelayOpenTrades = Properties.Settings.Default.DelayOpenTrades;
                processor.DelayOpenTradesIncrement = Properties.Settings.Default.DelayOpenTradesIncrement;
                processor.DelayCloseTrades = Properties.Settings.Default.DelayCloseTrades;


                processor.Initialize(Properties.Settings.Default.DBConnStr, Properties.Settings.Default.DBConnStr_MT4);

                groupsLocalCopy = processor.GetGroups();
                managersLocalCopy = processor.GetManagers();
                gridLogs.ItemsSource = logs;
                UpdateManagersGrid();
                UpdateGroupsGrid();

                ThreadPool.QueueUserWorkItem(new WaitCallback(Connect), null);

                timerManagers = new DispatcherTimer();
                timerManagers.Interval = TimeSpan.FromHours(1);
                timerManagers.Tick += new EventHandler(UpdateManagersGrid);
                timerManagers.Start();

                timerGroups = new DispatcherTimer();
                timerGroups.Interval = TimeSpan.FromHours(1);
                timerGroups.Tick += new EventHandler(UpdateGroupsGrid);
                timerGroups.Start();
            }
            catch (Exception ex)
            {
                AddLog(DateTime.UtcNow.AddHours(timeZoneOffset), "GUI", ex.Message, MsgType.ERROR);
            }
        }
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (timerManagers != null) { timerManagers.Stop(); }
            SecurityWindow secWin = new SecurityWindow();
            secWin.Owner = this;
            if (secWin.ShowDialog() == false || secWin.psw != "113355")
            {
                e.Cancel = true;
            }
            if (processor != null) { Disconnect(null);  processor.Deinit(); }

        }
        private void menuConnect_Click(object sender, RoutedEventArgs e)
        {
            SecurityWindow secWin = new SecurityWindow();
            secWin.Owner = this;
            if (secWin.ShowDialog() == true && secWin.psw == "113355")
                ThreadPool.QueueUserWorkItem(new WaitCallback(Connect), null);
        }
        private void menuDisconnect_Click(object sender, RoutedEventArgs e)
        {
            SecurityWindow secWin = new SecurityWindow();
            secWin.Owner = this;
            if (secWin.ShowDialog() == true && secWin.psw == "113355")
                ThreadPool.QueueUserWorkItem(new WaitCallback(Disconnect), null);
        }
        private void menuExit_Click(object sender, RoutedEventArgs e)
        {
            SecurityWindow secWin = new SecurityWindow();
            secWin.Owner = this;
            if (secWin.ShowDialog() == true && secWin.psw == "113355")
            {
                Thread thread = new Thread(() => { Disconnect(null); });
                thread.Start();

                if (!thread.Join(TimeSpan.FromSeconds(5)))
                {
                    thread.Abort(); // This is an unsafe operation so use as a last resort.
                    this.Close();
                }
            }
        }
        private void menuSync_Click(object sender, RoutedEventArgs e)
        {
            processor.ProcessTradesSync(1);
        }

        void processor_OnLog(DateTime time, string source, string msg, MsgType msgType)
        {
            AddLog(time,source,msg,msgType);
            //WriteGuiLogs(msg);
        }
        void processor_OnConnectionChanged(ConnType connType, bool bConnected)
        {
            SetConnectionState(connType, bConnected);
            /*
            if (bConnected && connType == ConnType.DIRECT)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateManagersGrid();
                    UpdateGroupsGrid();
                }));
                
            }
            */


            /*
            if (bConnected)
            {
                if (connType == ConnType.PUMP)
                    AddLog(DateTime.UtcNow.AddHours(timeZoneOffset), "MTManager", "Pump connected", MsgType.INFO);
                else
                {
                    AddLog(DateTime.UtcNow.AddHours(timeZoneOffset), "MTManager", "Direct connected", MsgType.INFO);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        UpdateManagersGrid();
                        UpdateGroupsGrid();
                    }));
                }
            }
            else
            {
                if (connType == ConnType.PUMP)
                    AddLog(DateTime.UtcNow.AddHours(timeZoneOffset), "MTManager", "Pump lost connection", MsgType.INFO);
                else
                    AddLog(DateTime.UtcNow.AddHours(timeZoneOffset), "MTManager", "Direct lost connection", MsgType.INFO);
            }*/
        }

        private void Connect(object state)
        {
            processor.Connect(Settings.Default.MT4ServerAddress, Settings.Default.ManagerLogin, Settings.Default.ManagerPassword);
        }
        private void Disconnect(object state)
        {
            processor.Disconnect();
        }

        private void SetConnectionState(ConnType connType, bool bConnected)
        {
            this.Dispatcher.Invoke(new Action(() =>
            {
                if (connType == ConnType.DIRECT)
                {
                    if (bConnected)
                    {
                        imgdirectConnected.Visibility = Visibility.Visible;
                        imgDirectDisconnected.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        imgdirectConnected.Visibility = Visibility.Collapsed;
                        imgDirectDisconnected.Visibility = Visibility.Visible;
                    }
                }
                else
                {
                    if (bConnected)
                    {
                        imgPumpConnected.Visibility = Visibility.Visible;
                        imgPumpDisconnected.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        imgPumpConnected.Visibility = Visibility.Collapsed;
                        imgPumpDisconnected.Visibility = Visibility.Visible;
                    }
                }
            }));
        }
        /*private string CreateLogForOrderInfo(OrderInfo orderInfo)
        {
            StringBuilder sb = new StringBuilder("[Login:");
            sb.Append(orderInfo.Login);
            sb.Append(" | Ticket:");
            sb.Append(orderInfo.Ticket);
            sb.Append(" | Time:");
            sb.Append(orderInfo.Time);
            sb.Append(" | Cmd:");
            sb.Append(orderInfo.Side);
            sb.Append(" | Volume:");
            sb.Append(orderInfo.Volume);
            sb.Append(" | Symbol:");
            sb.Append(orderInfo.Symbol);
            sb.Append(" | Price:");
            sb.Append(orderInfo.Price);
            sb.Append(" | NewTicket:");
            sb.Append(orderInfo.NewTicket);
            sb.Append(" | Comment:");
            sb.Append(orderInfo.Comment);
            sb.Append("]");
            return sb.ToString();
        }*/
        private void AddLog(DateTime time, string source, string msg, MsgType msgType)
        {
            try
            {
                LogInfo newLog = new LogInfo() { Type = msgType, Time = time, Source = source, Msg = msg };
                this.Dispatcher.Invoke(new Action(() =>
                    {
                        logs.Add(newLog);
                        if (logs.Count > 100)
                            logs.RemoveAt(0);

                        if (newLog.Type == MsgType.ERROR && !bLogsVisible)
                        {
                            if (imgBug.Visibility != Visibility.Visible)
                                imgBug.Visibility = Visibility.Visible;
                        }
                    }));
            }
            catch(Exception ex){
                throw ex;
            }
        }
       
        private void btnAddManager_Click(object sender, RoutedEventArgs e)
        {
            AddManagerDialog amd = new AddManagerDialog(processor);
            amd.Owner = this;
            amd.ShowDialog();
            //if a manager was added
            if (amd.DialogResult == true)
            {
                managersLocalCopy =  processor.GetManagers();
                UpdateManagersGrid();

            }
        }
        private void btnRemoveManager_Click(object sender, RoutedEventArgs e)
        {
            if (selectedManager != null)
            {
                RemoveManagerDialog rmd = new RemoveManagerDialog(selectedManager);
                rmd.Owner = this;
                rmd.ShowDialog();
                if (rmd.DialogResult == true)
                {
                    if (processor.RemoveManager(selectedManager.Login))
                    {
                        managersLocalCopy = processor.GetManagers();
                        UpdateManagersGrid();
                        UpdateClientsGrid();
                    }
                }
            }
        }
        private void btnRefreshManager_Click(object sender, RoutedEventArgs e)
        {
            managersLocalCopy = processor.GetManagers();
            UpdateManagersGrid();
            UpdateClientsGrid();
        }

        private void btnRefreshMultiple_Click(object sender, RoutedEventArgs e)
        {
            RefreshMultipleDialog dlg = new RefreshMultipleDialog(processor, selectedManager);
            dlg.Owner = this;
            dlg.ShowDialog();
            //if a manager was added / refreshed
            if (dlg.DialogResult == true)
            {
                managersLocalCopy = processor.GetManagers();
                UpdateManagersGrid();
                UpdateClientsGrid();
            }
        }
        
        private void btnAddClient_Click(object sender, RoutedEventArgs e)
        {
            if (selectedManager != null)
            {
                int prevSelectedManagerLogin = selectedManager.Login;
                AddClientDialog acd = new AddClientDialog(processor, selectedManager);
                acd.Owner = this;
                acd.ShowDialog();
                if (acd.DialogResult == true)
                {
                    managersLocalCopy = processor.GetManagers();
                    UpdateManagersGrid();
                    gridManagers.SelectedItem = managersLocalCopy[prevSelectedManagerLogin];
                    UpdateClientsGrid();
                }
            }
        }
        private void btnRemoveClient_Click(object sender, RoutedEventArgs e)
        {
            if (selectedManager != null && selectedClient != null)
            {
                RemoveClientDialog rcd = new RemoveClientDialog(selectedClient, selectedManager);
                rcd.Owner = this;
                rcd.ShowDialog();
                if (rcd.DialogResult == true)
                {
                    if (processor.RemoveClient(selectedManager.Login, selectedClient.Login))
                    {
                        int prevSelectedManagerLogin = selectedManager.Login;
                        managersLocalCopy = processor.GetManagers();
                        
                        UpdateManagersGrid();
                        gridManagers.SelectedItem = managersLocalCopy[prevSelectedManagerLogin];
                        UpdateClientsGrid();
                    }
                }
            }
        }
        private void btnEditClient_Click(object sender, RoutedEventArgs e)
        {
            EditClientDialog ecd = new EditClientDialog(processor,selectedManager,selectedClient);
            ecd.Owner = this;
            ecd.ShowDialog();
            if (ecd.DialogResult == true)
            {
                int prevSelectedManagerLogin = selectedManager.Login;
                managersLocalCopy = processor.GetManagers();
                UpdateManagersGrid();
                gridManagers.SelectedItem = managersLocalCopy[prevSelectedManagerLogin];
                UpdateClientsGrid();
            }
        }

        private void gridManagers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            selectedManager = gridManagers.SelectedItem as Manager;
            bool bEnable = false;
            if (selectedManager != null)
                bEnable = true;
               
            btnRemoveManager.IsEnabled = bEnable;
            btnAddClient.IsEnabled = bEnable;
            UpdateClientsGrid();
        }
        private void gridManagers_LoadingRow(object sender, Microsoft.Windows.Controls.DataGridRowEventArgs e)
        {
            var row = e.Row;
            if (((Manager)row.DataContext).CheckClients(processor))
            {
                row.Background = new SolidColorBrush(Colors.White);
            }
            else
            {
                row.Background = new SolidColorBrush(Colors.LightPink);
                managersCheckStatusOK = false;
            }
        }
        private void gridManagers_Loaded(object sender, RoutedEventArgs e)
        {
            if (managersCheckStatusOK)
            {
                imgCheckStatusOK.Visibility = System.Windows.Visibility.Visible;
                imgCheckStatusError.Visibility = System.Windows.Visibility.Hidden;
            }
            else
            {
                imgCheckStatusOK.Visibility = System.Windows.Visibility.Hidden;
                imgCheckStatusError.Visibility = System.Windows.Visibility.Visible;
            }
            managersCheckStatusOK = true;
        }

        private void gridClients_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            selectedClient = gridClients.SelectedItem as Client;
            bool bEnable = false;
            if (selectedClient != null)
                bEnable = true;

            btnRemoveClient.IsEnabled = bEnable;
            btnEditClient.IsEnabled = bEnable;
        }

        private void btnAddGroup_Click(object sender, RoutedEventArgs e)
        {
            var amd = new AddGroupDialog(processor);
            amd.Owner = this;
            amd.ShowDialog();
            //if a group was added
            if (amd.DialogResult == true)
            {
                groupsLocalCopy = processor.GetGroups();
                UpdateGroupsGrid();

            }
        }
        private void btnRemoveGroup_Click(object sender, RoutedEventArgs e)
        {
            if (selectedGroup != null)
            {
                var rmd = new RemoveGroupDialog(selectedGroup);
                rmd.Owner = this;
                rmd.ShowDialog();
                if (rmd.DialogResult == true)
                {
                    if (processor.RemoveGroup(selectedGroup.Name))
                    {
                        groupsLocalCopy = processor.GetGroups();
                        UpdateGroupsGrid();
                        UpdateClientsGrGrid();
                    }
                }
            }
        }
        private void btnRefreshGroup_Click(object sender, RoutedEventArgs e)
        {
            groupsLocalCopy = processor.GetGroups();
            UpdateGroupsGrid();
            UpdateClientsGrGrid();
        }

        private void btnAddClientGr_Click(object sender, RoutedEventArgs e)
        {
            if (selectedGroup != null)
            {
                string prevSelectedGroup = selectedGroup.Name;
                var acd = new AddClientGrDialog(processor, selectedGroup);
                acd.Owner = this;
                acd.ShowDialog();
                if (acd.DialogResult == true)
                {
                    groupsLocalCopy = processor.GetGroups();
                    UpdateGroupsGrid();
                    gridGroups.SelectedItem = groupsLocalCopy[prevSelectedGroup];
                    UpdateClientsGrGrid();
                }
            }
        }
        private void btnRemoveClientGr_Click(object sender, RoutedEventArgs e)
        {
            if (selectedGroup != null && selectedClientGr != null)
            {
                RemoveClientGrDialog rcd = new RemoveClientGrDialog(selectedClientGr, selectedGroup);
                rcd.Owner = this;
                rcd.ShowDialog();
                if (rcd.DialogResult == true)
                {
                    if (processor.RemoveClientGr(selectedGroup.Name, selectedClientGr.Login))
                    {
                        string prevSelectedGroupName = selectedGroup.Name;
                        groupsLocalCopy = processor.GetGroups();

                        UpdateGroupsGrid();
                        gridGroups.SelectedItem = groupsLocalCopy[prevSelectedGroupName];
                        UpdateClientsGrGrid();
                    }
                }
            }
        }
        private void btnEditClientGr_Click(object sender, RoutedEventArgs e)
        {
            EditClientGrDialog ecd = new EditClientGrDialog(processor, selectedGroup, selectedClientGr);
            ecd.Owner = this;
            ecd.ShowDialog();
            if (ecd.DialogResult == true)
            {
                string prevSelectedGroupName = selectedGroup.Name;
                groupsLocalCopy = processor.GetGroups();
                UpdateGroupsGrid();
                gridGroups.SelectedItem = groupsLocalCopy[prevSelectedGroupName];
                UpdateClientsGrGrid();
            }
        }
        private void btnSyncOpenTrades_Click(object sender, RoutedEventArgs e)
        {
            if (selectedGroup != null && selectedClientGr != null)
            {
                if (processor != null)
                {
                    processor.ProcessOpenTradesForNewClient(selectedGroup.Name, selectedClientGr.Login, selectedClientGr.Multiplier);
                    MessageBox.Show("Sync done.");
                }
            }
        }



        private void gridGroups_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            selectedGroup = gridGroups.SelectedItem as Group;
            bool bEnable = false;
            if (selectedGroup != null)
                bEnable = true;

            btnRemoveGroup.IsEnabled = bEnable;
            btnAddClientGr.IsEnabled = bEnable;
            UpdateClientsGrGrid();
            //UpdateSelectedManagerTrades();
            //UpdateClientsPositions();
        }
        private void gridGroups_LoadingRow(object sender, Microsoft.Windows.Controls.DataGridRowEventArgs e)
        {
            var row = e.Row;
            if (((Group)row.DataContext).CheckClients(processor))
            {
                row.Background = new SolidColorBrush(Colors.White);
            }
            else
            {
                row.Background = new SolidColorBrush(Colors.LightPink);
                groupsCheckStatusOK = false;
            }
        }
        private void gridGroups_Loaded(object sender, RoutedEventArgs e)
        {
            if (groupsCheckStatusOK)
            {
                imgCheckGroupStatusOK.Visibility = System.Windows.Visibility.Visible;
                imgCheckGroupStatusError.Visibility = System.Windows.Visibility.Hidden;
            }
            else
            {
                imgCheckGroupStatusOK.Visibility = System.Windows.Visibility.Hidden;
                imgCheckGroupStatusError.Visibility = System.Windows.Visibility.Visible;
            }
            groupsCheckStatusOK = true;
        }

        private void gridClientsGr_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            selectedClientGr = gridClientsGr.SelectedItem as Client;
            bool bEnable = false;
            if (selectedClientGr != null)
                bEnable = true;

            btnRemoveClientGr.IsEnabled = bEnable;
            btnEditClientGr.IsEnabled = bEnable;
        }
        
        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                TabItem selectedItem = e.AddedItems[0] as TabItem;
                if (selectedItem == logsTabItem)
                    ResetLogsBugAlert();
                else
                    bLogsVisible = false;
            }
        }
        
        private void UpdateManagersGrid(object sender, EventArgs e)
        {
            UpdateManagersGrid();
        }
        private void UpdateManagersGrid()
        {

            managersCheckStatusOK = true;
            gridManagers.ItemsSource = null;
            gridManagers.Items.Refresh();
            gridManagers.UpdateLayout();
            if (managersLocalCopy != null)
            {
                gridManagers.ItemsSource = managersLocalCopy.Values;
                gridManagers.UpdateLayout();
            }
        }
        private void UpdateClientsGrid()
        {
            gridClients.ItemsSource = null;
            if (selectedManager != null)
            {
                gridClients.ItemsSource = selectedManager.Clients.Values;
                gridClients.UpdateLayout();
            }
        }

        private void UpdateGroupsGrid(object sender, EventArgs e)
        {
            UpdateGroupsGrid();
        }
        private void UpdateGroupsGrid()
        {
            groupsCheckStatusOK = true;
            gridGroups.ItemsSource = null;
            gridGroups.Items.Refresh();
            gridGroups.UpdateLayout();
            if (groupsLocalCopy != null)
            {
                gridGroups.ItemsSource = groupsLocalCopy.Values;
                gridGroups.UpdateLayout();
            }
        }
        private void UpdateClientsGrGrid()
        {
            gridClientsGr.ItemsSource = null;
            if (selectedGroup != null)
            {
                gridClientsGr.ItemsSource = selectedGroup.Clients.Values;
                gridClientsGr.UpdateLayout();
            }
        }


        private void ResetLogsBugAlert()
        {
            bLogsVisible = true;
            imgBug.Visibility = Visibility.Hidden;
        }
        private void CheckForMMultiInstance()
        {
            // Check for existing instance of your Application
            string mutexName =  Properties.Settings.Default.AppName + "_MutexInstanceName";
            bool createdNew;
            singleInstanceMutex = new Mutex(true, mutexName, out createdNew);
            //A running instance 
            if (!createdNew)
            {
                MessageBox.Show("There is an already open instance", "Multiple Application Instances Warning");
                this.Close();
            }
        }

        private void chkEnableExtendedLogging_Checked(object sender, RoutedEventArgs e)
        {
            if(processor != null)
                processor.ExtendedLog = true;
        }

        private void chkEnableExtendedLogging_Unchecked(object sender, RoutedEventArgs e)
        {
            if (processor != null)
                processor.ExtendedLog = false;
        }
    }

    public class UserData
    {
        public int Login { get; set; }
        public string Name { get; set; }
    }
    public class managerGridData : UserData
    {
        public int UserCount { get; set; }
    }
    public class clientGridData : UserData
    {
        public int ManagerLogin { get; set; }
        public double Multiplier { get; set; }
    }
    public class LogInfo
    {
        public MsgType Type { get; set; }
        public DateTime Time { get; set; }
        public string Source { get; set; }
        public string Msg { get; set; }
    }
}
